using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FinanceFlow.Infrastructure.Persistence;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.FinancialReadRequests;
using static FinanceFlow.IntegrationTests.ReportTestRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class ReportFailureSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Missing_snapshot_configuration_is_a_safe_503_after_real_authentication_without_fallback()
    {
        var database = "FinanceFlowSummaryConfig_" + Guid.NewGuid().ToString("N");
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString) { InitialCatalog = database, Pooling = false };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString) { InitialCatalog = database, Pooling = false };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        int? observedSql = null;
        using var factory = new ReportApiFactory(fixture, queryFailure: failure =>
        {
            if (failure is DbContextTypeErrorEventData error)
            {
                var exception = error.Exception is InvalidOperationException { InnerException: { } inner } ? inner : error.Exception;
                if (exception is SqlException sql)
                {
                    observedSql = sql.Number;
                }
            }
        }, connectionString: application.ConnectionString);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        Totals(await SummaryAsync(client), 0m, 0m);
        await using (var admin = new SqlConnection(fixture.AdminConnectionString))
        {
            await admin.OpenAsync(Cancellation);
            await using var command = admin.CreateCommand();
            // Only this generated disposable database is reconfigured.
            command.CommandText = "ALTER DATABASE [" + database + "] SET ALLOW_SNAPSHOT_ISOLATION OFF";
            await command.ExecuteNonQueryAsync(Cancellation);
        }
        using var profile = await client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(owner, (await ReadAsync(profile)).GetProperty("id").GetGuid());

        using var response = await client.GetAsync(SummaryPath(), Cancellation);

        Assert.Equal(3952, observedSql);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("3952", body);
        Assert.DoesNotContain("\"income\"", body);
        using var ready = await client.GetAsync("/health/ready", Cancellation);
        using var live = await client.GetAsync("/health/live", Cancellation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Theory]
    [InlineData("timeout", HttpStatusCode.ServiceUnavailable, -2)]
    [InlineData("overflow", HttpStatusCode.ServiceUnavailable, 8115)]
    [InlineData("unexpected", HttpStatusCode.InternalServerError, null)]
    public async Task A_failed_aggregate_never_returns_partial_totals_and_a_later_read_succeeds(string stage, HttpStatusCode status, int? sqlNumber)
    {
        var fault = new AggregateFault(stage);
        using var factory = new ReportApiFactory(fixture, fault, fault.QueryFailed);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client);
        using var first = await MovementAsync(client, account.Id, "income", 1m, "2026-11-01");
        using var second = await MovementAsync(client, account.Id, "income", 1m, "2026-11-26");
        fault.Armed = true;

        using var response = await client.GetAsync(SummaryPath(account.Id), Cancellation);

        Assert.True(fault.Injected);
        Assert.Equal(sqlNumber, fault.SqlNumber);
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        foreach (var forbidden in new[] { "\"income\"", "\"expense\"", "\"result\"", "SqlException", "8115", "SELECT", "Injected", "stackTrace" })
        {
            Assert.DoesNotContain(forbidden, body);
        }
        Totals(await SummaryAsync(client, account.Id), 2m, 0m);
    }

    private sealed class AggregateFault(string stage) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public int? SqlNumber { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.Transaction?.IsolationLevel == IsolationLevel.Snapshot
                && command.CommandText.Contains("SUM(", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("[FinancialTransactions]", StringComparison.Ordinal))
            {
                Armed = false;
                Injected = true;
                if (stage == "unexpected")
                {
                    throw new IOException("Injected report read failure.");
                }
                if (stage == "overflow")
                {
                    var original = command.CommandText;
                    // Change only CASE operands; keep the derived table's named Amount projection.
                    command.CommandText = Regex.Replace(original, @"\bTHEN\s+\[[^\]]+\]\.\[Amount\]",
                        "THEN CAST(999999999999999999999999999999999999.99 AS decimal(38,2))");
                    Assert.NotEqual(original, command.CommandText);
                }
                else
                {
                    command.CommandTimeout = 1;
                    command.CommandText = "WAITFOR DELAY '00:00:02';\n" + command.CommandText;
                }
            }
            return ValueTask.FromResult(result);
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Observe(eventData.Exception);
            return Task.CompletedTask;
        }
        public void QueryFailed(EventData eventData)
        {
            if (eventData is DbContextTypeErrorEventData error)
            {
                Observe(error.Exception);
            }
        }
        private void Observe(Exception exception)
        {
            if (exception is InvalidOperationException { InnerException: { } inner })
            {
                exception = inner;
            }
            if (Injected && exception is SqlException sql)
            {
                SqlNumber = sql.Number;
            }
        }
    }
}
