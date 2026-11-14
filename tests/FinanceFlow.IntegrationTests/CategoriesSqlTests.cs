using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceFlow.Application.Categories;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.CategoryTestRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class CategoriesSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Creation_without_idempotency_key_persists_safe_contract_location_and_real_etag()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);

        using var created = await CreateAsync(client, " Alimentação ");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        var category = await ReadAsync(created);
        Assert.Equal("Alimentação", category.Name);
        Assert.Equal("expense", category.Kind);
        Assert.True(category.IsActive);
        Assert.Null(category.UpdatedAtUtc);
        Assert.Equal(TimeSpan.Zero, category.CreatedAtUtc.Offset);
        Assert.Equal(8, Convert.FromBase64String(category.Version).Length);
        Assert.Equal('"' + category.Version + '"', created.Headers.ETag?.ToString());
        Assert.Equal("/api/v1/categories/" + category.Id, created.Headers.Location?.OriginalString);
        var body = await created.Content.ReadAsStringAsync(TestCancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(new[] { "createdAtUtc", "id", "isActive", "kind", "name", "updatedAtUtc", "version" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order());

        using var retrieved = await client.GetAsync(created.Headers.Location, TestCancellationToken);

        Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        Assert.Equal(body, await retrieved.Content.ReadAsStringAsync(TestCancellationToken));
        Assert.Equal(created.Headers.ETag, retrieved.Headers.ETag);
        Assert.Equal("no-store", retrieved.Headers.CacheControl?.ToString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        var persisted = await db.Categories.AsNoTracking()
            .SingleAsync(record => record.Id == category.Id && record.UserId == owner, TestCancellationToken);
        Assert.Equal("ALIMENTAÇÃO", persisted.NormalizedName);
        Assert.Equal(owner, persisted.UserId);
        Assert.Equal(0, await db.IdempotencyRecords.CountAsync(record => record.UserId == owner, TestCancellationToken));
    }

    [Theory]
    [InlineData("Alimentação", " alimentação ")]
    [InlineData("Café", "CAFÉ")]
    [InlineData("Café", "Cafe\u0301")]
    public async Task Normalized_duplicate_is_409_including_case_trim_and_canonical_unicode(string original, string duplicate)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        using var first = await CreateAsync(client, original);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var response = await CreateAsync(client, duplicate);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestCancellationToken);
        Assert.DoesNotContain("IX_Categories", body);
        Assert.DoesNotContain(owner.ToString(), body);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Equal(1, await db.Categories.CountAsync(category => category.UserId == owner, TestCancellationToken));
    }

    [Fact]
    public async Task Accents_kinds_and_owners_define_distinct_namespaces()
    {
        using var factory = new SqlApiFactory(fixture);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        await AuthenticateAsync(firstClient);
        await AuthenticateAsync(secondClient);

        using var plain = await CreateAsync(firstClient, "Cafe");
        using var accent = await CreateAsync(firstClient, "Café");
        using var income = await CreateAsync(firstClient, "Café", "income");
        using var otherOwner = await CreateAsync(secondClient, "Café");

        foreach (var response in new[] { plain, accent, income, otherOwner })
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task Inactive_category_keeps_its_reserved_name()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        using var created = await CreateAsync(client, "Reserved");
        var category = await ReadAsync(created);
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.Categories SET IsActive=0 WHERE Id=@id";
        command.Parameters.AddWithValue("@id", category.Id);
        await command.ExecuteNonQueryAsync(TestCancellationToken);

        using var duplicate = await CreateAsync(client, " reserved ");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var retrieved = await client.GetAsync(created.Headers.Location, TestCancellationToken);
        Assert.False((await ReadAsync(retrieved)).IsActive);
        Assert.NotEqual(created.Headers.ETag, retrieved.Headers.ETag);
    }

    [Fact]
    public async Task Foreign_and_missing_categories_are_indistinguishable_and_counts_are_private()
    {
        using var factory = new SqlApiFactory(fixture);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        await AuthenticateAsync(firstClient);
        await AuthenticateAsync(secondClient);
        using var created = await CreateAsync(firstClient, "Private", "income");

        using var foreign = await secondClient.GetAsync(created.Headers.Location, TestCancellationToken);
        using var missing = await secondClient.GetAsync("/api/v1/categories/" + Guid.NewGuid(), TestCancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(missing.StatusCode, foreign.StatusCode);
        using var foreignProblem = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync(TestCancellationToken));
        using var missingProblem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync(TestCancellationToken));
        foreach (var field in new[] { "title", "code", "type" })
        {
            Assert.Equal(missingProblem.RootElement.GetProperty(field).GetString(), foreignProblem.RootElement.GetProperty(field).GetString());
        }

        var page = await secondClient.GetFromJsonAsync<CategoryPage>("/api/v1/categories?kind=income&isActive=true", TestCancellationToken);
        Assert.Empty(page!.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
    }

    [Fact]
    public async Task Pagination_filters_inactive_default_and_sorting_execute_on_owner_query()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        using var first = await CreateAsync(client, "A", "income");
        using var second = await CreateAsync(client, "A", "expense");
        using var third = await CreateAsync(client, "B");
        using var last = await CreateAsync(client, "Z");
        var lastId = (await ReadAsync(last)).Id;
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.Categories SET IsActive=0 WHERE Id=@id";
        command.Parameters.AddWithValue("@id", lastId);
        await command.ExecuteNonQueryAsync(TestCancellationToken);

        var page = await client.GetFromJsonAsync<CategoryPage>("/api/v1/categories?pageSize=2", TestCancellationToken);
        var repeated = await client.GetFromJsonAsync<CategoryPage>("/api/v1/categories?pageSize=2", TestCancellationToken);

        Assert.Equal(4, page!.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.All(page.Items, category => Assert.Equal("A", category.Name));
        Assert.Equal(page.Items.Select(category => category.Id), repeated!.Items.Select(category => category.Id));
        var inactive = await client.GetFromJsonAsync<CategoryPage>("/api/v1/categories?isActive=false&kind=expense", TestCancellationToken);
        Assert.Equal(lastId, Assert.Single(inactive!.Items).Id);
        Assert.Equal(1, inactive.TotalCount);
        var descending = await client.GetFromJsonAsync<CategoryPage>("/api/v1/categories?direction=desc", TestCancellationToken);
        Assert.Equal("Z", descending!.Items[0].Name);
        var byId = await client.GetFromJsonAsync<CategoryPage>("/api/v1/categories?sort=id&direction=desc", TestCancellationToken);
        // SQL uniqueidentifier ordering differs from Guid.CompareTo.
        Assert.Equal(byId!.Items.OrderByDescending(category => new System.Data.SqlTypes.SqlGuid(category.Id)).Select(category => category.Id),
            byId.Items.Select(category => category.Id));
        var empty = await client.GetFromJsonAsync<CategoryPage>("/api/v1/categories?page=99", TestCancellationToken);
        Assert.Empty(empty!.Items);
        Assert.Equal(4, empty.TotalCount);
        var defaults = await client.GetFromJsonAsync<CategoryPage>("/api/v1/categories", TestCancellationToken);
        Assert.Equal(1, defaults!.Page);
        Assert.Equal(20, defaults.PageSize);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("userId")]
    [InlineData("normalizedName")]
    [InlineData("isActive")]
    [InlineData("rowVersion")]
    [InlineData("createdAtUtc")]
    [InlineData("updatedAtUtc")]
    public async Task Server_controlled_properties_are_rejected(string field)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        var payload = new Dictionary<string, object?> { ["name"] = "Category", ["kind"] = "expense", [field] = null };

        using var response = await client.PostAsJsonAsync("/api/v1/categories", payload, TestCancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Equal(0, await db.Categories.CountAsync(category => category.UserId == owner, TestCancellationToken));
    }

    [Fact]
    public async Task Invalid_input_and_queries_are_400_without_persistence()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        var invalidBodies = new[]
        {
            "{}", "{", "{\"name\":\"Category\"}", "{\"kind\":\"income\"}",
            JsonSerializer.Serialize(new { name = " ", kind = "expense" }),
            JsonSerializer.Serialize(new { name = new string('x', 101), kind = "expense" }),
            JsonSerializer.Serialize(new { name = "Category", kind = "Income" }),
            JsonSerializer.Serialize(new { name = "Category", kind = "income " })
        };
        foreach (var body in invalidBodies)
        {
            using var response = await client.PostAsync("/api/v1/categories", new StringContent(body, Encoding.UTF8, "application/json"), TestCancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        foreach (var query in new[] { "page=0", "pageSize=101", "pageSize=0", "page=2147483647", "kind=Income", "isActive=no",
            "sort=normalizedName", "direction=ASC", "userId=anything", "kind=income&kind=expense", "kind=", "isActive=" })
        {
            using var response = await client.GetAsync("/api/v1/categories?" + query, TestCancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Equal(0, await db.Categories.CountAsync(category => category.UserId == owner, TestCancellationToken));
    }

    [Fact]
    public async Task Name_length_is_checked_after_nfc_and_never_truncated_by_sql()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var decomposedBoundary = string.Concat(Enumerable.Repeat("e\u0301", 100));

        using var response = await CreateAsync(client, " " + decomposedBoundary + " ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(new string('é', 100), (await ReadAsync(response)).Name);
    }

    [Fact]
    public async Task Concurrent_normalized_creations_have_one_confirmed_winner()
    {
        using var factory = new CategoryRaceFactory(fixture, new CategoryInsertGate());
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);

        var responses = await Task.WhenAll(CreateAsync(client, " Concurrent "), CreateAsync(client, "CONCURRENT"));
        try
        {
            Assert.Equal(new[] { 201, 409 }, responses.Select(response => (int)response.StatusCode).Order());
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
            Assert.Equal(1, await db.Categories.CountAsync(category => category.UserId == owner, TestCancellationToken));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Https_revocation_and_read_only_routes_remain_enforced()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        using var created = await CreateAsync(client, "Read only");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var http = await client.GetAsync("http://localhost/api/v1/categories", TestCancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, created.Headers.Location);
            using var response = await client.SendAsync(request, TestCancellationToken);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        using var logout = await client.PostAsync("/api/v1/auth/logout", null, TestCancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var revoked = await client.GetAsync(created.Headers.Location, TestCancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("Bearer", Assert.Single(revoked.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task Category_requests_share_the_authenticated_global_rate_limit()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);

        // Invalid sort avoids extra list queries, while real JWT state validation still uses SQL.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var invalid = await client.GetAsync("/api/v1/categories?sort=invalid", TestCancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using var limited = await client.GetAsync("/api/v1/categories", TestCancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
    }

    private sealed class CategoryRaceFactory(IdentitySqlFixture fixture, DbCommandInterceptor interceptor) : ApiFactory()
    {
        public override string ConnectionString => fixture.ApplicationConnectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(interceptor)));
        }
    }

    private sealed class CategoryInsertGate : DbCommandInterceptor
    {
        private int remaining = 2;
        private readonly TaskCompletionSource bothRequestsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [dbo].[Categories]", StringComparison.Ordinal))
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    bothRequestsReady.TrySetResult();
                }

                // Both real SQL INSERTs are ready before the unique index arbitrates the race.
                await bothRequestsReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }
}

internal static class CategoryTestRequests
{
    internal static CancellationToken TestCancellationToken => TestContext.Current.CancellationToken;

    internal static async Task<Guid> AuthenticateAsync(HttpClient client)
    {
        var (_, _, token) = await IdentitySqlTests.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Subject);
    }

    internal static Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, string kind = "expense") =>
        client.PostAsJsonAsync("/api/v1/categories", new { name, kind }, TestCancellationToken);

    internal static async Task<CategoryView> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<CategoryView>(TestCancellationToken))!;
}
