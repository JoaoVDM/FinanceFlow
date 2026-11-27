using System.Data;
using System.Data.Common;
using System.Net;
using FinanceFlow.Application.Reports;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.FinancialReadRequests;
using static FinanceFlow.IntegrationTests.ReportTestRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class ReportConsistencySqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ownership_and_conditional_sums_share_one_view_while_an_independent_writer_commits(bool deleteExpense)
    {
        var barrier = new AggregateBarrier();
        using var factory = new ReportApiFactory(fixture, barrier);
        using var reader = factory.CreateClient();
        using var writer = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(reader);
        writer.DefaultRequestHeaders.Authorization = reader.DefaultRequestHeaders.Authorization;
        var account = await AccountAsync(reader);
        using var income = await MovementAsync(reader, account.Id, "income", 10m, "2026-11-01");
        using var expense = await MovementAsync(reader, account.Id, "expense", 3m, "2026-11-26");
        barrier.Armed = true;

        var reading = SummaryAsync(reader, account.Id);
        try
        {
            await barrier.AtAggregate.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            if (deleteExpense)
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, expense.Headers.Location);
                request.Headers.Add("If-Match", expense.Headers.ETag!.ToString());
                using var deleted = await writer.SendAsync(request, Cancellation);
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            }
            else
            {
                using var created = await MovementAsync(writer, account.Id, "income", 7m, "2026-11-01");
            }
        }
        finally
        {
            barrier.Release.TrySetResult();
        }

        Totals(await reading, 10m, 3m);
        Assert.Equal(2, barrier.ReadCommands);
        Assert.True(barrier.Paused);
        barrier.Armed = false;
        Totals(await SummaryAsync(reader, account.Id), deleteExpense ? 10m : 17m, deleteExpense ? 0m : 3m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Summary_restores_the_same_pooled_session_before_real_JWT_revocation_and_a_later_query(bool filter)
    {
        var database = "FinanceFlowSummary_" + Guid.NewGuid().ToString("N");
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString) { InitialCatalog = database, Pooling = false };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            InitialCatalog = database, ApplicationName = database, Pooling = true, MaxPoolSize = 1, MinPoolSize = 0
        };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        var trace = new AggregateBarrier();
        using var factory = new ReportApiFactory(fixture, trace, connectionString: application.ConnectionString);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client);
        using var movement = await MovementAsync(client, account.Id, "income", 1.01m, "2026-11-01");
        var before = await SessionAsync(application.ConnectionString);
        trace.InspectSession = true;

        Totals(await SummaryAsync(client, filter ? account.Id : null), 1.01m, 0m);

        var after = await SessionAsync(application.ConnectionString);
        Assert.Equal(before.Spid, trace.Spid);
        Assert.Equal(before.ConnectionId, trace.ConnectionId);
        Assert.Equal(before.Spid, after.Spid);
        Assert.Equal(before.ConnectionId, after.ConnectionId);
        Assert.Equal(2, after.Isolation);
        Assert.Equal(0, after.Transactions);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
            var result = await scope.ServiceProvider.GetRequiredService<IReportStore>()
                .GetSummaryAsync(owner, new(new(2026, 11, 1), new(2026, 11, 26), filter ? account.Id : null), Cancellation);
            Assert.Null(result.Error);
            Assert.Equal(1.01m, result.Summary!.Result);
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
        using var profile = await client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(owner, (await ReadAsync(profile)).GetProperty("id").GetGuid());
        using var logout = await client.PostAsync("/api/v1/auth/logout", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revoked = await client.GetAsync(SummaryPath(), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        var final = await SessionAsync(application.ConnectionString);
        Assert.Equal(after, final);
    }

    private static async Task<(int Spid, Guid ConnectionId, int Isolation, int Transactions)> SessionAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(@@SPID AS int), CAST(transaction_isolation_level AS int), @@TRANCOUNT FROM sys.dm_exec_sessions WHERE session_id=@@SPID";
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        Assert.True(await reader.ReadAsync(Cancellation));
        var state = (Spid: reader.GetInt32(0), ConnectionId: connection.ClientConnectionId, Isolation: reader.GetInt32(1), Transactions: reader.GetInt32(2));
        TestContext.Current.TestOutputHelper!.WriteLine($"Summary session: SPID={state.Spid}; ClientConnectionId={state.ConnectionId}; Isolation={state.Isolation}; TranCount={state.Transactions}");
        return state;
    }

    private sealed class AggregateBarrier : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool InspectSession { get; set; }
        public bool Paused { get; private set; }
        public int ReadCommands { get; private set; }
        public int Spid { get; private set; }
        public Guid ConnectionId { get; private set; }
        public TaskCompletionSource AtAggregate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.Transaction?.IsolationLevel != IsolationLevel.Snapshot)
            {
                return result;
            }
            if (InspectSession)
            {
                Spid = ((SqlConnection)command.Connection!).ServerProcessId;
                ConnectionId = ((SqlConnection)command.Connection!).ClientConnectionId;
            }
            if (Armed)
            {
                ReadCommands++;
                if (command.CommandText.Contains("SUM(", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Contains("[FinancialTransactions]", command.CommandText);
                    Assert.DoesNotContain("[Transfers]", command.CommandText);
                    Paused = true;
                    AtAggregate.TrySetResult();
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
            }
            return result;
        }
    }
}
