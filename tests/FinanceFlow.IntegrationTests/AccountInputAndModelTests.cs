using System.Text.Json;
using FinanceFlow.Api.Contracts.Accounts;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using System.Net;
using System.Text;

namespace FinanceFlow.IntegrationTests;

public sealed class AccountInputAndModelTests
{
    [Theory]
    [InlineData("/api/v1/accounts")]
    [InlineData("/api/v1/accounts/9a48df08-cf0f-4b85-a4c2-82262dbab2de")]
    public async Task Account_reads_require_authentication(string path)
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task Creation_requires_authentication_even_with_valid_request_shape()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/v1/accounts", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Openapi_documents_only_authorized_account_routes_and_headers()
    {
        using var factory = new ApiFactory("Development"); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = json.RootElement.GetProperty("paths");
        var collection = paths.GetProperty("/api/v1/accounts"); var individual = paths.GetProperty("/api/v1/accounts/{id}");
        Assert.True(collection.GetProperty("post").GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.Contains(collection.GetProperty("post").GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "Idempotency-Key" && x.GetProperty("required").GetBoolean());
        Assert.True(individual.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));
        Assert.False(individual.TryGetProperty("put", out _)); Assert.False(individual.TryGetProperty("patch", out _));
        Assert.False(paths.TryGetProperty("/api/v1/categories", out _));
        var amount = json.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CreateAccountRequest").GetProperty("properties").GetProperty("openingBalance");
        Assert.Equal("number", amount.GetProperty("type").GetString());
        Assert.Equal(0.01m, amount.GetProperty("multipleOf").GetDecimal());
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0.000", "0")]
    [InlineData("12.34", "12.34")]
    [InlineData("-12.34", "-12.34")]
    [InlineData("10.000", "10")]
    [InlineData("1e1", "10")]
    [InlineData("1234e-2", "12.34")]
    [InlineData("999999999999.99", "999999999999.99")]
    [InlineData("-999999999999.99", "-999999999999.99")]
    public void Money_json_is_read_without_binary_float_or_rounding(string number, string expected)
    {
        var request = JsonSerializer.Deserialize<CreateAccountRequest>("{\"OpeningBalance\":" + number + "}")!;
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), request.OpeningBalance);
    }

    [Theory]
    [InlineData("10.001")]
    [InlineData("-0.001")]
    [InlineData("1000000000000")]
    [InlineData("-1000000000000")]
    [InlineData("10.0000000000000000000000000000000000000001")]
    [InlineData("999999999999.99000000000000000000000000001")]
    [InlineData("1e-999999999")]
    [InlineData("1e999999999")]
    [InlineData("\"10.00\"")]
    public void Invalid_original_money_tokens_are_rejected(string number) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateAccountRequest>("{\"OpeningBalance\":" + number + "}"));

    [Fact]
    public void Incremental_snapshot_matches_runtime_model_without_database_access()
    {
        using var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>()
            .UseSqlServer("Server=localhost;Database=UnusedModelCheck;Integrated Security=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261114000100_AccountsDeliveryA", db.Database.GetMigrations());
    }
}
