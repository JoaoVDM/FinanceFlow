using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.CategoryReportRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class CategoryReportFailureSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("overflow", HttpStatusCode.ServiceUnavailable, 8115)]
    [InlineData("timeout", HttpStatusCode.ServiceUnavailable, -2)]
    [InlineData("unexpected", HttpStatusCode.InternalServerError, null)]
    public async Task Failed_group_aggregation_never_streams_partial_groups_and_can_be_repeated(string stage, HttpStatusCode status, int? sqlNumber)
    {
        var fault = new GroupFault(stage);
        using var factory = new ReportApiFactory(fixture, fault, fault.QueryFailed);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client);
        var category = await CategoryAsync(client, "Overflow group", "income");
        using var first = await CategoryMovementAsync(client, account.Id, category.Id, "income", 1m);
        using var second = await CategoryMovementAsync(client, account.Id, category.Id, "income", 1m);
        fault.Armed = true;

        using var response = await client.GetAsync(CategoryPath(account.Id), Cancellation);

        Assert.True(fault.Injected);
        Assert.Equal(sqlNumber, fault.SqlNumber);
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        foreach (var forbidden in new[] { "\"categoryId\"", "\"total\"", "SqlException", "8115", "8155", "SELECT", "Injected", "stackTrace" })
        {
            Assert.DoesNotContain(forbidden, body);
        }
        Assert.Equal(2m, Assert.Single(await GroupsAsync(client, account.Id)).Total);
    }

    private sealed class GroupFault(string stage) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public int? SqlNumber { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.Transaction?.IsolationLevel == IsolationLevel.Snapshot
                && command.CommandText.Contains("GROUP BY", StringComparison.Ordinal) && command.CommandText.Contains("[Categories]", StringComparison.Ordinal))
            {
                Armed = false;
                Injected = true;
                if (stage == "overflow")
                {
                    const string sum = @"SUM\(\s*\[[^\]]+\]\.\[Amount\]\s*\)";
                    Assert.Matches(sum, command.CommandText);
                    // Only the first SUM (the projection), not its ORDER BY repetition.
                    // Preserve all aliases, join keys and inner projections.
                    command.CommandText = new Regex(sum).Replace(command.CommandText,
                        "SUM(CAST(999999999999999999999999999999999999.99 AS decimal(38,2)))", 1);
                }
                else if (stage == "timeout")
                {
                    command.CommandTimeout = 1;
                    command.CommandText = "WAITFOR DELAY '00:00:02';\n" + command.CommandText;
                }
                else
                {
                    throw new IOException("Injected category group failure.");
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
