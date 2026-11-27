using System.Net;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class ReportContractTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("expired")]
    public async Task Reports_require_JWT_before_validating_the_query(string tokenKind)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        if (tokenKind == "malformed")
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid.jwt.token");
        }
        else if (tokenKind == "expired")
        {
            var now = TimeProvider.System.GetUtcNow().UtcDateTime;
            var token = new JwtSecurityToken("FinanceFlow.Tests", "FinanceFlow.Tests.Client",
                [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())], now.AddHours(-2), now.AddHours(-1),
                new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(factory.TestKey)), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        }

        using var response = await client.GetAsync("/api/v1/reports/summary?userId=invalid", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Openapi_exposes_only_the_summary_with_exact_aggregate_schemas_and_period_contract()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = json.RootElement.GetProperty("paths");
        var reportPaths = paths.EnumerateObject().Where(value => value.Name.StartsWith("/api/v1/reports", StringComparison.Ordinal));
        Assert.Equal(new[] { "/api/v1/reports/by-category", "/api/v1/reports/summary" }, reportPaths.Select(value => value.Name).Order());
        var operation = paths.GetProperty("/api/v1/reports/summary").GetProperty("get");
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.Contains("SNAPSHOT", operation.GetProperty("description").GetString());
        Assert.Contains("366", operation.GetProperty("description").GetString());
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Equal(3, parameters.Length);
        foreach (var name in new[] { "from", "to" })
        {
            var parameter = Assert.Single(parameters, value => value.GetProperty("name").GetString() == name);
            Assert.True(parameter.GetProperty("required").GetBoolean());
            Assert.Equal("date", parameter.GetProperty("schema").GetProperty("format").GetString());
        }
        var account = Assert.Single(parameters, value => value.GetProperty("name").GetString() == "accountId");
        Assert.False(account.TryGetProperty("required", out var required) && required.GetBoolean());
        Assert.Equal("uuid", account.GetProperty("schema").GetProperty("format").GetString());
        foreach (var status in new[] { "200", "400", "401", "404", "429", "500", "503" })
        {
            var headers = operation.GetProperty("responses").GetProperty(status).GetProperty("headers");
            Assert.True(headers.TryGetProperty("Cache-Control", out _));
            Assert.False(headers.TryGetProperty("ETag", out _));
        }
        var schema = json.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("FinancialSummary");
        var properties = schema.GetProperty("properties");
        Assert.Equal(6, properties.EnumerateObject().Count());
        foreach (var name in new[] { "income", "expense", "result" })
        {
            var amount = properties.GetProperty(name);
            Assert.Equal("number", amount.GetProperty("type").GetString());
            Assert.Equal(0.01m, amount.GetProperty("multipleOf").GetDecimal());
            Assert.False(amount.TryGetProperty("maximum", out _));
            Assert.Equal(name != "result", amount.TryGetProperty("minimum", out _));
        }
        Assert.Equal(4000m, schema.GetProperty("examples")[0].GetProperty("result").GetDecimal());
    }
}
