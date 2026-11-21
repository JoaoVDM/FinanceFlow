using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Domain.Transfers;
using FinanceFlow.Infrastructure.Persistence.Accounts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Transfers;

public sealed class TransferStore(FinanceFlowDbContext db, DbContextOptions<FinanceFlowDbContext> options) : ITransferStore
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private static readonly Expression<Func<Transfer, TransferView>> Projection = value => new(
        value.Id, value.SourceAccountId, value.DestinationAccountId, value.Amount, value.OccurredOn,
        value.Description, value.CreatedAtUtc, value.UpdatedAtUtc, Convert.ToBase64String(value.RowVersion));

    public async Task<TransferCreationResult> CreateAsync(Transfer value, string key, byte[] hash, CancellationToken cancellationToken)
    {
        var existing = await FindRecordAsync(db, value.UserId, key, cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, hash);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        try
        {
            // The same account order is used in both directions; shared locks protect activity until commit.
            var accounts = await db.FinancialAccounts.AsNoTracking()
                .Where(account => account.UserId == value.UserId
                    && (account.Id == value.SourceAccountId || account.Id == value.DestinationAccountId))
                .OrderBy(account => account.Id).ToListAsync(cancellationToken);

            // A committed winner may have appeared while waiting for a reference lock.
            existing = await FindRecordAsync(db, value.UserId, key, cancellationToken);
            if (existing is not null)
            {
                return Replay(existing, hash);
            }
            var source = accounts.SingleOrDefault(account => account.Id == value.SourceAccountId);
            var destination = accounts.SingleOrDefault(account => account.Id == value.DestinationAccountId);
            if (source is null || destination is null)
            {
                return new(Error: TransferFailure.NotFound);
            }
            var links = value.CheckAccounts(source, destination);
            if (links != TransferAccountStatus.Valid)
            {
                return new(Error: links == TransferAccountStatus.Inactive ? TransferFailure.InactiveAccount : TransferFailure.InvalidInput);
            }

            db.Transfers.Add(value);
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
            return winner is null ? new(Error: TransferFailure.Unavailable) : Replay(winner, hash);
        }
        catch (Exception exception) when (IsTemporaryCreationFailure(exception))
        {
            // Disposal rolls back the unit; a deadlock victim may already be aborted by SQL Server.
            return new(Error: TransferFailure.Unavailable);
        }
    }

    public Task<TransferView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.Transfers.AsNoTracking().Where(value => value.Id == id && value.UserId == userId && !value.IsCanceled)
            .Select(Projection).SingleOrDefaultAsync(cancellationToken);

    private static IdempotencyRecord CreateRecord(Transfer value, string key, byte[] hash)
    {
        var snapshot = new TransferView(value.Id, value.SourceAccountId, value.DestinationAccountId, value.Amount,
            value.OccurredOn, value.Description, value.CreatedAtUtc, value.UpdatedAtUtc, Convert.ToBase64String(value.RowVersion));
        return new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            UserId = value.UserId,
            Operation = TransferCreationFingerprint.Operation,
            Key = key,
            RequestHash = hash.ToArray(),
            StatusCode = 201,
            ResponseBody = JsonSerializer.Serialize(snapshot, SnapshotJson),
            Location = "/api/v1/transfers/" + value.Id.ToString("D"),
            ETag = '"' + snapshot.Version + '"',
            ContentType = "application/json; charset=utf-8",
            CreatedAtUtc = value.CreatedAtUtc
        };
    }

    private static Task<IdempotencyRecord?> FindRecordAsync(FinanceFlowDbContext context, Guid userId, string key, CancellationToken cancellationToken) =>
        context.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(record => record.UserId == userId
            && record.Operation == TransferCreationFingerprint.Operation && record.Key == key, cancellationToken);

    private static TransferCreationResult Replay(IdempotencyRecord record, byte[] hash) =>
        CryptographicOperations.FixedTimeEquals(record.RequestHash, hash)
            ? new(Response: new(record.StatusCode, record.ResponseBody, record.Location, record.ETag, record.ContentType))
            : new(Error: TransferFailure.IdempotencyConflict);

    private static bool IsIdempotencyCollision(DbUpdateException exception) =>
        exception.Entries.Count == 1 && exception.Entries[0].Entity is IdempotencyRecord
        && exception.InnerException is SqlException sql && sql.Errors.Cast<SqlError>().Any(error => error.Number == 2601
            && error.Message.StartsWith("Cannot insert duplicate key row in object 'dbo.IdempotencyRecords' with unique index '"
                + IdempotencyRecordConfiguration.UniqueIndex + "'.", StringComparison.Ordinal));

    private static bool IsTemporaryCreationFailure(Exception exception)
    {
        if (exception is InvalidOperationException { InnerException: { } strategyFailure })
        {
            exception = strategyFailure;
        }
        if (exception is DbUpdateException { InnerException: { } updateFailure })
        {
            exception = updateFailure;
        }
        return exception is SqlException { Number: 1205 or -2 };
    }
}
