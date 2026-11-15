using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Domain.Accounts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Accounts;

public sealed class FinancialAccountStore(
    FinanceFlowDbContext db,
    DbContextOptions<FinanceFlowDbContext> options) : IFinancialAccountStore
{
    // Snapshot v1 is persisted as JSON and sent unchanged on both the first response and replay.
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private static readonly Expression<Func<FinancialAccount, FinancialAccountView>> Projection = account => new(
        account.Id,
        account.Name,
        account.Type,
        account.Currency,
        account.OpeningBalance,
        account.OpeningDate,
        account.IsActive,
        account.CreatedAtUtc,
        account.UpdatedAtUtc,
        Convert.ToBase64String(account.RowVersion));

    public Task<FinancialAccountView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.FinancialAccounts.AsNoTracking()
            .Where(account => account.UserId == userId && account.Id == id)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<AccountPage> ListAsync(Guid userId, AccountQuery query, CancellationToken cancellationToken)
    {
        var accounts = db.FinancialAccounts.AsNoTracking().Where(account => account.UserId == userId);
        if (query.IsActive is { } isActive)
        {
            accounts = accounts.Where(account => account.IsActive == isActive);
        }

        if (query.Type is { } type)
        {
            accounts = accounts.Where(account => account.Type == type);
        }

        var totalCount = await accounts.CountAsync(cancellationToken);
        var orderedAccounts = (query.Sort, query.Direction) switch
        {
            ("id", "asc") => accounts.OrderBy(x => x.Id),
            ("id", "desc") => accounts.OrderByDescending(x => x.Id),
            ("name", "desc") => accounts.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            _ => accounts.OrderBy(x => x.Name).ThenBy(x => x.Id)
        };
        var items = await orderedAccounts
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(Projection)
            .ToListAsync(cancellationToken);

        var totalPages = (int)Math.Ceiling((decimal)totalCount / query.PageSize);
        return new(items, query.Page, query.PageSize, totalCount, totalPages);
    }

    public async Task<AccountCreationResult> CreateAsync(
        FinancialAccount account,
        string key,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var existing = await FindRecordAsync(db, account.UserId, key, cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, requestHash);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            db.FinancialAccounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);

            // SaveChanges supplies the SQL rowversion before the immutable snapshot is captured.
            var record = CreateRecord(account, key, requestHash);
            db.IdempotencyRecords.Add(record);
            await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return Replay(record, requestHash);
        }
        catch (DbUpdateException exception) when (IsIdempotencyCollision(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            // A failed transaction/tracker is never reused to decide the winner.
            await using var recoveryContext = new FinanceFlowDbContext(options);
            var winner = await FindRecordAsync(recoveryContext, account.UserId, key, cancellationToken);
            return winner is null ? new(Error: AccountFailure.Unavailable) : Replay(winner, requestHash);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 1205 or -2 })
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(Error: AccountFailure.Unavailable);
        }
    }

    public Task<FinancialAccount?> FindForUpdateAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.FinancialAccounts.SingleOrDefaultAsync(account => account.UserId == userId && account.Id == id, cancellationToken);

    public async Task<AccountChangeResult> SaveUpdateAsync(
        FinancialAccount account, byte[] expectedVersion, CancellationToken cancellationToken)
    {
        var entry = db.Entry(account);
        if (entry.State == EntityState.Detached)
        {
            throw new InvalidOperationException("Load the account through this store before updating it.");
        }

        // The client's version becomes the SQL UPDATE precondition, not just a memory comparison.
        entry.Property(account => account.RowVersion).OriginalValue = expectedVersion.ToArray();
        // Force a conditional write even for a repeated command or an unchanged test clock.
        entry.Property(account => account.UpdatedAtUtc).IsModified = true;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            entry.State = EntityState.Detached;
            var stillOwned = await db.FinancialAccounts.AsNoTracking()
                .AnyAsync(current => current.Id == account.Id && current.UserId == account.UserId, cancellationToken);
            return new(Error: stillOwned ? AccountFailure.VersionConflict : AccountFailure.NotFound);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 1205 or -2 })
        {
            return new(Error: AccountFailure.Unavailable);
        }

        return new(Version: Convert.ToBase64String(account.RowVersion));
    }

    private static IdempotencyRecord CreateRecord(FinancialAccount account, string key, byte[] requestHash)
    {
        var snapshot = new FinancialAccountView(
            account.Id,
            account.Name,
            account.Type,
            account.Currency,
            account.OpeningBalance,
            account.OpeningDate,
            account.IsActive,
            account.CreatedAtUtc,
            account.UpdatedAtUtc,
            Convert.ToBase64String(account.RowVersion));

        return new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            Operation = AccountCreationFingerprint.Operation,
            Key = key,
            RequestHash = requestHash.ToArray(),
            StatusCode = 201,
            ResponseBody = JsonSerializer.Serialize(snapshot, SnapshotJson),
            Location = "/api/v1/accounts/" + account.Id.ToString("D"),
            ETag = '"' + snapshot.Version + '"',
            ContentType = "application/json; charset=utf-8",
            CreatedAtUtc = account.CreatedAtUtc
        };
    }

    private static Task<IdempotencyRecord?> FindRecordAsync(
        FinanceFlowDbContext context,
        Guid userId,
        string key,
        CancellationToken cancellationToken) =>
        context.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(record => record.UserId == userId
                && record.Operation == AccountCreationFingerprint.Operation
                && record.Key == key, cancellationToken);

    private static AccountCreationResult Replay(IdempotencyRecord record, byte[] requestHash)
    {
        if (!CryptographicOperations.FixedTimeEquals(record.RequestHash, requestHash))
        {
            return new(Error: AccountFailure.IdempotencyConflict);
        }

        return new(Response: new(record.StatusCode, record.ResponseBody, record.Location, record.ETag, record.ContentType));
    }

    private static bool IsIdempotencyCollision(DbUpdateException exception) =>
        exception.Entries.Count == 1 && exception.Entries[0].Entity is IdempotencyRecord
        && exception.InnerException is SqlException sql
        && sql.Errors.Cast<SqlError>().Any(error => error.Number == 2601
            && error.Message.StartsWith(
                "Cannot insert duplicate key row in object 'dbo.IdempotencyRecords' with unique index '"
                    + IdempotencyRecordConfiguration.UniqueIndex + "'.",
                StringComparison.Ordinal));
}
