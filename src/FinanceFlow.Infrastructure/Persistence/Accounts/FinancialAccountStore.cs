using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Domain.Accounts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Accounts;

public sealed class FinancialAccountStore(FinanceFlowDbContext db, DbContextOptions<FinanceFlowDbContext> options) : IFinancialAccountStore
{
    // Snapshot v1 is persisted as JSON and sent unchanged on both the first response and replay.
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private static readonly Expression<Func<FinancialAccount, FinancialAccountView>> Projection = account => new(
        account.Id, account.Name, account.Type, account.Currency, account.OpeningBalance, account.OpeningDate,
        account.IsActive, account.CreatedAtUtc, account.UpdatedAtUtc, Convert.ToBase64String(account.RowVersion));

    public Task<FinancialAccountView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.FinancialAccounts.AsNoTracking().Where(x => x.UserId == userId && x.Id == id)
            .Select(Projection).SingleOrDefaultAsync(cancellationToken);

    public async Task<AccountPage> ListAsync(Guid userId, AccountQuery query, CancellationToken cancellationToken)
    {
        var accounts = db.FinancialAccounts.AsNoTracking().Where(x => x.UserId == userId);
        if (query.IsActive is { } active) accounts = accounts.Where(x => x.IsActive == active);
        if (query.Type is { } type) accounts = accounts.Where(x => x.Type == type);
        var count = await accounts.CountAsync(cancellationToken);
        var ordered = (query.Sort, query.Direction) switch
        {
            ("id", "asc") => accounts.OrderBy(x => x.Id),
            ("id", "desc") => accounts.OrderByDescending(x => x.Id),
            ("name", "desc") => accounts.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            _ => accounts.OrderBy(x => x.Name).ThenBy(x => x.Id)
        };
        var items = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(Projection).ToListAsync(cancellationToken);
        return new(items, query.Page, query.PageSize, count, (int)Math.Ceiling((decimal)count / query.PageSize));
    }

    public async Task<AccountCreationResult> CreateAsync(FinancialAccount account, string key, byte[] requestHash, CancellationToken cancellationToken)
    {
        var existing = await FindRecordAsync(db, account.UserId, key, cancellationToken);
        if (existing is not null) return Replay(existing, requestHash);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            db.FinancialAccounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);
            var snapshot = new FinancialAccountView(account.Id, account.Name, account.Type, account.Currency,
                account.OpeningBalance, account.OpeningDate, account.IsActive, account.CreatedAtUtc,
                account.UpdatedAtUtc, Convert.ToBase64String(account.RowVersion));
            var record = new IdempotencyRecord
            {
                Id = Guid.NewGuid(), UserId = account.UserId, Operation = AccountCreationFingerprint.Operation,
                Key = key, RequestHash = requestHash.ToArray(), StatusCode = 201,
                ResponseBody = JsonSerializer.Serialize(snapshot, SnapshotJson),
                Location = "/api/v1/accounts/" + account.Id.ToString("D"),
                ETag = '"' + snapshot.Version + '"', ContentType = "application/json; charset=utf-8", CreatedAtUtc = account.CreatedAtUtc
            };
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
            await using var fresh = new FinanceFlowDbContext(options);
            var winner = await FindRecordAsync(fresh, account.UserId, key, cancellationToken);
            return winner is null ? new(Error: AccountFailure.Unavailable) : Replay(winner, requestHash);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 1205 or -2 })
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(Error: AccountFailure.Unavailable);
        }
    }

    private static Task<IdempotencyRecord?> FindRecordAsync(FinanceFlowDbContext context, Guid userId, string key, CancellationToken cancellationToken) =>
        context.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(record => record.UserId == userId
            && record.Operation == AccountCreationFingerprint.Operation && record.Key == key, cancellationToken);

    private static AccountCreationResult Replay(IdempotencyRecord record, byte[] hash) =>
        CryptographicOperations.FixedTimeEquals(record.RequestHash, hash)
            ? new(Response: new(record.StatusCode, record.ResponseBody, record.Location, record.ETag, record.ContentType))
            : new(Error: AccountFailure.IdempotencyConflict);

    private static bool IsIdempotencyCollision(DbUpdateException exception) =>
        exception.Entries.Count == 1 && exception.Entries[0].Entity is IdempotencyRecord
        && exception.InnerException is SqlException sql && sql.Errors.Cast<SqlError>().Any(error => error.Number == 2601
            && error.Message.StartsWith("Cannot insert duplicate key row in object 'dbo.IdempotencyRecords' with unique index '"
                + IdempotencyRecordConfiguration.UniqueIndex + "'.", StringComparison.Ordinal));
}
