using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using FinanceFlow.Application.Reports;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.CategoryReportRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class CategoryReportConsistencySqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("rename")]
    public async Task One_snapshot_keeps_current_names_and_totals_together_while_an_independent_writer_commits(string action)
    {
        var observer = new GroupObserver();
        using var factory = new ReportApiFactory(fixture, observer);
        using var reader = factory.CreateClient();
        using var writer = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(reader);
        writer.DefaultRequestHeaders.Authorization = reader.DefaultRequestHeaders.Authorization;
        var account = await AccountAsync(reader);
        var income = await CategoryAsync(reader, "Original income", "income");
        var expense = await CategoryAsync(reader, "Expense", "expense");
        using var original = await CategoryMovementAsync(reader, account.Id, income.Id, "income", 10m);
        using var removed = await CategoryMovementAsync(reader, account.Id, expense.Id, "expense", 3m);
        observer.Pause = true;

        var reading = GroupsAsync(reader, account.Id);
        try
        {
            // The account ownership SELECT established Snapshot before the group query.
            await observer.AtGroup.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            if (action == "create")
            {
                using var created = await CategoryMovementAsync(writer, account.Id, income.Id, "income", 7m);
            }
            else
            {
                using var request = action == "rename"
                    ? new HttpRequestMessage(HttpMethod.Put, income.Location) { Content = JsonContent.Create(new { name = "Renamed income" }) }
                    : new HttpRequestMessage(HttpMethod.Delete, removed.Headers.Location);
                request.Headers.Add("If-Match", action == "rename" ? income.Version : removed.Headers.ETag!.ToString());
                using var changed = await writer.SendAsync(request, Cancellation);
                Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
            }
        }
        finally
        {
            observer.Release.TrySetResult();
        }
        Assert.Equal(new[] { new CategoryReportGroup(expense.Id, "Expense", "expense", 3m), new(income.Id, "Original income", "income", 10m) }, await reading);
        Assert.Equal(2, observer.Commands);
        observer.Pause = false;
        var after = await GroupsAsync(reader, account.Id);
        Assert.Equal(action == "delete" ? 1 : 2, after.Length);
        Assert.Equal(new CategoryReportGroup(income.Id, action == "rename" ? "Renamed income" : "Original income", "income", action == "create" ? 17m : 10m), after.Last());
    }

    [Fact]
    public async Task Real_grouped_read_returns_the_same_physical_session_as_ReadCommitted_and_preserves_JWT_revocation()
    {
        var database = "FinanceFlowCategoryPool_" + Guid.NewGuid().ToString("N");
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString) { InitialCatalog = database, Pooling = false };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            InitialCatalog = database, ApplicationName = database, Pooling = true, MaxPoolSize = 1, MinPoolSize = 0
        };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        var output = TestContext.Current.TestOutputHelper!;
        var observer = new GroupObserver(message => output.WriteLine(message));
        using var factory = new ReportApiFactory(fixture, observer, connectionString: application.ConnectionString);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client);
        var category = await CategoryAsync(client, "Pooled history", "income");
        using var movement = await CategoryMovementAsync(client, account.Id, category.Id, "income", 1.01m);
        var before = await ProbeAsync(application.ConnectionString);
        observer.InspectSession = true;

        Assert.Equal(1.01m, Assert.Single(await GroupsAsync(client, account.Id)).Total);

        var after = await ProbeAsync(application.ConnectionString);
        Assert.Equal(before.Spid, observer.During!.Spid);
        Assert.Equal(before.ConnectionId, observer.During.ConnectionId);
        Assert.Equal(5, observer.During.Isolation);
        Assert.Equal(1, observer.During.Transactions);
        Assert.Equal(before.Spid, after.Spid);
        Assert.Equal(before.ConnectionId, after.ConnectionId);
        Assert.Equal(2, after.Isolation);
        Assert.Equal(0, after.Transactions);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
            var result = await scope.ServiceProvider.GetRequiredService<IReportStore>()
                .GetByCategoryAsync(owner, new(new(2026, 11, 1), new(2026, 11, 26)), Cancellation);
            Assert.Null(result.Error);
            Assert.Equal(1.01m, Assert.Single(result.Groups!).Total);
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
        using var profile = await client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(owner, (await ReadAsync(profile)).GetProperty("id").GetGuid());
        using var logout = await client.PostAsync("/api/v1/auth/logout", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revoked = await client.GetAsync(CategoryPath(), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("Bearer", Assert.Single(revoked.Headers.WwwAuthenticate).Scheme);
        Assert.Equal(after, await ProbeAsync(application.ConnectionString));
    }

    private sealed record Session(int Spid, Guid ConnectionId, int Isolation, int Transactions);
    private static async Task<Session> ProbeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Cancellation);
        return await ProbeAsync(connection, null, Cancellation, message => TestContext.Current.TestOutputHelper!.WriteLine(message));
    }
    private static async Task<Session> ProbeAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken token, Action<string> write)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CAST(@@SPID AS int), CAST(transaction_isolation_level AS int), @@TRANCOUNT FROM sys.dm_exec_sessions WHERE session_id=@@SPID";
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        var state = new Session(reader.GetInt32(0), connection.ClientConnectionId, reader.GetInt32(1), reader.GetInt32(2));
        write($"Category session: SPID={state.Spid}; ClientConnectionId={state.ConnectionId}; Isolation={state.Isolation}; TranCount={state.Transactions}");
        return state;
    }

    private sealed class GroupObserver(Action<string>? write = null) : DbCommandInterceptor
    {
        public bool Pause { get; set; }
        public bool InspectSession { get; set; }
        public int Commands { get; private set; }
        public Session? During { get; private set; }
        public TaskCompletionSource AtGroup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.Transaction?.IsolationLevel == IsolationLevel.Snapshot)
            {
                if (Pause)
                {
                    Commands++;
                }
                if (command.CommandText.Contains("GROUP BY", StringComparison.Ordinal) && command.CommandText.Contains("[Categories]", StringComparison.Ordinal))
                {
                    if (InspectSession && During is null)
                    {
                        During = await ProbeAsync((SqlConnection)command.Connection!, (SqlTransaction)command.Transaction, cancellationToken, write!);
                    }
                    if (Pause)
                    {
                        AtGroup.TrySetResult();
                        await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                    }
                }
            }
            return result;
        }
    }
}
