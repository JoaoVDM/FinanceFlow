using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceFlow.Api.Contracts.Transfers;
using FinanceFlow.Domain.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class TransferContractTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public async Task Transfers_require_jwt_and_return_uncached_safe_challenges(string method)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/transfers" + (method == "GET" ? "/" + Guid.NewGuid() : ""));
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("10.001")]
    [InlineData("10.0000000000000000000000000000000000001")]
    [InlineData("1000000000000")]
    [InlineData("\"10.00\"")]
    public void Original_invalid_money_tokens_are_never_silently_rounded(string amount) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateTransferRequest>("{\"Amount\":" + amount + "}"));

    [Fact]
    public void Migration_snapshot_constraints_and_sql_types_match_the_runtime_model()
    {
        using var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>()
            .UseSqlServer("Server=localhost;Database=UnusedModelCheck;Integrated Security=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261120000100_TransfersDelivery6A", db.Database.GetMigrations());
        var entity = db.Model.FindEntityType(typeof(Transfer))!;
        Assert.Equal(18, entity.FindProperty("Amount")!.GetPrecision());
        Assert.Equal(2, entity.FindProperty("Amount")!.GetScale());
        Assert.Equal("date", entity.FindProperty("OccurredOn")!.GetColumnType());
        Assert.True(entity.FindProperty("RowVersion")!.IsConcurrencyToken);
        Assert.Equal(3, entity.GetForeignKeys().Count());
        Assert.All(entity.GetForeignKeys(), key => Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior));
        Assert.Null(entity.FindProperty("Kind"));
        Assert.Null(entity.FindProperty("CategoryId"));
    }

    [Fact]
    public async Task Openapi_exposes_only_transfer_creation_and_individual_read_with_strong_headers()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = json.RootElement.GetProperty("paths");
        var collection = paths.GetProperty("/api/v1/transfers");
        var individual = paths.GetProperty("/api/v1/transfers/{id}");
        Assert.Equal(new[] { "post" }, collection.EnumerateObject().Select(value => value.Name));
        Assert.Equal(new[] { "get" }, individual.EnumerateObject().Select(value => value.Name));
        foreach (var operation in new[] { collection.GetProperty("post"), individual.GetProperty("get") })
        {
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
            Assert.DoesNotContain(operation.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "If-Match");
        }
        Assert.Contains(collection.GetProperty("post").GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key" && parameter.GetProperty("required").GetBoolean());
        var created = collection.GetProperty("post").GetProperty("responses").GetProperty("201").GetProperty("headers");
        Assert.True(created.TryGetProperty("Location", out _));
        Assert.True(created.TryGetProperty("ETag", out _));
        Assert.True(individual.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));
        var schema = json.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("CreateTransferRequest");
        Assert.Equal(new[] { "amount", "description", "destinationAccountId", "occurredOn", "sourceAccountId" }, schema.GetProperty("properties").EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal(0.01m, schema.GetProperty("properties").GetProperty("amount").GetProperty("multipleOf").GetDecimal());
    }
}
