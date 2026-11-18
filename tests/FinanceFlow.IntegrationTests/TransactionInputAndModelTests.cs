using System.Net;
using System.Text.Json;
using FinanceFlow.Api.Contracts.Transactions;
using FinanceFlow.Domain.Transactions;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class TransactionInputAndModelTests
{
    [Theory]
    [InlineData("0.01")]
    [InlineData("10.000")]
    [InlineData("999999999999.99")]
    [InlineData("1234e-2")]
    public void Transaction_json_reuses_exact_decimal_parsing(string amount)
    {
        var request = JsonSerializer.Deserialize<CreateTransactionRequest>("{\"Amount\":" + amount + "}")!;
        Assert.NotNull(request.Amount);
    }

    [Theory]
    [InlineData("10.001")]
    [InlineData("10.00000000000000000000000000000000000000000001")]
    [InlineData("999999999999.990000000000000000000000000000001")]
    [InlineData("1000000000000")]
    [InlineData("1e-999999999")]
    [InlineData("\"10.00\"")]
    public void Invalid_original_numeric_tokens_never_round_into_valid_amounts(string amount) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateTransactionRequest>("{\"Amount\":" + amount + "}"));

    [Theory]
    [InlineData("/api/v1/transactions")]
    [InlineData("/api/v1/transactions/41bbeff3-8bbe-4558-9a56-ffb7bc98cd30")]
    [InlineData("/api/v1/accounts/41bbeff3-8bbe-4558-9a56-ffb7bc98cd30/balance")]
    public async Task New_reads_require_jwt_and_are_not_cached(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        foreach (var address in new[] { path, path + "/" })
        {
            using var response = await client.GetAsync(address, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public void Model_snapshot_migration_relationships_and_aggregate_query_are_coherent()
    {
        using var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>()
            .UseSqlServer("Server=localhost;Database=UnusedModelCheck;Integrated Security=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261117000100_TransactionsDelivery5A", db.Database.GetMigrations());
        var entity = db.Model.FindEntityType(typeof(FinancialTransaction))!;
        Assert.Equal(18, entity.FindProperty("Amount")!.GetPrecision());
        Assert.Equal(2, entity.FindProperty("Amount")!.GetScale());
        Assert.True(entity.FindProperty("RowVersion")!.IsConcurrencyToken);
        Assert.Equal(3, entity.GetForeignKeys().Count());
        Assert.All(entity.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior));
        var account = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var cutoff = new DateOnly(2026, 11, 17);
        var sql = db.FinancialAccounts.Where(value => value.Id == account && value.UserId == owner)
            .Select(value => new
            {
                value.OpeningBalance,
                Net = db.FinancialTransactions.Where(transaction => transaction.UserId == owner && transaction.AccountId == account
                    && !transaction.IsDeleted && transaction.OccurredOn <= cutoff)
                    .Sum(transaction => (decimal?)(transaction.Kind == "income" ? transaction.Amount : -transaction.Amount)) ?? 0m
            }).ToQueryString();
        Assert.Contains("SUM", sql);
        Assert.Contains("[IsDeleted]", sql);
        Assert.DoesNotContain("CurrentBalance", sql);
    }

    [Fact]
    public async Task Openapi_exposes_only_5a_contracts_with_bearer_idempotency_and_exact_money()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = json.RootElement.GetProperty("paths");
        var collection = paths.GetProperty("/api/v1/transactions");
        var individual = paths.GetProperty("/api/v1/transactions/{id}");
        Assert.Equal(new[] { "get", "post" }, collection.EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal(new[] { "get" }, individual.EnumerateObject().Select(value => value.Name));
        foreach (var operation in new[] { collection.GetProperty("post"), collection.GetProperty("get"), individual.GetProperty("get"),
            paths.GetProperty("/api/v1/accounts/{id}/balance").GetProperty("get") })
        {
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        }
        var post = collection.GetProperty("post");
        Assert.Contains(post.GetProperty("parameters").EnumerateArray(), value => value.GetProperty("name").GetString() == "Idempotency-Key" && value.GetProperty("required").GetBoolean());
        var headers = post.GetProperty("responses").GetProperty("201").GetProperty("headers");
        Assert.True(headers.TryGetProperty("ETag", out _));
        Assert.True(headers.TryGetProperty("Location", out _));
        var amount = json.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CreateTransactionRequest").GetProperty("properties").GetProperty("amount");
        Assert.Equal("number", amount.GetProperty("type").GetString());
        Assert.Equal(0.01m, amount.GetProperty("minimum").GetDecimal());
        Assert.Equal(0.01m, amount.GetProperty("multipleOf").GetDecimal());
        Assert.False(paths.GetProperty("/api/v1/accounts/{id}/balance").GetProperty("get").GetProperty("responses").GetProperty("200").TryGetProperty("headers", out _));
    }
}
