using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceFlow.Application.Transactions;
using FinanceFlow.Domain.Transactions;
using FinanceFlow.Infrastructure.Persistence.Accounts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Transactions;

public sealed class FinancialTransactionStore(FinanceFlowDbContext db, DbContextOptions<FinanceFlowDbContext> options)
    : IFinancialTransactionStore
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private static readonly Expression<Func<FinancialTransaction, FinancialTransactionView>> Projection = value => new(
        value.Id, value.AccountId, value.CategoryId, value.Kind, value.Amount, value.OccurredOn, value.Description,
        value.CreatedAtUtc, value.UpdatedAtUtc, Convert.ToBase64String(value.RowVersion));

    public async Task<TransactionCreationResult> CreateAsync(FinancialTransaction value, string key, byte[] hash, CancellationToken cancellationToken)
    {
        var existing = await FindRecordAsync(db, value.UserId, key, cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, hash);
        }

        // RepeatableRead retains shared locks on the account/category until commit. An UPDATE
        // that deactivates either reference must finish before these reads, or wait for this write.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        try
        {
            var account = await db.FinancialAccounts.AsNoTracking()
                .SingleOrDefaultAsync(account => account.Id == value.AccountId && account.UserId == value.UserId, cancellationToken);
            var category = await db.Categories.AsNoTracking()
                .SingleOrDefaultAsync(category => category.Id == value.CategoryId && category.UserId == value.UserId, cancellationToken);

            // A winner may have committed while we waited on a reference being deactivated.
            existing = await FindRecordAsync(db, value.UserId, key, cancellationToken);
            if (existing is not null)
            {
                return Replay(existing, hash);
            }

            if (account is null || category is null)
            {
                return new(Error: TransactionFailure.NotFound);
            }

            var links = value.CheckLinks(account, category);
            if (links != TransactionLinkStatus.Valid)
            {
                return new(Error: links == TransactionLinkStatus.Inactive
                    ? TransactionFailure.InactiveReference : TransactionFailure.InvalidInput);
            }

            db.FinancialTransactions.Add(value);
            await db.SaveChangesAsync(cancellationToken);
            var record = CreateRecord(value, key, hash);
            db.IdempotencyRecords.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Replay(record, hash);
        }
        catch (DbUpdateException exception) when (IsIdempotencyCollision(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            await using var recovery = new FinanceFlowDbContext(options);
            var winner = await FindRecordAsync(recovery, value.UserId, key, cancellationToken);
            return winner is null ? new(Error: TransactionFailure.Unavailable) : Replay(winner, hash);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 1205 or -2 })
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(Error: TransactionFailure.Unavailable);
        }
        // Early returns or unexpected failures dispose/roll back the transaction. No partial success.
    }

    public async Task<TransactionChangeResult> UpdateAsync(Guid userId, Guid id, TransactionDetails details,
        byte[] expectedVersion, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Load optimistically before RepeatableRead: holding shared locks on the transaction row
        // while two editors try to upgrade them would cause deadlocks instead of rowversion conflicts.
        var value = await FindForChangeAsync(userId, id, cancellationToken);
        if (value is null)
        {
            return new(Error: TransactionFailure.NotFound);
        }
        if (!value.RowVersion.AsSpan().SequenceEqual(expectedVersion))
        {
            return new(Error: TransactionFailure.VersionConflict);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        try
        {
            // Source and destination are read in a consistent order and stay locked until commit.
            var accounts = await db.FinancialAccounts.AsNoTracking()
                .Where(account => account.UserId == userId && (account.Id == value.AccountId || account.Id == details.AccountId))
                .OrderBy(account => account.Id).ToListAsync(cancellationToken);
            var originalAccount = accounts.SingleOrDefault(account => account.Id == value.AccountId);
            var targetAccount = accounts.SingleOrDefault(account => account.Id == details.AccountId);
            var category = await db.Categories.AsNoTracking()
                .SingleOrDefaultAsync(category => category.UserId == userId && category.Id == details.CategoryId, cancellationToken);
            if (originalAccount is null || targetAccount is null || category is null)
            {
                return new(Error: TransactionFailure.NotFound);
            }

            var links = value.UpdateDetails(details, originalAccount, targetAccount, category, today, now);
            if (links != TransactionLinkStatus.Valid)
            {
                return new(Error: links == TransactionLinkStatus.Inactive
                    ? TransactionFailure.InactiveReference : TransactionFailure.InvalidInput);
            }

            await SaveChangeAsync(value, expectedVersion, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(Version: Convert.ToBase64String(value.RowVersion));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return await ConcurrencyFailureAsync(value, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 1205 or -2 })
        {
            // Disposal rolls back the whole unit, including when SQL already aborted a deadlock victim.
            return new(Error: TransactionFailure.Unavailable);
        }
    }

    public async Task<TransactionChangeResult> DeleteAsync(Guid userId, Guid id, byte[] expectedVersion,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var value = await FindForChangeAsync(userId, id, cancellationToken);
        if (value is null)
        {
            return new(Error: TransactionFailure.NotFound);
        }
        if (!value.RowVersion.AsSpan().SequenceEqual(expectedVersion))
        {
            return new(Error: TransactionFailure.VersionConflict);
        }

        // Deletion does not validate activity or lock reference rows. Keep an explicit local unit
        // so a failure after SaveChanges but before commit still rolls back the logical deletion.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            value.Delete(now);
            await SaveChangeAsync(value, expectedVersion, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(Version: Convert.ToBase64String(value.RowVersion));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return await ConcurrencyFailureAsync(value, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 1205 or -2 })
        {
            return new(Error: TransactionFailure.Unavailable);
        }
    }

    private Task<FinancialTransaction?> FindForChangeAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.FinancialTransactions.SingleOrDefaultAsync(value => value.UserId == userId && value.Id == id && !value.IsDeleted, cancellationToken);

    private Task<int> SaveChangeAsync(FinancialTransaction value, byte[] expectedVersion, CancellationToken cancellationToken)
    {
        var entry = db.Entry(value);
        // The early check only sets error precedence. This is the actual SQL write precondition.
        entry.Property(transaction => transaction.RowVersion).OriginalValue = expectedVersion.ToArray();
        entry.Property(transaction => transaction.UpdatedAtUtc).IsModified = true;
        return db.SaveChangesAsync(cancellationToken);
    }

    private async Task<TransactionChangeResult> ConcurrencyFailureAsync(FinancialTransaction value, CancellationToken cancellationToken)
    {
        db.Entry(value).State = EntityState.Detached;
        var exists = await db.FinancialTransactions.AsNoTracking()
            .AnyAsync(current => current.Id == value.Id && current.UserId == value.UserId && !current.IsDeleted, cancellationToken);
        return new(Error: exists ? TransactionFailure.VersionConflict : TransactionFailure.NotFound);
    }

    public Task<FinancialTransactionView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.FinancialTransactions.AsNoTracking().Where(value => value.UserId == userId && value.Id == id && !value.IsDeleted)
            .Select(Projection).SingleOrDefaultAsync(cancellationToken);

    public async Task<TransactionListResult> ListAsync(Guid userId, TransactionQuery query, CancellationToken cancellationToken)
    {
        if ((query.AccountId is { } accountId && !await db.FinancialAccounts.AnyAsync(account => account.Id == accountId && account.UserId == userId, cancellationToken))
            || (query.CategoryId is { } categoryId && !await db.Categories.AnyAsync(category => category.Id == categoryId && category.UserId == userId, cancellationToken)))
        {
            return new(Error: TransactionFailure.NotFound);
        }

        var transactions = db.FinancialTransactions.AsNoTracking().Where(value => value.UserId == userId && !value.IsDeleted);
        if (query.AccountId is { } account)
        {
            transactions = transactions.Where(value => value.AccountId == account);
        }
        if (query.CategoryId is { } category)
        {
            transactions = transactions.Where(value => value.CategoryId == category);
        }
        if (query.Kind is { } kind)
        {
            transactions = transactions.Where(value => value.Kind == kind);
        }
        if (query.From is { } from)
        {
            transactions = transactions.Where(value => value.OccurredOn >= from);
        }
        if (query.To is { } to)
        {
            transactions = transactions.Where(value => value.OccurredOn <= to);
        }
        if (query.MinAmount is { } min)
        {
            transactions = transactions.Where(value => value.Amount >= min);
        }
        if (query.MaxAmount is { } max)
        {
            transactions = transactions.Where(value => value.Amount <= max);
        }

        var count = await transactions.CountAsync(cancellationToken);
        var ordered = (query.Sort, query.Direction) switch
        {
            ("id", "asc") => transactions.OrderBy(value => value.Id),
            ("id", "desc") => transactions.OrderByDescending(value => value.Id),
            ("amount", "asc") => transactions.OrderBy(value => value.Amount).ThenBy(value => value.Id),
            ("amount", "desc") => transactions.OrderByDescending(value => value.Amount).ThenBy(value => value.Id),
            ("occurredOn", "asc") => transactions.OrderBy(value => value.OccurredOn).ThenBy(value => value.Id),
            _ => transactions.OrderByDescending(value => value.OccurredOn).ThenBy(value => value.Id)
        };
        var items = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).Select(Projection).ToListAsync(cancellationToken);
        return new(Page: new(items, query.Page, query.PageSize, count, (int)Math.Ceiling((decimal)count / query.PageSize)));
    }

    public async Task<AccountBalance?> BalanceAsync(Guid userId, Guid accountId, DateOnly asOf, CancellationToken cancellationToken)
    {
        // One SQL command for account and aggregate. Keep the SUM separate from OpeningBalance:
        // SQL decimal(38,2) + decimal(18,2) can reduce the result scale; add exactly in C# instead.
        var position = await db.FinancialAccounts.AsNoTracking().Where(account => account.Id == accountId && account.UserId == userId)
            .Select(account => new
            {
                account.Id, account.Currency, account.OpeningDate, account.OpeningBalance,
                Net = db.FinancialTransactions.Where(value => value.UserId == userId && value.AccountId == accountId
                        && !value.IsDeleted && value.OccurredOn <= asOf)
                    .Sum(value => (decimal?)(value.Kind == "income" ? value.Amount : -value.Amount)) ?? 0m
            }).SingleOrDefaultAsync(cancellationToken);

        return position is null ? null : new(position.Id, position.Currency, asOf,
            asOf < position.OpeningDate ? 0m : checked(position.OpeningBalance + position.Net));
    }

    private static IdempotencyRecord CreateRecord(FinancialTransaction value, string key, byte[] hash)
    {
        var snapshot = new FinancialTransactionView(value.Id, value.AccountId, value.CategoryId, value.Kind, value.Amount,
            value.OccurredOn, value.Description, value.CreatedAtUtc, value.UpdatedAtUtc, Convert.ToBase64String(value.RowVersion));
        return new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            UserId = value.UserId,
            Operation = TransactionCreationFingerprint.Operation,
            Key = key,
            RequestHash = hash.ToArray(),
            StatusCode = 201,
            ResponseBody = JsonSerializer.Serialize(snapshot, SnapshotJson),
            Location = "/api/v1/transactions/" + value.Id.ToString("D"),
            ETag = '"' + snapshot.Version + '"',
            ContentType = "application/json; charset=utf-8",
            CreatedAtUtc = value.CreatedAtUtc
        };
    }

    private static Task<IdempotencyRecord?> FindRecordAsync(FinanceFlowDbContext context, Guid userId, string key, CancellationToken cancellationToken) =>
        context.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(record => record.UserId == userId
            && record.Operation == TransactionCreationFingerprint.Operation && record.Key == key, cancellationToken);

    private static TransactionCreationResult Replay(IdempotencyRecord record, byte[] hash) =>
        CryptographicOperations.FixedTimeEquals(record.RequestHash, hash)
            ? new(Response: new(record.StatusCode, record.ResponseBody, record.Location, record.ETag, record.ContentType))
            : new(Error: TransactionFailure.IdempotencyConflict);

    private static bool IsIdempotencyCollision(DbUpdateException exception) =>
        exception.Entries.Count == 1 && exception.Entries[0].Entity is IdempotencyRecord
        && exception.InnerException is SqlException sql && sql.Errors.Cast<SqlError>().Any(error => error.Number == 2601
            && error.Message.StartsWith("Cannot insert duplicate key row in object 'dbo.IdempotencyRecords' with unique index '"
                + IdempotencyRecordConfiguration.UniqueIndex + "'.", StringComparison.Ordinal));
}
