using System.Data;
using System.Data.Common;
using System.Net;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Domain.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using FinanceFlow.Infrastructure.Persistence.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransfersConcurrencySqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_same_key_has_one_financial_winner_and_original_snapshot(bool changedPayload)
    {
        var observer = new TransferObserver { CoordinateInserts = true };
        using var factory = new ObservedFactory(fixture, observer);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        var body = new TransferBody(source.Id, destination.Id, 10m, new(2000, 1, 1), null);
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(PostTransferAsync(client, body, key), PostTransferAsync(client, body with { Amount = changedPayload ? 11m : 10m }, key));
        try
        {
            if (changedPayload)
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            }
            else
            {
                await SameTransferSnapshotAsync(responses[0], responses[1]);
            }
            var winner = responses.First(response => response.StatusCode == HttpStatusCode.Created);
            var amount = (await ReadAsync(winner)).GetProperty("amount").GetDecimal();
            Assert.Equal(-amount, await BalanceAsync(client, source.Id));
            Assert.Equal(amount, await BalanceAsync(client, destination.Id));
            await AssertCountsAsync(factory, owner, key, 1);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Concurrent_reverse_transfers_read_accounts_in_the_same_order_and_conserve_total()
    {
        var observer = new TransferObserver { CoordinateInserts = true };
        using var factory = new ObservedFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client, 100m);
        var destination = await AccountAsync(client, 200m);
        var responses = await Task.WhenAll(
            PostTransferAsync(client, new(source.Id, destination.Id, 10.01m, new(2000, 1, 1), null)),
            PostTransferAsync(client, new(destination.Id, source.Id, 5.05m, new(2000, 1, 1), null)));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            }
        }
        Assert.Equal(95.04m, await BalanceAsync(client, source.Id));
        Assert.Equal(204.96m, await BalanceAsync(client, destination.Id));
        Assert.Equal(2, observer.OrderedAccountReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transfer_holds_reference_locks_until_commit_before_source_or_destination_deactivation(bool sourceAccount)
    {
        var observer = new TransferObserver { PauseAccounts = true };
        using var factory = new ObservedFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        var deactivated = sourceAccount ? source : destination;
        // Arm after account creation; this callback observes only protected reads inside an explicit transaction.
        observer.Armed = true;
        var creating = PostTransferAsync(client, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null));
        Task<HttpResponseMessage>? deactivating = null;
        try
        {
            await observer.AccountsRead.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            Assert.Equal(IsolationLevel.RepeatableRead, observer.ReadIsolation);
            Assert.True(await HasSharedLockAsync(observer.SessionId));
            deactivating = FinancialChangeRequests.ChangeAsync(client, deactivated.Location, "PATCH", new { isActive = false }, deactivated.Version);
            await observer.DeactivationStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            Assert.False(deactivating.IsCompleted);
        }
        finally
        {
            observer.ReleaseAccounts.TrySetResult();
        }
        using var created = await creating;
        using var inactive = await deactivating!;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, inactive.StatusCode);
        Assert.Equal(-10m, await BalanceAsync(client, source.Id));
        Assert.Equal(10m, await BalanceAsync(client, destination.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deactivation_committing_before_account_validation_rejects_transfer_without_snapshot(bool sourceAccount)
    {
        var observer = new TransferObserver();
        using var factory = new ObservedFactory(fixture, observer);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await using var deactivate = connection.CreateCommand();
        deactivate.Transaction = transaction;
        deactivate.CommandText = "UPDATE dbo.FinancialAccounts SET IsActive=0 WHERE Id=@id";
        deactivate.Parameters.AddWithValue("@id", sourceAccount ? source.Id : destination.Id);
        Assert.Equal(1, await deactivate.ExecuteNonQueryAsync(Cancellation));
        observer.Armed = true;
        var key = Guid.NewGuid().ToString("N");

        var creating = PostTransferAsync(client, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null), key);
        await observer.AccountReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
        Assert.False(creating.IsCompleted);
        await transaction.CommitAsync(Cancellation);

        using var rejected = await creating;
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        await AssertCountsAsync(factory, owner, key, 0);
        Assert.Equal(0m, await BalanceAsync(client, source.Id));
        Assert.Equal(0m, await BalanceAsync(client, destination.Id));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("saved")]
    [InlineData("committed")]
    [InlineData("timeout")]
    public async Task Failure_boundaries_preserve_atomicity_and_same_key_recovers_exactly_once(string stage)
    {
        var observer = new TransferObserver();
        var saved = new SavedSnapshotFault();
        var committed = new CommittedFault();
        using var factory = new ObservedFactory(fixture, observer, saved, committed);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        var body = new TransferBody(source.Id, destination.Id, 10m, new(2000, 1, 1), null);
        var key = Guid.NewGuid().ToString("N");
        observer.FailSnapshot = stage == "snapshot";
        observer.TimeoutInsert = stage == "timeout";
        saved.Armed = stage == "saved";
        committed.Armed = stage == "committed";

        using var failed = await PostTransferAsync(client, body, key);

        Assert.Equal(stage == "timeout" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(observer.Injected || saved.Injected || committed.Injected);
        if (stage == "timeout")
        {
            Assert.Equal(-2, observer.SqlError);
        }
        var confirmedCount = stage == "committed" ? 1 : 0;
        await AssertCountsAsync(factory, owner, key, confirmedCount);
        Assert.Equal(-10m * confirmedCount, await BalanceAsync(client, source.Id));
        Assert.Equal(10m * confirmedCount, await BalanceAsync(client, destination.Id));
        using var recovered = await PostTransferAsync(client, body, key);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        using var replay = await PostTransferAsync(client, body, key);
        await SameTransferSnapshotAsync(recovered, replay);
        await AssertCountsAsync(factory, owner, key, 1);
        await using var scope = factory.Services.CreateAsyncScope();
        var record = await scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>().IdempotencyRecords.AsNoTracking()
            .SingleAsync(value => value.UserId == owner && value.Key == key && value.Operation == TransferCreationFingerprint.Operation, Cancellation);
        Assert.Equal(record.ResponseBody, await recovered.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(record.ETag, recovered.Headers.ETag!.ToString());
        Assert.Equal(-10m, await BalanceAsync(client, source.Id));
        Assert.Equal(10m, await BalanceAsync(client, destination.Id));
    }

    [Fact]
    public async Task Real_deadlock_does_not_leave_a_transfer_or_snapshot_and_same_key_can_retry()
    {
        var observer = new TransferObserver();
        using var factory = new ObservedFactory(fixture, observer);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var blocker = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await using var tableLock = connection.CreateCommand();
        tableLock.Transaction = blocker;
        tableLock.CommandText = "SET DEADLOCK_PRIORITY HIGH; SELECT TOP(1) Id FROM dbo.Transfers WITH(TABLOCKX,HOLDLOCK)";
        await tableLock.ExecuteScalarAsync(Cancellation);
        observer.Armed = true;
        var body = new TransferBody(source.Id, destination.Id, 10m, new(2000, 1, 1), null);
        var key = Guid.NewGuid().ToString("N");
        var creating = PostTransferAsync(client, body, key);
        await observer.InsertStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
        await using var accountLock = connection.CreateCommand();
        accountLock.Transaction = blocker;
        accountLock.CommandText = "UPDATE dbo.FinancialAccounts SET Name=Name WHERE Id=@id";
        accountLock.Parameters.AddWithValue("@id", source.Id);
        Assert.Equal(1, await accountLock.ExecuteNonQueryAsync(Cancellation));
        await blocker.CommitAsync(Cancellation);
        using var failed = await creating;
        Assert.Equal(1205, observer.SqlError);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain("SqlException", await failed.Content.ReadAsStringAsync(Cancellation));
        await AssertCountsAsync(factory, owner, key, 0);
        using var retry = await PostTransferAsync(client, body, key);
        using var replay = await PostTransferAsync(client, body, key);
        await SameTransferSnapshotAsync(retry, replay);
        await AssertCountsAsync(factory, owner, key, 1);
        Assert.Equal(-10m, await BalanceAsync(client, source.Id));
        Assert.Equal(10m, await BalanceAsync(client, destination.Id));
    }

    [Fact]
    public async Task Unrelated_primary_key_collision_is_not_replayed_and_does_not_create_a_success_snapshot()
    {
        var observer = new TransferObserver();
        var collision = new PrimaryKeyCollision();
        using var factory = new ObservedFactory(fixture, observer, collision);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        var body = new TransferBody(source.Id, destination.Id, 10m, new(2000, 1, 1), null);
        var originalKey = Guid.NewGuid().ToString("N");
        using var original = await PostTransferAsync(client, body, originalKey);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        collision.ExistingId = (await ReadAsync(original)).GetProperty("id").GetGuid();
        var failedKey = Guid.NewGuid().ToString("N");

        using var failed = await PostTransferAsync(client, body, failedKey);

        var updateFailure = Assert.IsType<DbUpdateException>(collision.PersistenceFailure);
        var sqlFailure = Assert.IsType<SqlException>(updateFailure.InnerException);
        Assert.Equal(2627, sqlFailure.Number);
        Assert.Contains("PK_Transfers", sqlFailure.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await AssertCountsAsync(factory, owner, originalKey, 1);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>().IdempotencyRecords
                .CountAsync(value => value.UserId == owner && value.Key == failedKey, Cancellation));
        }
        Assert.Equal(-10m, await BalanceAsync(client, source.Id));
        using var retry = await PostTransferAsync(client, body, failedKey);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(-20m, await BalanceAsync(client, source.Id));
        Assert.Equal(20m, await BalanceAsync(client, destination.Id));
    }

    private sealed class PrimaryKeyCollision : SaveChangesInterceptor
    {
        public Guid? ExistingId { get; set; }
        public Exception? PersistenceFailure { get; private set; }
        private bool injected;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (ExistingId is { } id)
            {
                var entry = eventData.Context!.ChangeTracker.Entries<Transfer>().SingleOrDefault(value => value.State == EntityState.Added);
                if (entry is not null)
                {
                    ExistingId = null;
                    entry.Property(value => value.Id).CurrentValue = id;
                    injected = true;
                }
            }
            return ValueTask.FromResult(result);
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (injected)
            {
                // SQL can fail while consuming INSERT results, after ExecuteReader succeeded.
                // SaveChanges observes that failure even when CommandFailed is not raised.
                PersistenceFailure = eventData.Exception;
            }
            return Task.CompletedTask;
        }
    }

    private static async Task AssertCountsAsync(ObservedFactory factory, Guid owner, string key, int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Equal(count, await db.Transfers.CountAsync(value => value.UserId == owner, Cancellation));
        Assert.Equal(count, await db.IdempotencyRecords.CountAsync(value => value.UserId == owner && value.Key == key
            && value.Operation == TransferCreationFingerprint.Operation, Cancellation));
    }

    private async Task<bool> HasSharedLockAsync(int sessionId)
    {
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            InitialCatalog = new SqlConnectionStringBuilder(fixture.MigrationConnectionString).InitialCatalog
        };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.dm_tran_locks AS locks
            LEFT JOIN sys.partitions AS partitions ON locks.resource_associated_entity_id=partitions.hobt_id
            WHERE locks.request_session_id=@session AND locks.request_status='GRANT' AND locks.request_mode='S'
              AND (partitions.object_id=OBJECT_ID('dbo.FinancialAccounts')
                OR (locks.resource_type='OBJECT' AND locks.resource_associated_entity_id=OBJECT_ID('dbo.FinancialAccounts')))
            """;
        command.Parameters.AddWithValue("@session", sessionId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(Cancellation)) > 0;
    }

    private sealed class ObservedFactory(IdentitySqlFixture fixture, params IInterceptor[] interceptors) : ApiFactory()
    {
        public override string ConnectionString => fixture.ApplicationConnectionString;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(interceptors)));
        }
    }

    private sealed class SavedSnapshotFault : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<IdempotencyRecord>().Any())
            {
                Armed = false;
                Injected = true;
                throw new InvalidOperationException("Injected transfer failure before commit.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommittedFault : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                Injected = true;
                throw new IOException("Injected lost transfer response after commit.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class TransferObserver : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool CoordinateInserts { get; init; }
        public bool PauseAccounts { get; init; }
        public bool FailSnapshot { get; set; }
        public bool TimeoutInsert { get; set; }
        public bool Injected { get; private set; }
        public int? SqlError { get; private set; }
        public int SessionId { get; private set; }
        public IsolationLevel? ReadIsolation { get; private set; }
        public int OrderedAccountReads => orderedAccountReads;
        private int orderedAccountReads;
        private int inserts;
        private readonly TaskCompletionSource bothInserts = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AccountsRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAccounts { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AccountReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeactivationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InsertStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.Transaction is not null && command.CommandText.Contains("FROM [dbo].[FinancialAccounts]", StringComparison.Ordinal))
            {
                AccountReadStarted.TrySetResult();
            }
            if (command.CommandText.Contains("UPDATE [dbo].[FinancialAccounts]", StringComparison.Ordinal))
            {
                DeactivationStarted.TrySetResult();
            }
            if (FailSnapshot && command.CommandText.Contains("INSERT INTO [dbo].[IdempotencyRecords]", StringComparison.Ordinal))
            {
                FailSnapshot = false;
                Injected = true;
                throw new InvalidOperationException("Injected transfer snapshot failure.");
            }
            if (command.CommandText.Contains("INSERT INTO [dbo].[Transfers]", StringComparison.Ordinal))
            {
                InsertStarted.TrySetResult();
                if (Armed && PauseAccounts)
                {
                    // Both accounts have been fully materialized and validated at this point.
                    // Pause before INSERT, while RepeatableRead still retains both reads.
                    Armed = false;
                    SessionId = ((SqlConnection)command.Connection!).ServerProcessId;
                    ReadIsolation = command.Transaction!.IsolationLevel;
                    AccountsRead.TrySetResult();
                    await ReleaseAccounts.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
                if (CoordinateInserts)
                {
                    if (Interlocked.Increment(ref inserts) == 2)
                    {
                        bothInserts.TrySetResult();
                    }
                    await bothInserts.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
                if (TimeoutInsert)
                {
                    TimeoutInsert = false;
                    Injected = true;
                    command.CommandTimeout = 1;
                    command.CommandText = "WAITFOR DELAY '00:00:02';\n" + command.CommandText;
                }
            }
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.Transaction is not null && command.CommandText.Contains("FROM [dbo].[FinancialAccounts]", StringComparison.Ordinal)
                && command.CommandText.Contains("ORDER BY", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref orderedAccountReads);
                Assert.Equal(IsolationLevel.RepeatableRead, command.Transaction.IsolationLevel);
            }
            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is SqlException exception)
            {
                SqlError = exception.Number;
            }
            return Task.CompletedTask;
        }
    }
}
