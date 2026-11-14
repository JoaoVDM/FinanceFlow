using System.Net;
using System.Text.Json;
using FinanceFlow.Domain.Categories;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class CategoryInputAndModelTests
{
    private static CancellationToken TestCancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("GET", "/api/v1/categories")]
    [InlineData("GET", "/api/v1/categories/9a48df08-cf0f-4b85-a4c2-82262dbab2de")]
    [InlineData("POST", "/api/v1/categories")]
    public async Task All_category_operations_require_bearer_authentication(string method, string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, TestCancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Snapshot_matches_runtime_model_and_category_mapping_without_database_access()
    {
        using var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>()
            .UseSqlServer("Server=localhost;Database=UnusedCategoryModel;Integrated Security=True").Options);

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261114000200_CategoriesDeliveryB", db.Database.GetMigrations());
        var category = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Category))!;
        Assert.Equal("Latin1_General_100_BIN2", category.FindProperty(nameof(Category.NormalizedName))!.GetCollation());
        Assert.Equal("Latin1_General_100_BIN2", category.FindProperty(nameof(Category.Kind))!.GetCollation());
        Assert.Equal(100, category.FindProperty(nameof(Category.Name))!.GetMaxLength());
        Assert.Equal(100, category.FindProperty(nameof(Category.NormalizedName))!.GetMaxLength());
        Assert.True(category.FindProperty(nameof(Category.RowVersion))!.IsConcurrencyToken);
        Assert.Equal(DeleteBehavior.NoAction, Assert.Single(category.GetForeignKeys()).DeleteBehavior);
        var unique = Assert.Single(category.GetIndexes());
        Assert.True(unique.IsUnique);
        Assert.Null(unique.GetFilter());
        Assert.Equal(new[] { "UserId", "Kind", "NormalizedName" }, unique.Properties.Select(property => property.Name));
        Assert.Contains(category.GetKeys(), key => key.Properties.Select(property => property.Name)
            .SequenceEqual(new[] { "Id", "UserId", "Kind" }));
    }

    [Fact]
    public async Task Openapi_documents_categories_without_idempotency_or_future_operations()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestCancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestCancellationToken));
        var paths = document.RootElement.GetProperty("paths");
        var collection = paths.GetProperty("/api/v1/categories");
        var individual = paths.GetProperty("/api/v1/categories/{id}");
        var post = collection.GetProperty("post");
        var list = collection.GetProperty("get");
        var get = individual.GetProperty("get");
        foreach (var operation in new[] { post, list, get })
        {
            Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        }

        Assert.False(individual.TryGetProperty("put", out _));
        Assert.False(individual.TryGetProperty("patch", out _));
        Assert.False(individual.TryGetProperty("delete", out _));
        if (post.TryGetProperty("parameters", out var postParameters))
        {
            Assert.DoesNotContain(postParameters.EnumerateArray(), parameter =>
                parameter.GetProperty("name").GetString() == "Idempotency-Key");
        }

        var createdHeaders = post.GetProperty("responses").GetProperty("201").GetProperty("headers");
        Assert.True(createdHeaders.TryGetProperty("Location", out _));
        Assert.True(createdHeaders.TryGetProperty("ETag", out _));
        Assert.True(get.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));
        var parameters = list.GetProperty("parameters").EnumerateArray().ToDictionary(
            parameter => parameter.GetProperty("name").GetString()!, parameter => parameter.GetProperty("schema"));
        Assert.Equal(20, parameters["pageSize"].GetProperty("default").GetInt32());
        Assert.Equal(100, parameters["pageSize"].GetProperty("maximum").GetInt32());
        Assert.Equal(new[] { "income", "expense" }, parameters["kind"].GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("CreateCategoryRequest").GetProperty("properties");
        Assert.Equal(new[] { "kind", "name" }, properties.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(new[] { "income", "expense" }, properties.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));

        // Accounts retain their existing idempotency contract and no update endpoints.
        var accountPost = paths.GetProperty("/api/v1/accounts").GetProperty("post");
        Assert.Contains(accountPost.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key" && parameter.GetProperty("required").GetBoolean());
    }
}
