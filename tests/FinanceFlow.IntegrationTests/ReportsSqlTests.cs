using System.Net;
using System.Net.Http.Json;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.TransferCancellationRequests;
using static FinanceFlow.IntegrationTests.FinancialReadRequests;
using static FinanceFlow.IntegrationTests.ReportTestRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class ReportsSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Reference_flow_is_4000_not_11000_and_reconciles_with_statements_excluding_transfers()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var nubank = await AccountAsync(client, 5000m, "2026-11-01");
        var itau = await AccountAsync(client, 2000m, "2026-11-01");
        using var salary = await MovementAsync(client, nubank.Id, "income", 4500m, "2026-11-05");
        using var gym = await MovementAsync(client, nubank.Id, "expense", 150m, "2026-11-10");
        using var food = await MovementAsync(client, nubank.Id, "expense", 350m, "2026-11-15");
        using var transfer = await PostTransferAsync(client, new(nubank.Id, itau.Id, 1000m, new(2026, 11, 20), null));
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);

        Totals(await SummaryAsync(client), 4500m, 500m);
        var first = await SummaryAsync(client, nubank.Id);
        Totals(first, 4500m, 500m);
        Totals(await SummaryAsync(client, itau.Id), 0m, 0m);
        var firstStatement = await StatementAsync(client, nubank.Id, "2026-11-01", "2026-11-26");
        var secondStatement = await StatementAsync(client, itau.Id, "2026-11-01", "2026-11-26");
        Assert.Equal(first.GetProperty("result").GetDecimal(), firstStatement.GetProperty("result").GetDecimal());
        Assert.Equal(8000m, firstStatement.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(3000m, secondStatement.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(8000m, await BalanceAsync(client, nubank.Id, "2026-11-26"));
        Assert.Equal(3000m, await BalanceAsync(client, itau.Id, "2026-11-26"));
        Assert.Equal(11000m, firstStatement.GetProperty("closingBalance").GetDecimal() + secondStatement.GetProperty("closingBalance").GetDecimal());

        using var canceled = await CancelAsync(client, transfer.Headers.Location!, transfer.Headers.ETag!.ToString());
        await AssertSuccessAsync(canceled);
        Totals(await SummaryAsync(client), 4500m, 500m);
        Totals(await SummaryAsync(client, itau.Id), 0m, 0m);
    }

    [Fact]
    public async Task Distributed_accounts_and_other_owners_have_independent_totals_even_with_inactive_history()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        using var other = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(other);
        var a = await AccountAsync(client, -900m);
        var b = await AccountAsync(client, 800m);
        var foreign = await AccountAsync(other);
        using var incomeA = await MovementAsync(client, a.Id, "income", 10.01m, "2026-11-01");
        using var expenseA = await MovementAsync(client, a.Id, "expense", 20.02m, "2026-11-26");
        using var incomeB = await MovementAsync(client, b.Id, "income", 30.03m, "2026-11-26");
        using var expenseB = await MovementAsync(client, b.Id, "expense", 5.04m, "2026-11-01");
        using var foreignIncome = await MovementAsync(other, foreign.Id, "income", 999m, "2026-11-01");
        var category = (await ReadAsync(incomeA)).GetProperty("categoryId").GetGuid();
        using var getCategory = await client.GetAsync($"/api/v1/categories/{category}", Cancellation);
        await DeactivateAsync(client, getCategory.Headers.ETag!.ToString(), new Uri($"/api/v1/categories/{category}", UriKind.Relative));
        await DeactivateAsync(client, a.Version, a.Location);

        Totals(await SummaryAsync(client), 40.04m, 25.06m);
        Totals(await SummaryAsync(client, a.Id), 10.01m, 20.02m);
        Totals(await SummaryAsync(client, b.Id), 30.03m, 5.04m);
        Totals(await SummaryAsync(other), 999m, 0m);
        Totals(await SummaryAsync(client, a.Id, "2026-11-01", "2026-11-01"), 10.01m, 0m);
        Totals(await SummaryAsync(client, a.Id, "2026-11-26", "2026-11-26"), 0m, 20.02m);
    }

    [Fact]
    public async Task Empty_periods_and_accounts_opened_within_or_after_the_period_return_zero_flow()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        Totals(await SummaryAsync(client), 0m, 0m);
        var within = await AccountAsync(client, 999m, "2026-11-05");
        var after = await AccountAsync(client, -500m, "2026-11-26");
        Totals(await SummaryAsync(client, within.Id), 0m, 0m);
        Totals(await SummaryAsync(client, after.Id, "2026-11-01", "2026-11-10"), 0m, 0m);
        using var movement = await MovementAsync(client, within.Id, "income", 20m, "2026-11-06");
        Totals(await SummaryAsync(client, within.Id), 20m, 0m);
        Totals(await SummaryAsync(client, within.Id, "2026-11-01", "2026-11-05"), 0m, 0m);
    }

    [Fact]
    public async Task Edits_retroactive_entries_and_deletions_recalculate_the_current_valid_period()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var a = await AccountAsync(client);
        var b = await AccountAsync(client);
        using var before = await MovementAsync(client, a.Id, "income", 900m, "2026-10-31");
        using var after = await MovementAsync(client, a.Id, "expense", 800m, "2026-11-26");
        using var movement = await MovementAsync(client, a.Id, "income", 10m, "2026-11-02");
        using var expenseCategory = await CategoryTestRequests.CreateAsync(client, "Expense-" + Guid.NewGuid().ToString("N"), "expense");
        var categoryId = (await ReadAsync(expenseCategory)).GetProperty("id").GetGuid();
        using var edit = new HttpRequestMessage(HttpMethod.Put, movement.Headers.Location)
        {
            Content = JsonContent.Create(new { accountId = b.Id, categoryId, kind = "expense", amount = 30.03m, occurredOn = "2026-11-01" })
        };
        edit.Headers.Add("If-Match", movement.Headers.ETag!.ToString());
        using var edited = await client.SendAsync(edit, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        using var retroactive = await MovementAsync(client, a.Id, "income", 30.03m, "2026-11-01");

        Totals(await SummaryAsync(client, from: "2026-11-01", to: "2026-11-25"), 30.03m, 30.03m);
        Totals(await SummaryAsync(client, a.Id, "2026-11-01", "2026-11-25"), 30.03m, 0m);
        Totals(await SummaryAsync(client, b.Id), 0m, 30.03m);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, movement.Headers.Location);
        delete.Headers.Add("If-Match", edited.Headers.ETag!.ToString());
        using var deleted = await client.SendAsync(delete, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Totals(await SummaryAsync(client, from: "2026-11-01", to: "2026-11-25"), 30.03m, 0m);
        Totals(await SummaryAsync(client, b.Id), 0m, 0m);
    }

    [Fact]
    public async Task Aggregates_larger_than_one_transaction_preserve_exact_cents_using_restricted_SQL_credentials()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var a = await AccountAsync(client);
        using var first = await MovementAsync(client, a.Id, "income", 999999999999.99m, "2026-11-01");
        using var second = await MovementAsync(client, a.Id, "income", 999999999999.99m, "2026-11-26");
        using var expense = await MovementAsync(client, a.Id, "expense", 0.01m, "2026-11-01");

        Totals(await SummaryAsync(client), 1999999999999.98m, 0.01m);
        Totals(await SummaryAsync(client, a.Id), 1999999999999.98m, 0.01m);
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, Cancellation);
    }

    [Fact]
    public async Task Foreign_and_nonexistent_account_filters_are_indistinguishable_and_real_logout_revokes_access()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        using var other = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(other);
        var foreign = await AccountAsync(other);
        using var missing = await client.GetAsync(SummaryPath(Guid.NewGuid()), Cancellation);
        using var hidden = await client.GetAsync(SummaryPath(foreign.Id), Cancellation);
        foreach (var response in new[] { missing, hidden })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var problem = await ReadAsync(response);
            Assert.Equal("not_found", problem.GetProperty("code").GetString());
            Assert.False(problem.TryGetProperty("income", out _));
        }
        Assert.Equal((await ReadAsync(missing)).GetProperty("title").GetString(), (await ReadAsync(hidden)).GetProperty("title").GetString());
        using var profile = await client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(owner, (await ReadAsync(profile)).GetProperty("id").GetGuid());
        using var logout = await client.PostAsync("/api/v1/auth/logout", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revoked = await client.GetAsync(SummaryPath(), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("Bearer", Assert.Single(revoked.Headers.WwwAuthenticate).Scheme);
        Assert.True(revoked.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("")]
    [InlineData("from=2026-11-01")]
    [InlineData("to=2026-11-26")]
    [InlineData("from=&to=2026-11-26")]
    [InlineData("from=2026-11-01&to=")]
    [InlineData("from=2026-11-1&to=2026-11-26")]
    [InlineData("from=2026-02-30&to=2026-11-26")]
    [InlineData("from=2026-11-01T00:00:00Z&to=2026-11-26")]
    [InlineData("from=2026-11-01&from=2026-11-01&to=2026-11-26")]
    [InlineData("from=2026-11-01&FROM=2026-11-02&to=2026-11-26")]
    [InlineData("from=2026-11-01&to=2026-11-26&accountId=")]
    [InlineData("from=2026-11-01&to=2026-11-26&accountId=%20")]
    [InlineData("from=2026-11-01&to=2026-11-26&accountId=invalid")]
    [InlineData("from=2026-11-01&to=2026-11-26&accountId=00000000-0000-0000-0000-000000000000")]
    [InlineData("from=2026-11-01&to=2026-11-26&accountId=11111111-1111-1111-1111-111111111111&accountId=11111111-1111-1111-1111-111111111111")]
    [InlineData("from=2026-11-01&to=2026-11-26&userId=11111111-1111-1111-1111-111111111111")]
    [InlineData("from=2026-11-01&to=2026-11-26&page=1")]
    [InlineData("from=2024-01-01&to=2025-01-01")]
    [InlineData("from=2026-11-27&to=2026-11-27")]
    [InlineData("from=2026-11-26&to=2026-11-01")]
    public async Task Invalid_queries_are_400_Problem_Details_without_financial_data(string query)
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var response = await client.GetAsync("/api/v1/reports/summary?" + query, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False((await ReadAsync(response)).TryGetProperty("income", out _));
    }

    [Fact]
    public async Task Query_casing_and_the_exact_366_day_boundary_follow_the_existing_policy()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var response = await client.GetAsync("/api/v1/reports/summary?FROM=2024-01-01&TO=2024-12-31", Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Totals(await ReadAsync(response), 0m, 0m);
    }

    [Fact]
    public async Task Summary_is_subject_to_the_existing_authenticated_rate_limit()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        for (var attempt = 0; attempt <= 120; attempt++)
        {
            using var response = await client.GetAsync(SummaryPath(), Cancellation);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.NotNull(response.Headers.RetryAfter);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                return;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Fail("The authenticated report exceeded the configured rate limit without 429.");
    }

    private static async Task DeactivateAsync(HttpClient client, string version, Uri location)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, location) { Content = JsonContent.Create(new { isActive = false }) };
        request.Headers.Add("If-Match", version);
        using var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
