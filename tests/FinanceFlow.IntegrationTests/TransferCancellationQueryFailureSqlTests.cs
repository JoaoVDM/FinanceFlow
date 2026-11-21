using System.Data.Common;
using System.Net;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.TransferCancellationRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransferCancellationQueryFailureSqlTests(IdentitySqlFixture fixture)
{
    private static readonly DateTimeOffset WriterTimestamp = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, 1205)]
    [InlineData(false, -2)]
    [InlineData(true, 1205)]
    [InlineData(true, -2)]
    public async Task Real_query_deadlocks_and_timeouts_are_503_without_cancellation_or_extra_recovery_reads(bool recovery, int sqlNumber)
    {
        var fault = new QueryFault(recovery, sqlNumber);
        using var factory = new QueryFailureFactory(fixture, fault);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        var key = Guid.NewGuid().ToString("N");
        var body = new TransferBody(source.Id, destination.Id, 10.01m, new(2000, 1, 1), null);
        using var created = await PostTransferAsync(client, body, key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        fault.Armed = true;

        var canceling = CancelAsync(client, created.Headers.Location!, created.Headers.ETag!.ToString());
        try
        {
            await ReachTargetLookupAsync(fault, id);
            if (sqlNumber == 1205)
            {
                await CauseReadDeadlockAsync(fault, id);
            }
            else
            {
                fault.ReleaseLookup.TrySetResult();
            }
        }
        finally
        {
            fault.ReleaseWrite.TrySetResult();
            fault.ReleaseLookup.TrySetResult();
        }
        using var failed = await canceling.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        fault.Armed = false;

        Assert.True(fault.Injected);
        Assert.Equal(sqlNumber, fault.SqlError);
        if (sqlNumber == 1205)
        {
            // QueryIterationFailed can observe the provider exception directly or the EF strategy wrapper.
            var queryException = fault.QueryException;
            if (queryException is InvalidOperationException strategyFailure)
            {
                queryException = strategyFailure.InnerException;
            }
            Assert.Equal(1205, Assert.IsType<SqlException>(queryException).Number);
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.True(failed.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", failed.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync(failed);
        Assert.Equal("service_unavailable", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("sqlError", out _));
        var publicBody = await failed.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("SqlException", publicBody);
        Assert.DoesNotContain("InvalidOperationException", publicBody);
        Assert.DoesNotContain("SELECT", publicBody);
        Assert.Equal(1, fault.InitialReads);
        Assert.Equal(recovery ? 1 : 0, fault.RecoveryReads);
        Assert.Equal(recovery ? 1 : 0, fault.Updates);
        if (recovery)
        {
            Assert.IsType<DbUpdateConcurrencyException>(fault.ConcurrencyFailure);
        }

        using var current = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        if (recovery)
        {
            Assert.NotEqual(created.Headers.ETag, current.Headers.ETag);
        }
        else
        {
            Assert.Equal(created.Headers.ETag, current.Headers.ETag);
        }
        Assert.Equal(-10.01m, await BalanceAsync(client, source.Id));
        Assert.Equal(10.01m, await BalanceAsync(client, destination.Id));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
            var persisted = await db.Transfers.AsNoTracking().SingleAsync(value => value.Id == id && value.UserId == owner, Cancellation);
            Assert.False(persisted.IsCanceled);
            Assert.Null(persisted.CanceledAtUtc);
            Assert.Equal(recovery ? (DateTimeOffset?)WriterTimestamp : null, persisted.UpdatedAtUtc);
            Assert.Equal(1, await db.Transfers.CountAsync(value => value.UserId == owner, Cancellation));
            Assert.Equal(0, await db.FinancialTransactions.CountAsync(value => value.UserId == owner, Cancellation));
            var snapshot = await db.IdempotencyRecords.AsNoTracking().SingleAsync(value => value.UserId == owner
                && value.Operation == TransferCreationFingerprint.Operation && value.Key == key, Cancellation);
            Assert.Equal(await created.Content.ReadAsStringAsync(Cancellation), snapshot.ResponseBody);
        }
        using var retry = await CancelAsync(client, created.Headers.Location!, current.Headers.ETag!.ToString());
        await AssertSuccessAsync(retry);
        using var repeated = await CancelAsync(client, created.Headers.Location!, retry.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        using var replay = await PostTransferAsync(client, body, key);
        await SameTransferSnapshotAsync(created, replay);
        Assert.Equal(0m, await BalanceAsync(client, source.Id));
        Assert.Equal(0m, await BalanceAsync(client, destination.Id));
    }

    [Theory]
    [InlineData(false, "invalid-operation")]
    [InlineData(false, "update")]
    [InlineData(true, "invalid-operation")]
    [InlineData(true, "update")]
    public async Task Unexpected_query_exceptions_still_use_the_safe_500_handler(bool recovery, string kind)
    {
        Exception unexpected = kind == "update"
            ? new DbUpdateException("Injected query failure.", new InvalidOperationException("No SQL exception."))
            : new InvalidOperationException("Injected query failure.");
        var fault = new QueryFault(recovery, unexpected: unexpected);
        using var factory = new QueryFailureFactory(fixture, fault);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null));
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        fault.Armed = true;

        var canceling = CancelAsync(client, created.Headers.Location!, created.Headers.ETag!.ToString());
        try
        {
            await ReachTargetLookupAsync(fault, id);
        }
        finally
        {
            fault.ReleaseWrite.TrySetResult();
            fault.ReleaseLookup.TrySetResult();
        }
        using var failed = await canceling;
        fault.Armed = false;

        Assert.True(fault.Injected);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("Injected query failure", await failed.Content.ReadAsStringAsync(Cancellation));
        Assert.True(failed.Headers.CacheControl?.NoStore);
        Assert.Equal(1, fault.InitialReads);
        Assert.Equal(recovery ? 1 : 0, fault.RecoveryReads);
        using var get = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(-10m, await BalanceAsync(client, source.Id));
        Assert.Equal(10m, await BalanceAsync(client, destination.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_cancellation_exceptions_propagate_instead_of_becoming_unavailable(bool recovery)
    {
        var cancellation = new OperationCanceledException("Injected query cancellation.", Cancellation);
        var fault = new QueryFault(recovery, unexpected: cancellation);
        using var factory = new QueryFailureFactory(fixture, fault);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null));
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITransferStore>();
        fault.Armed = true;

        var canceling = store.CancelAsync(owner, id, Convert.FromBase64String(created.Headers.ETag!.Tag.Trim('"')),
            WriterTimestamp, Cancellation);
        try
        {
            await ReachTargetLookupAsync(fault, id);
        }
        finally
        {
            fault.ReleaseWrite.TrySetResult();
            fault.ReleaseLookup.TrySetResult();
        }
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceling);
        fault.Armed = false;

        Assert.True(fault.Injected);
        Assert.Same(cancellation, thrown);
        Assert.Equal(1, fault.InitialReads);
        Assert.Equal(recovery ? 1 : 0, fault.RecoveryReads);
        using var get = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(-10m, await BalanceAsync(client, source.Id));
        Assert.Equal(10m, await BalanceAsync(client, destination.Id));
    }

    private async Task ReachTargetLookupAsync(QueryFault fault, Guid id)
    {
        if (fault.Recovery)
        {
            await fault.AtWrite.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            // A real independent UPDATE makes the pending cancellation's version stale.
            await using var connection = new SqlConnection(fixture.MigrationConnectionString);
            await connection.OpenAsync(Cancellation);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE dbo.Transfers SET UpdatedAtUtc=@now WHERE Id=@id";
            update.Parameters.AddWithValue("@now", WriterTimestamp);
            update.Parameters.AddWithValue("@id", id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(Cancellation));
            fault.ReleaseWrite.TrySetResult();
        }
        await fault.AtLookup.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
    }

    private async Task CauseReadDeadlockAsync(QueryFault fault, Guid id)
    {
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await using var rowLock = connection.CreateCommand();
        rowLock.Transaction = transaction;
        rowLock.CommandText = "SET DEADLOCK_PRIORITY HIGH; UPDATE dbo.Transfers WITH(ROWLOCK) SET UpdatedAtUtc=UpdatedAtUtc WHERE Id=@id";
        rowLock.Parameters.AddWithValue("@id", id);
        Assert.Equal(1, await rowLock.ExecuteNonQueryAsync(Cancellation));
        fault.ReleaseLookup.TrySetResult();
        await WaitForBlockedLookupAsync(fault.SessionId, connection.ServerProcessId);

        // The lookup holds table IS and waits for our row X. Requesting table X here
        // closes a genuine lock cycle. SQL chooses the lower-priority API query as victim.
        await using var tableLock = connection.CreateCommand();
        tableLock.Transaction = transaction;
        tableLock.CommandTimeout = 20;
        tableLock.CommandText = "SELECT TOP(1) Id FROM dbo.Transfers WITH(TABLOCKX,HOLDLOCK)";
        await tableLock.ExecuteScalarAsync(Cancellation);
        // Roll back even the fixture-only write, preserving the original stored version.
        await transaction.RollbackAsync(Cancellation);
    }

    private async Task WaitForBlockedLookupAsync(int querySession, int blockerSession)
    {
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            InitialCatalog = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString).InitialCatalog
        };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.dm_exec_requests AS requests
            WHERE requests.session_id=@query AND requests.blocking_session_id=@blocker
              AND requests.wait_type LIKE 'LCK_M_%'
              AND EXISTS (SELECT 1 FROM sys.dm_tran_locks AS locks
                WHERE locks.request_session_id=@query AND locks.resource_type='OBJECT'
                  AND locks.resource_associated_entity_id=OBJECT_ID('dbo.Transfers')
                  AND locks.request_status='GRANT' AND locks.request_mode='IS')
            """;
        command.Parameters.AddWithValue("@query", querySession);
        command.Parameters.AddWithValue("@blocker", blockerSession);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Observe actual SQL blocking; no delay is used to assume request ordering.
        while (Convert.ToInt32(await command.ExecuteScalarAsync(deadline.Token)) == 0)
        {
            deadline.Token.ThrowIfCancellationRequested();
        }
    }

    private sealed class QueryFailureFactory(IdentitySqlFixture fixture, QueryFault fault) : ApiFactory()
    {
        public override string ConnectionString => fixture.ApplicationConnectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options =>
            {
                options.AddInterceptors(fault);
                // Reader creation can succeed before SQL reports an error while consuming it.
                options.LogTo((eventId, _) => eventId == CoreEventId.QueryIterationFailed, fault.QueryFailed);
            }));
        }
    }

    private sealed class QueryFault(bool recovery, int? sqlNumber = null, Exception? unexpected = null)
        : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public bool Recovery => recovery;
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public int InitialReads { get; private set; }
        public int RecoveryReads { get; private set; }
        public int Updates { get; private set; }
        public int SessionId { get; private set; }
        public int? SqlError { get; private set; }
        public Exception? QueryException { get; private set; }
        public Exception? ConcurrencyFailure { get; private set; }
        public TaskCompletionSource AtWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AtLookup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLookup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Armed)
            {
                return result;
            }
            if (command.CommandText.Contains("UPDATE [dbo].[Transfers]", StringComparison.Ordinal))
            {
                Updates++;
                if (Recovery)
                {
                    AtWrite.TrySetResult();
                    await ReleaseWrite.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
            }
            if (!command.CommandText.Contains("FROM [dbo].[Transfers]", StringComparison.Ordinal))
            {
                return result;
            }
            var initialRead = command.CommandText.Contains("[RowVersion]", StringComparison.Ordinal);
            var recoveryRead = command.CommandText.Contains("EXISTS", StringComparison.Ordinal);
            if (initialRead)
            {
                InitialReads++;
            }
            if (recoveryRead)
            {
                RecoveryReads++;
            }
            if (Injected || !(Recovery ? recoveryRead : initialRead))
            {
                return result;
            }

            Assert.Null(command.Transaction);
            SessionId = ((SqlConnection)command.Connection!).ServerProcessId;
            AtLookup.TrySetResult();
            await ReleaseLookup.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            Injected = true;
            if (unexpected is not null)
            {
                throw unexpected;
            }
            if (sqlNumber == -2)
            {
                // Real SqlClient timeout, not a fabricated SqlException or an ordering sleep.
                command.CommandTimeout = 1;
                command.CommandText = "WAITFOR DELAY '00:00:02';\n" + command.CommandText;
            }
            else if (sqlNumber == 1205)
            {
                // Allow SQL's deadlock detector to choose the victim before a command timeout.
                command.CommandTimeout = 30;
            }
            return result;
        }

        public ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(ConcurrencyExceptionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                ConcurrencyFailure = eventData.Exception;
            }
            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Injected && eventData.Exception is SqlException sql)
            {
                SqlError = sql.Number;
            }
            return Task.CompletedTask;
        }

        public void QueryFailed(EventData eventData)
        {
            if (Injected && eventData is DbContextTypeErrorEventData error)
            {
                QueryException = error.Exception;
                var exception = error.Exception is InvalidOperationException { InnerException: { } inner } ? inner : error.Exception;
                if (exception is SqlException sql)
                {
                    SqlError = sql.Number;
                }
            }
        }
    }
}
