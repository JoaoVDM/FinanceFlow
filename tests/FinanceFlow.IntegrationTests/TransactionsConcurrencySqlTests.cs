using System.Data;
using System.Data.Common;
using System.Net;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransactionsConcurrencySqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Simultaneous_same_key_creations_commit_one_transaction_and_one_snapshot()
    {
        var observer = new CreationObserver();
        using var factory = new ObservedFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        var key = Guid.NewGuid().ToString("N");
        observer.CoordinateInserts = true;

        var responses = await Task.WhenAll(CreateAsync(client, references, key), CreateAsync(client, references, key));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            Assert.Equal(await responses[0].Content.ReadAsStringAsync(Cancellation), await responses[1].Content.ReadAsStringAsync(Cancellation));
            Assert.Equal(responses[0].Headers.Location, responses[1].Headers.Location);
            Assert.Equal(responses[0].Headers.ETag, responses[1].Headers.ETag);
            Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
            Assert.Equal(1, await SnapshotCountAsync(key));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Failure_between_transaction_insert_and_snapshot_insert_rolls_back_everything_and_allows_retry()
    {
        var observer = new CreationObserver();
        using var factory = new ObservedFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        var key = Guid.NewGuid().ToString("N");
        observer.FailSnapshot = true;

        using var failed = await CreateAsync(client, references, key);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(observer.FaultInjected);
        Assert.Equal(0, await SnapshotCountAsync(key));
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId));
        using var emptyList = await client.GetAsync("/api/v1/transactions", Cancellation);
        Assert.Equal(0, (await ReadAsync(emptyList)).GetProperty("totalCount").GetInt32());
        observer.FailSnapshot = false;
        using var retry = await CreateAsync(client, references, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
        Assert.Equal(1, await SnapshotCountAsync(key));
    }

    [Theory]
    [InlineData("FinancialAccounts")]
    [InlineData("Categories")]
    public async Task Creation_holds_real_reference_locks_until_commit_and_concurrent_deactivation_waits(string table)
    {
        var observer = new CreationObserver();
        using var factory = new ObservedFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        observer.PauseAfterReferences = true;
        var creation = CreateAsync(client, references);
        Task<HttpResponseMessage>? deactivation = null;
        try
        {
            await observer.ReferencesRead.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            Assert.Equal(IsolationLevel.RepeatableRead, observer.ReadIsolation);
            Assert.True(await HasHeldSharedReferenceLockAsync(observer.SessionId, table));
            var location = table == "FinancialAccounts" ? references.AccountLocation : references.CategoryLocation;
            var version = table == "FinancialAccounts" ? references.AccountVersion : references.CategoryVersion;
            deactivation = FinancialChangeRequests.ChangeAsync(client, location, "PATCH", new { isActive = false }, version);
            await observer.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            Assert.False(deactivation.IsCompleted);
        }
        finally
        {
            observer.ReleaseReferences.TrySetResult();
        }

        using var created = await creation;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var inactive = await deactivation!;
        Assert.Equal(HttpStatusCode.NoContent, inactive.StatusCode);
        Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
        using var next = await CreateAsync(client, references);
        Assert.Equal(HttpStatusCode.Conflict, next.StatusCode);
    }

    [Theory]
    [InlineData("FinancialAccounts")]
    [InlineData("Categories")]
    public async Task Deactivation_that_wins_before_the_reference_read_rejects_creation(string table)
    {
        var observer = new CreationObserver { ObservedReadTable = table };
        using var factory = new ObservedFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var sqlTransaction = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await using var command = connection.CreateCommand();
        command.Transaction = sqlTransaction;
        command.CommandText = $"UPDATE dbo.{table} SET IsActive=0 WHERE Id=@id";
        command.Parameters.AddWithValue("@id", table == "FinancialAccounts" ? references.AccountId : references.CategoryId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Cancellation));
        observer.ObserveReadStart = true;

        var creation = CreateAsync(client, references);
        await observer.ReferenceReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
        Assert.False(creation.IsCompleted);
        await sqlTransaction.CommitAsync(Cancellation);

        using var rejected = await creation;
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId));
    }

    private async Task<int> SnapshotCountAsync(string key)
    {
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.IdempotencyRecords WHERE Operation='transactions.create.v1' AND [Key]=@key";
        command.Parameters.AddWithValue("@key", key);
        return Convert.ToInt32(await command.ExecuteScalarAsync(Cancellation));
    }

    private async Task<bool> HasHeldSharedReferenceLockAsync(int sessionId, string table)
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
            WHERE locks.request_session_id=@sessionId AND locks.request_status='GRANT' AND locks.request_mode='S'
              AND (partitions.object_id=OBJECT_ID(@table)
                OR (locks.resource_type='OBJECT' AND locks.resource_associated_entity_id=OBJECT_ID(@table)))
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@table", "dbo." + table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(Cancellation)) > 0;
    }

    private sealed class ObservedFactory(IdentitySqlFixture fixture, DbCommandInterceptor observer) : ApiFactory()
    {
        public override string ConnectionString => fixture.ApplicationConnectionString;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(observer)));
        }
    }

    private sealed class CreationObserver : DbCommandInterceptor
    {
        public bool CoordinateInserts { get; set; }
        public bool FailSnapshot { get; set; }
        public bool FaultInjected { get; private set; }
        public bool PauseAfterReferences { get; set; }
        public bool ObserveReadStart { get; set; }
        public string ObservedReadTable { get; init; } = "FinancialAccounts";
        public int SessionId { get; private set; }
        public IsolationLevel? ReadIsolation { get; private set; }
        public TaskCompletionSource ReferencesRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReferences { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReferenceReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource bothInserts = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int inserts;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (ObserveReadStart && command.CommandText.Contains($"FROM [dbo].[{ObservedReadTable}]", StringComparison.Ordinal))
            {
                ReferenceReadStarted.TrySetResult();
            }
            if (command.CommandText.Contains("UPDATE [dbo].[FinancialAccounts]", StringComparison.Ordinal)
                || command.CommandText.Contains("UPDATE [dbo].[Categories]", StringComparison.Ordinal))
            {
                MutationStarted.TrySetResult();
            }
            if (FailSnapshot && command.CommandText.Contains("INSERT INTO [dbo].[IdempotencyRecords]", StringComparison.Ordinal))
            {
                FaultInjected = true;
                throw new InvalidOperationException("Injected test failure before idempotency persistence.");
            }
            if (CoordinateInserts && command.CommandText.Contains("INSERT INTO [dbo].[FinancialTransactions]", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref inserts) == 2) bothInserts.TrySetResult();
                await bothInserts.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (PauseAfterReferences && command.CommandText.Contains("FROM [dbo].[Categories]", StringComparison.Ordinal))
            {
                PauseAfterReferences = false;
                SessionId = ((SqlConnection)command.Connection!).ServerProcessId;
                ReadIsolation = command.Transaction!.IsolationLevel;
                ReferencesRead.TrySetResult();
                await ReleaseReferences.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
