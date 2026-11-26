using System.Data;
using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace FinanceFlow.Infrastructure.Persistence;

// Owns only the multi-query Snapshot reads. It does not change the other stores or global pooling.
internal static class SnapshotReadSession
{
    public static async Task<T> RunAsync<T>(FinanceFlowDbContext db, Func<Task<T>> read, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("A snapshot read must own its transaction.");
        }

        var connection = (SqlConnection)db.Database.GetDbConnection();
        var ownsConnection = connection.State != ConnectionState.Open;
        var logger = db.GetService<ILoggerFactory>().CreateLogger(typeof(SnapshotReadSession).FullName!);
        IDbContextTransaction? transaction = null;
        ExceptionDispatchInfo? failure = null;
        var opened = false;
        var discard = false;
        var discarded = false;
        T result = default!;
        try
        {
            // EF respects an externally opened connection. Keep this physical session open
            // through commit/dispose and reset; only its owner returns it to the pool.
            if (ownsConnection)
            {
                await connection.OpenAsync(cancellationToken);
            }
            opened = true;
            transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
            result = await read();
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        // Cleanup is independent of an already-canceled request. Preserve the first failure,
        // including OperationCanceledException, instead of replacing it with a cleanup error.
        if (transaction is not null)
        {
            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
                discard = true;
            }
        }
        if (opened && !discard && connection.State == ConnectionState.Open)
        {
            var previousTimeout = db.Database.GetCommandTimeout();
            try
            {
                db.Database.SetCommandTimeout(5);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", CancellationToken.None);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
                discard = true;
            }
            finally
            {
                db.Database.SetCommandTimeout(previousTimeout);
            }
        }
        else if (opened || connection.State != ConnectionState.Closed)
        {
            discard = true;
        }

        if (discard)
        {
            try
            {
                DiscardFailedPool(connection, logger);
                discarded = true;
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
        try
        {
            if (opened && transaction is null)
            {
                // A failed EF BeginTransaction can leave its open reference acquired.
                await db.Database.CloseConnectionAsync();
            }
            if (ownsConnection && connection.State != ConnectionState.Closed)
            {
                await connection.CloseAsync();
            }
        }
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
            if (!discarded)
            {
                try
                {
                    DiscardFailedPool(connection, logger);
                }
                catch (Exception cleanupException)
                {
                    failure ??= ExceptionDispatchInfo.Capture(cleanupException);
                }
            }
        }

        failure?.Throw();
        return result;
    }

    private static void DiscardFailedPool(SqlConnection connection, ILogger logger)
    {
        // SqlClient exposes ClearPool per connection string, not eviction of an individual
        // physical session. Retire only this failed pool generation; never ClearAllPools.
        // Active borrowers are not interrupted, but their sessions are retired on return.
        SqlConnection.ClearPool(connection);
        logger.LogWarning(new EventId(6001, "SnapshotPoolRetired"),
            "SQL pool retired because snapshot read cleanup could not guarantee session isolation.");
    }
}
