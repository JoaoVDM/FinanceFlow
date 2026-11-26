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
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.FinancialReadRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class FinancialReadFailureSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_SQL_read_timeout_is_safe_503_and_a_later_read_succeeds(bool statement)
    {
        var fault = new QueryFault(overflow: false);
        using var factory = new FailureFactory(fixture, fault);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client);
        var path = statement ? $"/api/v1/accounts/{account.Id}/statement?from=2000-01-01&to=2000-01-06" : "/api/v1/transfers";
        fault.Armed = true;

        using var failed = await client.GetAsync(path, Cancellation);

        Assert.True(fault.Injected);
        Assert.Equal(-2, fault.SqlNumber);
        await AssertSafeErrorAsync(failed);
        using var retry = await client.GetAsync(path, Cancellation);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(0m, await BalanceAsync(client, account.Id, "2000-01-06"));
    }

    [Fact]
    public async Task SQL_aggregate_overflow_is_observed_internally_and_never_returns_a_partial_statement()
    {
        var fault = new QueryFault(overflow: true);
        using var factory = new FailureFactory(fixture, fault);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client);
        using var first = await MovementAsync(client, account.Id, "income", 1m);
        using var second = await MovementAsync(client, account.Id, "income", 1m);
        fault.Armed = true;

        using var failed = await client.GetAsync($"/api/v1/accounts/{account.Id}/statement?from=2000-01-01&to=2000-01-06", Cancellation);

        Assert.True(fault.Injected);
        Assert.Equal(8115, fault.SqlNumber);
        await AssertSafeErrorAsync(failed);
        Assert.Equal(2m, (await StatementAsync(client, account.Id)).GetProperty("closingBalance").GetDecimal());
    }

    private static async Task AssertSafeErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("SqlException", body);
        Assert.DoesNotContain("SELECT", body);
        Assert.DoesNotContain("8115", body);
        Assert.DoesNotContain("closingBalance", body);
        Assert.DoesNotContain("items", body);
        Assert.Equal("service_unavailable", (await ReadAsync(response)).GetProperty("code").GetString());
    }

    private sealed class FailureFactory(IdentitySqlFixture fixture, QueryFault fault) : ApiFactory()
    {
        public override string ConnectionString => fixture.ApplicationConnectionString;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options =>
            {
                options.AddInterceptors(fault);
                options.LogTo((eventId, _) => eventId == CoreEventId.QueryIterationFailed, fault.QueryFailed);
            }));
        }
    }

    private sealed class QueryFault(bool overflow) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public int? SqlNumber { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.Transaction?.IsolationLevel == IsolationLevel.Snapshot
                && (!overflow || command.CommandText.Contains("SUM(", StringComparison.OrdinalIgnoreCase)))
            {
                Armed = false;
                Injected = true;
                if (overflow)
                {
                    // Test-only operands simulate a history whose SQL SUM exceeds decimal(38,2).
                    var original = command.CommandText;
                    command.CommandText = original.Replace("THEN [Amount] ELSE -[Amount]",
                        "THEN CAST(999999999999999999999999999999999999.99 AS decimal(38,2)) ELSE -[Amount]", StringComparison.Ordinal);
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
            if (eventData is DbContextTypeErrorEventData failure)
            {
                Observe(failure.Exception);
            }
        }
        private void Observe(Exception exception)
        {
            if (exception is InvalidOperationException { InnerException: { } wrapped })
            {
                exception = wrapped;
            }
            if (Injected && exception is SqlException sql)
            {
                SqlNumber = sql.Number;
            }
        }
    }
}
