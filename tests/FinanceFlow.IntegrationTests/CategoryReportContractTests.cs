using System.Net;
using System.Text.Json;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class CategoryReportContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task By_category_requires_JWT_before_query_validation(bool malformedToken)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        if (malformedToken)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid.jwt.token");
        }
        using var response = await client.GetAsync("/api/v1/reports/by-category?userId=invalid", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Openapi_documents_the_array_kind_fixed_order_and_both_examples_without_changing_summary()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = json.RootElement.GetProperty("paths");
        var operation = paths.GetProperty("/api/v1/reports/by-category").GetProperty("get");
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.Contains("kind asc, total desc, categoryId asc", operation.GetProperty("description").GetString());
        Assert.Contains("SNAPSHOT", operation.GetProperty("description").GetString());
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(4, parameters.Length);
        foreach (var name in new[] { "from", "to" })
        {
            var parameter = Assert.Single(parameters, value => value.GetProperty("name").GetString() == name);
            Assert.True(parameter.GetProperty("required").GetBoolean());
            Assert.Equal("date", parameter.GetProperty("schema").GetProperty("format").GetString());
        }
        var kind = Assert.Single(parameters, value => value.GetProperty("name").GetString() == "kind");
        Assert.Equal(new[] { "income", "expense" }, kind.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        var media = operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json");
        Assert.Equal("array", media.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(3, media.GetProperty("examples").GetProperty("groups").GetProperty("value").GetArrayLength());
        Assert.Equal(0, media.GetProperty("examples").GetProperty("empty").GetProperty("value").GetArrayLength());
        foreach (var status in new[] { "200", "400", "401", "404", "429", "500", "503" })
        {
            Assert.True(operation.GetProperty("responses").GetProperty(status).GetProperty("headers").TryGetProperty("Cache-Control", out _));
        }
        var group = json.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CategoryReportGroup").GetProperty("properties");
        Assert.Equal(new[] { "categoryId", "kind", "name", "total" }, group.EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal(0.01m, group.GetProperty("total").GetProperty("multipleOf").GetDecimal());
        Assert.False(group.GetProperty("total").TryGetProperty("maximum", out _));
        var summary = paths.GetProperty("/api/v1/reports/summary").GetProperty("get");
        Assert.Equal(3, summary.GetProperty("parameters").GetArrayLength());
        Assert.DoesNotContain(summary.GetProperty("parameters").EnumerateArray(), value => value.GetProperty("name").GetString() == "kind");
    }
}
