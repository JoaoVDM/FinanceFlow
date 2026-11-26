using System.Data;
using System.Data.Common;
using System.Net;
using FinanceFlow.Application.Statements;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.TransferCancellationRequests;
using static FinanceFlow.IntegrationTests.FinancialReadRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class StatementConsistencySqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("income", 12, 4)]
    [InlineData("delete-expense", 8, 2)]
    [InlineData("transfer", 1, 4)]
    [InlineData("cancel-transfer", 7, 2)]
    public async Task One_snapshot_keeps_page_totals_and_account_coherent_while_an_independent_writer_commits(string action, int afterBalance, int afterCount)
    {
        var observer = new StatementBarrier();
        using var factory = new FinancialReadApiFactory(fixture, observer);
        using var reader = factory.CreateClient();
        using var writer = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(reader);
        writer.DefaultRequestHeaders.Authorization = reader.DefaultRequestHeaders.Authorization;
        var a = await AccountAsync(reader);
        var b = await AccountAsync(reader);
        using var income = await MovementAsync(reader, a.Id, "income", 10m);
        using var expense = await MovementAsync(reader, a.Id, "expense", 3m);
        using var transfer = await PostTransferAsync(reader, new(a.Id, b.Id, 2m, new(2000, 1, 3), null));
        observer.Armed = true;

        var reading = StatementAsync(reader, a.Id);
        try
        {
            // The account SELECT has completed inside Snapshot before the aggregate is paused.
            await observer.AtAggregate.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            if (action == "income")
            {
                using var created = await MovementAsync(writer, a.Id, "income", 7m);
            }
            else if (action == "transfer")
            {
                using var created = await PostTransferAsync(writer, new(a.Id, b.Id, 4m, new(2000, 1, 3), null));
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }
            else if (action == "cancel-transfer")
            {
                using var canceled = await CancelAsync(writer, transfer.Headers.Location!, transfer.Headers.ETag!.ToString());
                await AssertSuccessAsync(canceled);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, expense.Headers.Location);
                request.Headers.Add("If-Match", expense.Headers.ETag!.ToString());
                using var deleted = await writer.SendAsync(request, Cancellation);
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            }
        }
        finally
        {
            observer.Release.TrySetResult();
        }
        var oldView = await reading;
        observer.Armed = false;

        Assert.True(observer.Paused);
        Assert.Equal(3, observer.SnapshotCommands);
        Assert.Equal(3, oldView.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, oldView.GetProperty("items").GetArrayLength());
        Assert.Equal(10m, oldView.GetProperty("income").GetDecimal());
        Assert.Equal(3m, oldView.GetProperty("expense").GetDecimal());
        Assert.Equal(2m, oldView.GetProperty("transferOut").GetDecimal());
        Assert.Equal(5m, oldView.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(5m, oldView.GetProperty("items").EnumerateArray().Sum(item => item.GetProperty("amount").GetDecimal()));
        var newView = await StatementAsync(reader, a.Id);
        Assert.Equal((decimal)afterBalance, newView.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(afterCount, newView.GetProperty("totalCount").GetInt32());
        Assert.Equal((decimal)afterBalance, await BalanceAsync(reader, a.Id, "2000-01-06"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_canceled_reads_dispose_transaction_and_connection_and_can_be_repeated(bool cancel)
    {
        var fault = new ReadFault(cancel);
        using var factory = new FinancialReadApiFactory(fixture, fault);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client, 1.01m);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IAccountStatementStore>();
        var query = new StatementQuery(new(2000, 1, 1), new(2000, 1, 6));
        fault.Armed = true;

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => store.GetAsync(owner, account.Id, query, Cancellation));

        Assert.Same(fault.Exception, exception);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        var repeated = await store.GetAsync(owner, account.Id, query, Cancellation);
        Assert.Null(repeated.Error);
        Assert.Equal(1.01m, repeated.Statement!.ClosingBalance);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Explicit_snapshot_configuration_is_required_detected_by_readiness_and_does_not_enable_RCSI_or_change_model()
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString) { InitialCatalog = "FinanceFlow6C_" + Guid.NewGuid().ToString("N") };
        // Changing Snapshot availability must not reuse a session whose isolation level was
        // left at Snapshot by the statement or the SQL probe. JWT state checks must still read
        // the real user before the financial query independently fails with SQL 3952.
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            InitialCatalog = migration.InitialCatalog,
            Pooling = false
        };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        using var factory = new SqlApiFactory(fixture, application.ConnectionString);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client);
        Assert.Equal(0m, (await StatementAsync(client, account.Id)).GetProperty("closingBalance").GetDecimal());
        using var authenticatedBefore = await client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(HttpStatusCode.OK, authenticatedBefore.StatusCode);
        Assert.Equal(owner, (await ReadAsync(authenticatedBefore)).GetProperty("id").GetGuid());
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(application.ConnectionString, Cancellation);
        await using (var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(application.ConnectionString).Options))
        {
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT CAST(snapshot_isolation_state AS int) AS [Value] FROM sys.databases WHERE database_id=DB_ID()").SingleAsync(Cancellation));
            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT CAST(is_read_committed_snapshot_on AS int) AS [Value] FROM sys.databases WHERE database_id=DB_ID()").SingleAsync(Cancellation));
        }
        using var ready = await client.GetAsync("/health/ready", Cancellation);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await using var admin = new SqlConnection(new SqlConnectionStringBuilder(fixture.AdminConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await admin.OpenAsync(Cancellation);
        await using var configure = admin.CreateCommand();
        // Only this isolated disposable test database is reconfigured; no persistent database is touched.
        configure.CommandText = "ALTER DATABASE [" + migration.InitialCatalog + "] SET ALLOW_SNAPSHOT_ISOLATION OFF";
        await configure.ExecuteNonQueryAsync(Cancellation);

        var snapshotFailure = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var restricted = new SqlConnection(application.ConnectionString);
            await restricted.OpenAsync(Cancellation);
            await using var transaction = await restricted.BeginTransactionAsync(IsolationLevel.Snapshot, Cancellation);
            await using var query = restricted.CreateCommand();
            query.Transaction = (SqlTransaction)transaction;
            query.CommandText = "SELECT TOP(1) Id FROM dbo.FinancialAccounts";
            await query.ExecuteScalarAsync(Cancellation);
        });
        Assert.Equal(3952, snapshotFailure.Number);

        using var notReady = await client.GetAsync("/health/ready", Cancellation);
        using var live = await client.GetAsync("/health/live", Cancellation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var authenticatedAfter = await client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(HttpStatusCode.OK, authenticatedAfter.StatusCode);
        Assert.Equal(owner, (await ReadAsync(authenticatedAfter)).GetProperty("id").GetGuid());
        foreach (var path in new[] { "/api/v1/transfers", $"/api/v1/accounts/{account.Id}/statement?from=2000-01-01&to=2000-01-06" })
        {
            using var unavailable = await client.GetAsync(path, Cancellation);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Empty(unavailable.Headers.WwwAuthenticate);
            Assert.Equal("service_unavailable", (await ReadAsync(unavailable)).GetProperty("code").GetString());
            var body = await unavailable.Content.ReadAsStringAsync(Cancellation);
            Assert.DoesNotContain("3952", body);
            Assert.DoesNotContain("SqlException", body);
            Assert.DoesNotContain("closingBalance", body);
        }
    }

    private sealed class StatementBarrier : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Paused { get; private set; }
        public int SnapshotCommands { get; private set; }
        public TaskCompletionSource AtAggregate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.Transaction?.IsolationLevel == IsolationLevel.Snapshot)
            {
                SnapshotCommands++;
                if (!Paused && command.CommandText.Contains("UNION ALL", StringComparison.Ordinal))
                {
                    Paused = true;
                    AtAggregate.TrySetResult();
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
            }
            return result;
        }
    }

    private sealed class ReadFault(bool cancel) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public Exception Exception { get; } = cancel ? new OperationCanceledException("Injected read cancellation.") : new IOException("Injected read failure.");
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("UNION ALL", StringComparison.Ordinal))
            {
                Armed = false;
                throw Exception;
            }
            return ValueTask.FromResult(result);
        }
    }
}
