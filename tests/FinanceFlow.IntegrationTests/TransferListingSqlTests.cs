using System.Data.SqlTypes;
using System.Net;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.TransferCancellationRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransferListingSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("occurredOn", "asc")]
    [InlineData("occurredOn", "desc")]
    [InlineData("amount", "asc")]
    [InlineData("amount", "desc")]
    [InlineData("id", "asc")]
    [InlineData("id", "desc")]
    public async Task SQL_filters_pages_and_orders_each_own_valid_transfer_once(string sort, string direction)
    {
        using var factory = new FinancialReadApiFactory(fixture);
        using var client = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var a = await AccountAsync(client);
        var b = await AccountAsync(client);
        var c = await AccountAsync(client);
        using var first = await PostTransferAsync(client, new(a.Id, b.Id, 10.01m, new(2000, 1, 1), null));
        using var second = await PostTransferAsync(client, new(b.Id, a.Id, 10.01m, new(2000, 1, 1), null));
        using var third = await PostTransferAsync(client, new(a.Id, c.Id, 20.02m, new(2000, 1, 2), null));
        using var canceled = await PostTransferAsync(client, new(a.Id, b.Id, 999m, new(2000, 1, 2), null));
        using var cancel = await CancelAsync(client, canceled.Headers.Location!, canceled.Headers.ETag!.ToString());
        await AssertSuccessAsync(cancel);
        var foreignA = await AccountAsync(stranger);
        var foreignB = await AccountAsync(stranger);
        using var foreign = await PostTransferAsync(stranger, new(foreignA.Id, foreignB.Id, 9999m, new(2000, 1, 2), null));
        await TransactionMutationRequests.DeactivateAsync(client, a.Location, a.Version);

        var expected = new[] { await ReadAsync(first), await ReadAsync(second), await ReadAsync(third) };
        var ordered = (sort, direction) switch
        {
            ("id", "asc") => expected.OrderBy(value => new SqlGuid(value.GetProperty("id").GetGuid())),
            ("id", "desc") => expected.OrderByDescending(value => new SqlGuid(value.GetProperty("id").GetGuid())),
            ("amount", "asc") => expected.OrderBy(value => value.GetProperty("amount").GetDecimal()).ThenBy(value => new SqlGuid(value.GetProperty("id").GetGuid())),
            ("amount", "desc") => expected.OrderByDescending(value => value.GetProperty("amount").GetDecimal()).ThenBy(value => new SqlGuid(value.GetProperty("id").GetGuid())),
            ("occurredOn", "asc") => expected.OrderBy(value => value.GetProperty("occurredOn").GetString()).ThenBy(value => new SqlGuid(value.GetProperty("id").GetGuid())),
            _ => expected.OrderByDescending(value => value.GetProperty("occurredOn").GetString()).ThenBy(value => new SqlGuid(value.GetProperty("id").GetGuid()))
        };
        var expectedIds = ordered.Select(value => value.GetProperty("id").GetGuid()).ToArray();
        var actualIds = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            using var response = await client.GetAsync($"/api/v1/transfers?accountId={a.Id}&sort={sort}&direction={direction}&pageSize=2&page={page}", Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await ReadAsync(response);
            Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, body.GetProperty("totalPages").GetInt32());
            foreach (var item in body.GetProperty("items").EnumerateArray())
            {
                Assert.False(item.TryGetProperty("userId", out _));
                Assert.Equal(8, Convert.FromBase64String(item.GetProperty("version").GetString()!).Length);
                actualIds.Add(item.GetProperty("id").GetGuid());
            }
        }
        Assert.Equal(expectedIds, actualIds);
        Assert.Equal(3, actualIds.Distinct().Count());
        using var period = await client.GetAsync("/api/v1/transfers?from=2000-01-02&to=2000-01-02", Cancellation);
        Assert.Equal(1, (await ReadAsync(period)).GetProperty("totalCount").GetInt32());
        using var empty = await client.GetAsync("/api/v1/transfers?to=0001-01-01", Cancellation);
        Assert.Equal(0, (await ReadAsync(empty)).GetProperty("totalPages").GetInt32());
        using var wide = await client.GetAsync("/api/v1/transfers?from=0001-01-01&to=2001-01-01", Cancellation);
        Assert.Equal(HttpStatusCode.OK, wide.StatusCode);
        using var foreignFilter = await client.GetAsync($"/api/v1/transfers?accountId={foreignA.Id}", Cancellation);
        using var missingFilter = await client.GetAsync($"/api/v1/transfers?accountId={Guid.NewGuid()}", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, foreignFilter.StatusCode);
        Assert.Equal(foreignFilter.StatusCode, missingFilter.StatusCode);
        Assert.Equal((await ReadAsync(foreignFilter)).GetProperty("code").GetString(), (await ReadAsync(missingFilter)).GetProperty("code").GetString());
    }
}
