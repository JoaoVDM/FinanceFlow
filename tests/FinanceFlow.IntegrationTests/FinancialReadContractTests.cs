using System.Net;
using System.Text.Json;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class FinancialReadContractTests
{
    [Theory]
    [InlineData("/api/v1/transfers", false)]
    [InlineData("/api/v1/transfers", true)]
    [InlineData("/api/v1/accounts/11111111-1111-1111-1111-111111111111/statement", false)]
    [InlineData("/api/v1/accounts/11111111-1111-1111-1111-111111111111/statement", true)]
    public async Task Financial_reads_require_JWT_before_validating_query(string path, bool invalidToken)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        if (invalidToken)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid.jwt.token");
        }

        using var response = await client.GetAsync(path + "?invalid=1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Openapi_documents_both_read_contracts_and_signed_movement_references()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        foreach (var path in new[] { "/api/v1/transfers", "/api/v1/accounts/{id}/statement" })
        {
            var operation = root.GetProperty("paths").GetProperty(path).GetProperty("get");
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
            Assert.Contains("SNAPSHOT", operation.GetProperty("description").GetString());
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            Assert.DoesNotContain(parameters, value => value.GetProperty("name").GetString() is "If-Match" or "Idempotency-Key");
            foreach (var name in new[] { "from", "to", "page", "pageSize" })
            {
                var parameter = Assert.Single(parameters, value => value.GetProperty("name").GetString() == name);
                if (name is "from" or "to")
                {
                    Assert.Equal(path.EndsWith("/statement", StringComparison.Ordinal),
                        parameter.TryGetProperty("required", out var required) && required.GetBoolean());
                    Assert.Equal("date", parameter.GetProperty("schema").GetProperty("format").GetString());
                }
            }
            foreach (var status in new[] { "200", "400", "401", "404", "429", "503" })
            {
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
            }
            Assert.True(operation.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("Cache-Control", out _));
        }
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var movement = schemas.GetProperty("StatementMovement").GetProperty("properties");
        Assert.Equal(new[] { "income", "expense", "transferIn", "transferOut" },
            movement.GetProperty("nature").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(movement.TryGetProperty("operationId", out _));
        Assert.True(movement.TryGetProperty("operationType", out _));
        Assert.False(movement.TryGetProperty("runningBalance", out _));
        Assert.False(movement.TryGetProperty("userId", out _));
        var statement = schemas.GetProperty("AccountStatement").GetProperty("properties");
        foreach (var name in new[] { "accountOpeningDate", "accountOpeningBalance", "accountOpeningContribution", "openingBalance",
            "closingBalance", "income", "expense", "result", "transferIn", "transferOut", "items", "totalCount", "totalPages" })
        {
            Assert.True(statement.TryGetProperty(name, out _));
        }
        Assert.Equal(0m, statement.GetProperty("income").GetProperty("minimum").GetDecimal());
        Assert.False(statement.GetProperty("closingBalance").TryGetProperty("maximum", out _));
    }
}
