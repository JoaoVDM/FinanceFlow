using System.Net;
using System.Net.Http.Json;
using FinanceFlow.Application.Reports;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;

namespace FinanceFlow.IntegrationTests;

internal static class CategoryReportRequests
{
    internal static string CategoryPath(Guid? account = null, string? kind = null,
        string from = "2026-11-01", string to = "2026-11-26") =>
        $"/api/v1/reports/by-category?from={from}&to={to}" + (account is null ? "" : $"&accountId={account}")
        + (kind is null ? "" : $"&kind={kind}");

    internal static async Task<CategoryReportGroup[]> GroupsAsync(HttpClient client, Guid? account = null, string? kind = null,
        string from = "2026-11-01", string to = "2026-11-26")
    {
        using var response = await client.GetAsync(CategoryPath(account, kind, from, to), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
        var body = await ReadAsync(response);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, body.ValueKind);
        return body.EnumerateArray().Select(item =>
        {
            Assert.Equal(new[] { "categoryId", "kind", "name", "total" }, item.EnumerateObject().Select(value => value.Name).Order());
            var value = new CategoryReportGroup(item.GetProperty("categoryId").GetGuid(), item.GetProperty("name").GetString()!,
                item.GetProperty("kind").GetString()!, item.GetProperty("total").GetDecimal());
            Assert.True(value.Total > 0m);
            return value;
        }).ToArray();
    }

    internal static async Task<(Guid Id, Uri Location, string Version)> CategoryAsync(HttpClient client, string name, string kind)
    {
        using var response = await CategoryTestRequests.CreateAsync(client, name, kind);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await ReadAsync(response)).GetProperty("id").GetGuid(), response.Headers.Location!, response.Headers.ETag!.ToString());
    }

    internal static async Task<HttpResponseMessage> CategoryMovementAsync(HttpClient client, Guid account, Guid category,
        string kind, decimal amount, string date = "2026-11-01")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions")
        {
            Content = JsonContent.Create(new { accountId = account, categoryId = category, kind, amount, occurredOn = date })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response;
    }
}
