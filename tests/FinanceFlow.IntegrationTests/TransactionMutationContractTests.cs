using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceFlow.Api.Contracts.Transactions;
using FinanceFlow.Domain.Transactions;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class TransactionMutationContractTests
{
    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Mutation_endpoints_require_jwt_and_preserve_no_store_and_challenge(string method)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/transactions/" + Guid.NewGuid());
        if (method == "PUT") request.Content = JsonContent.Create(new { });
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("10.001")]
    [InlineData("10.0000000000000000000000000000000000001")]
    [InlineData("1000000000000")]
    [InlineData("1e-999999999")]
    [InlineData("\"100.00\"")]
    public void Put_rejects_original_money_tokens_that_could_otherwise_be_rounded(string amount) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateTransactionRequest>("{\"Amount\":" + amount + "}"));

    [Fact]
    public void Runtime_preserves_schema_and_protects_immutable_properties_without_a_new_migration()
    {
        using var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>()
            .UseSqlServer("Server=localhost;Database=UnusedModelCheck;Integrated Security=True").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(5, db.Database.GetMigrations().Count());
        var transaction = db.Model.FindEntityType(typeof(FinancialTransaction))!;
        Assert.Equal(PropertySaveBehavior.Throw, transaction.FindProperty("UserId")!.GetAfterSaveBehavior());
        Assert.Equal(PropertySaveBehavior.Throw, transaction.FindProperty("CreatedAtUtc")!.GetAfterSaveBehavior());
        Assert.True(transaction.FindProperty("RowVersion")!.IsConcurrencyToken);
    }

    [Fact]
    public async Task Openapi_documents_only_put_and_delete_changes_with_strong_preconditions_and_no_idempotency()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = json.RootElement.GetProperty("paths");
        var resource = paths.GetProperty("/api/v1/transactions/{id}");
        Assert.Equal(new[] { "delete", "get", "put" }, resource.EnumerateObject().Select(value => value.Name).Order());
        foreach (var method in new[] { "put", "delete" })
        {
            var operation = resource.GetProperty(method);
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            Assert.Contains(parameters, parameter => parameter.GetProperty("name").GetString() == "If-Match" && parameter.GetProperty("required").GetBoolean());
            Assert.DoesNotContain(parameters, parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
            foreach (var status in new[] { "204", "400", "401", "404", "412", "428", "429", "503" })
            {
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
            }
            var success = operation.GetProperty("responses").GetProperty("204");
            Assert.False(success.TryGetProperty("content", out _));
            Assert.True(success.GetProperty("headers").TryGetProperty("ETag", out _));
        }
        Assert.True(resource.GetProperty("put").GetProperty("responses").TryGetProperty("409", out _));
        var schema = json.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("UpdateTransactionRequest");
        Assert.Equal(new[] { "accountId", "amount", "categoryId", "description", "kind", "occurredOn" }, schema.GetProperty("properties").EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal(0.01m, schema.GetProperty("properties").GetProperty("amount").GetProperty("multipleOf").GetDecimal());
        var create = paths.GetProperty("/api/v1/transactions").GetProperty("post");
        Assert.Contains(create.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key" && parameter.GetProperty("required").GetBoolean());
    }
}
