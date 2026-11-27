using System.Net;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.ReportTestRequests;
using static FinanceFlow.IntegrationTests.CategoryReportRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class CategoryReportValidationSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("")]
    [InlineData("from=2026-11-01")]
    [InlineData("from=&to=2026-11-26")]
    [InlineData("from=2026-11-1&to=2026-11-26")]
    [InlineData("from=2026-02-30&to=2026-11-26")]
    [InlineData("from=2026-11-01&to=2026-11-26&accountId=invalid")]
    [InlineData("from=2026-11-01&to=2026-11-26&accountId=00000000-0000-0000-0000-000000000000")]
    [InlineData("from=2026-11-01&to=2026-11-26&accountId=%20")]
    [InlineData("from=2026-11-01&to=2026-11-26&kind=transfer")]
    [InlineData("from=2026-11-01&to=2026-11-26&kind=Income")]
    [InlineData("from=2026-11-01&to=2026-11-26&kind=expense%20")]
    [InlineData("from=2026-11-01&to=2026-11-26&kind=")]
    [InlineData("from=2026-11-01&to=2026-11-26&kind=%20")]
    [InlineData("from=2026-11-01&to=2026-11-26&kind=income&KIND=expense")]
    [InlineData("from=2026-11-01&FROM=2026-11-02&to=2026-11-26")]
    [InlineData("from=2026-11-01&to=2026-11-26&userId=11111111-1111-1111-1111-111111111111")]
    [InlineData("from=2026-11-01&to=2026-11-26&sort=total")]
    [InlineData("from=2026-11-01&to=2026-11-26&page=1")]
    [InlineData("from=2024-01-01&to=2025-01-01")]
    [InlineData("from=2026-11-27&to=2026-11-27")]
    public async Task Strict_queries_fail_without_financial_data(string query)
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var response = await client.GetAsync("/api/v1/reports/by-category?" + query, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False((await ReadAsync(response)).TryGetProperty("total", out _));
    }

    [Fact]
    public async Task Casing_and_366_dates_are_supported_but_summary_still_rejects_kind()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var valid = await client.GetAsync("/api/v1/reports/by-category?FROM=2024-01-01&TO=2024-12-31&KIND=expense", Cancellation);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(0, (await ReadAsync(valid)).GetArrayLength());
        using var invalidSummary = await client.GetAsync(SummaryPath() + "&kind=expense", Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, invalidSummary.StatusCode);
        Assert.True(invalidSummary.Headers.CacheControl?.NoStore);
        Totals(await SummaryAsync(client), 0m, 0m);
    }

    [Fact]
    public async Task Foreign_and_missing_filters_remain_indistinguishable_and_real_revocation_blocks_the_report()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        using var other = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(other);
        var foreign = await AccountAsync(other);
        using var hidden = await client.GetAsync(CategoryPath(foreign.Id), Cancellation);
        using var missing = await client.GetAsync(CategoryPath(Guid.NewGuid()), Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(hidden.StatusCode, missing.StatusCode);
        Assert.Equal((await ReadAsync(hidden)).GetProperty("code").GetString(), (await ReadAsync(missing)).GetProperty("code").GetString());
        Assert.Equal((await ReadAsync(hidden)).GetProperty("title").GetString(), (await ReadAsync(missing)).GetProperty("title").GetString());
        Assert.True(hidden.Headers.CacheControl?.NoStore);
        using var profile = await client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(owner, (await ReadAsync(profile)).GetProperty("id").GetGuid());
        using var logout = await client.PostAsync("/api/v1/auth/logout", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revoked = await client.GetAsync(CategoryPath(), Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.True(revoked.Headers.CacheControl?.NoStore);
        Assert.Equal("Bearer", Assert.Single(revoked.Headers.WwwAuthenticate).Scheme);
    }
}
