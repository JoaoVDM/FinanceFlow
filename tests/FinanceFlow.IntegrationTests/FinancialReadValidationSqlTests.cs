using System.Net;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.FinancialReadRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class FinancialReadValidationSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("unknown=1")]
    [InlineData("page=1&page=2")]
    [InlineData("page=1&PAGE=2")]
    [InlineData("page=")]
    [InlineData("page=0")]
    [InlineData("page=2147483647")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("accountId=")]
    [InlineData("accountId=00000000-0000-0000-0000-000000000000")]
    [InlineData("accountId=not-a-guid")]
    [InlineData("from=01-01-2000")]
    [InlineData("from=2000-1-1")]
    [InlineData("from=")]
    [InlineData("from=2001-01-01&to=2000-01-01")]
    [InlineData("to=2026-11-22")]
    [InlineData("sort=description")]
    [InlineData("sort=")]
    [InlineData("direction=DESC")]
    public async Task Transfer_list_strictly_rejects_invalid_queries(string query)
    {
        using var factory = new FinancialReadApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);

        using var response = await client.GetAsync("/api/v1/transfers?" + query, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("from=2000-01-01")]
    [InlineData("to=2000-01-01")]
    [InlineData("from=&to=2000-01-01")]
    [InlineData("from=2000-1-1&to=2000-01-01")]
    [InlineData("from=01-01-2000&to=2000-01-01")]
    [InlineData("from=2000-02-01&to=2000-01-01")]
    [InlineData("from=2024-01-01&to=2025-01-01")]
    [InlineData("from=0001-01-01&to=9999-12-31")]
    [InlineData("from=2026-11-21&to=2026-11-22")]
    [InlineData("from=2000-01-01&from=2000-01-01&to=2000-01-02")]
    [InlineData("from=2000-01-01&to=2000-01-02&page=0")]
    [InlineData("from=2000-01-01&to=2000-01-02&pageSize=101")]
    [InlineData("from=2000-01-01&to=2000-01-02&page=2147483647")]
    [InlineData("from=2000-01-01&to=2000-01-02&sort=amount")]
    [InlineData("from=2000-01-01&to=2000-01-02&accountId=anything")]
    public async Task Statement_strictly_validates_required_exact_dates_and_range(string query)
    {
        using var factory = new FinancialReadApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);

        using var response = await client.GetAsync($"/api/v1/accounts/{Guid.NewGuid()}/statement?" + query, Cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Own_inactive_accounts_remain_readable_but_foreign_missing_and_revoked_access_are_safe()
    {
        using var factory = new FinancialReadApiFactory(fixture);
        using var owner = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(owner);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var account = await AccountAsync(owner, -10.01m);
        await TransactionMutationRequests.DeactivateAsync(owner, account.Location, account.Version);

        Assert.Equal(-10.01m, (await StatementAsync(owner, account.Id)).GetProperty("closingBalance").GetDecimal());
        using var foreign = await stranger.GetAsync($"/api/v1/accounts/{account.Id}/statement?from=2000-01-01&to=2000-01-01", Cancellation);
        using var missing = await stranger.GetAsync($"/api/v1/accounts/{Guid.NewGuid()}/statement?from=2000-01-01&to=2000-01-01", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(foreign.StatusCode, missing.StatusCode);
        Assert.Equal((await ReadAsync(foreign)).GetProperty("code").GetString(), (await ReadAsync(missing)).GetProperty("code").GetString());
        using var leapPeriod = await owner.GetAsync($"/api/v1/accounts/{account.Id}/statement?from=2024-01-01&to=2024-12-31", Cancellation);
        Assert.Equal(HttpStatusCode.OK, leapPeriod.StatusCode);
        using var logout = await owner.PostAsync("/api/v1/auth/logout", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        foreach (var path in new[] { "/api/v1/transfers", $"/api/v1/accounts/{account.Id}/statement?from=2000-01-01&to=2000-01-01" })
        {
            using var rejected = await owner.GetAsync(path, Cancellation);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal("Bearer", Assert.Single(rejected.Headers.WwwAuthenticate).Scheme);
            Assert.True(rejected.Headers.CacheControl?.NoStore);
        }
    }
}
