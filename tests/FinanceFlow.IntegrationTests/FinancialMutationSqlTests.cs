using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static FinanceFlow.IntegrationTests.FinancialChangeRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class FinancialMutationSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("accounts", "PUT")]
    [InlineData("accounts", "PATCH")]
    [InlineData("categories", "PUT")]
    [InlineData("categories", "PATCH")]
    public async Task Valid_changes_are_204_with_current_etag_utc_timestamp_and_immutable_data(string resource, string method)
    {
        using var factory = new FinancialChangeApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var created = await CreateResourceAsync(client, resource);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var before = await BodyAsync(created);

        using var changed = await ChangeAsync(client, created.Headers.Location!, method, Command(resource, method, " Cafe\u0301 "), created.Headers.ETag!.ToString());

        await AssertSuccessAsync(changed);
        Assert.NotEqual(created.Headers.ETag, changed.Headers.ETag);
        using var retrieved = await client.GetAsync(created.Headers.Location, TestCancellationToken);
        Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        Assert.Equal(changed.Headers.ETag, retrieved.Headers.ETag);
        var current = await BodyAsync(retrieved);
        Assert.Equal(factory.Instant, current.GetProperty("updatedAtUtc").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, current.GetProperty("updatedAtUtc").GetDateTimeOffset().Offset);
        Assert.Equal(before.GetProperty("createdAtUtc").GetRawText(), current.GetProperty("createdAtUtc").GetRawText());
        Assert.Equal(before.GetProperty("id").GetGuid(), current.GetProperty("id").GetGuid());
        if (method == "PUT")
        {
            Assert.Equal("Café", current.GetProperty("name").GetString());
            if (resource == "accounts")
            {
                Assert.Equal("cash", current.GetProperty("type").GetString());
            }
        }
        else
        {
            Assert.Equal(before.GetProperty("name").GetRawText(), current.GetProperty("name").GetRawText());
            Assert.False(current.GetProperty("isActive").GetBoolean());
            using var reactivated = await ChangeAsync(client, created.Headers.Location!, "PATCH", new { isActive = true }, changed.Headers.ETag!.ToString());
            await AssertSuccessAsync(reactivated);
            using var active = await client.GetAsync(created.Headers.Location, TestCancellationToken);
            Assert.True((await BodyAsync(active)).GetProperty("isActive").GetBoolean());
        }

        foreach (var field in resource == "accounts" ? new[] { "currency", "openingBalance", "openingDate" } : new[] { "kind" })
        {
            Assert.Equal(before.GetProperty(field).GetRawText(), current.GetProperty(field).GetRawText());
        }
    }

    [Theory]
    [InlineData("accounts", "PUT")]
    [InlineData("accounts", "PATCH")]
    [InlineData("categories", "PUT")]
    [InlineData("categories", "PATCH")]
    public async Task Missing_or_invalid_preconditions_never_modify_the_resource(string resource, string method)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var created = await CreateResourceAsync(client, resource);
        var command = Command(resource, method);

        using var missing = await ChangeAsync(client, created.Headers.Location!, method, command, null);

        await AssertProblemAsync(missing, 428, "precondition_required");

        // HttpClient/TestServer drops an empty header value. Set it on the incoming context
        // so this case exercises a present-but-empty header through the authenticated API pipeline.
        using var emptyHeaderClient = new HttpClient(factory.Server.CreateHandler(context =>
            context.Request.Headers["If-Match"] = string.Empty))
        {
            BaseAddress = client.BaseAddress
        };
        emptyHeaderClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        using var empty = await ChangeAsync(emptyHeaderClient, created.Headers.Location!, method, command, "");
        await AssertProblemAsync(empty, 400, "validation_error");

        foreach (var header in new[] { "*", "W/\"AAAAAAAAAAE=\"", "\"AAAAAAAAAAE=\",\"AAAAAAAAAAI=\"", "AAAAAAAAAAE=",
            "\"invalid!!!==\"", "\"AAAAAAAAAA==\"", "\"AAAAAAAAAAAA\"", "\"AAAAAAAAAAJ=\"" })
        {
            using var invalid = await ChangeAsync(client, created.Headers.Location!, method, command, header);

            await AssertProblemAsync(invalid, 400, "validation_error");
        }

        using var multiple = new HttpRequestMessage(new HttpMethod(method), created.Headers.Location) { Content = JsonContent.Create(command) };
        multiple.Headers.TryAddWithoutValidation("If-Match", new[] { created.Headers.ETag!.ToString(), created.Headers.ETag.ToString() });
        using var multipleResponse = await client.SendAsync(multiple, TestCancellationToken);
        await AssertProblemAsync(multipleResponse, 400, "validation_error");
        using var current = await client.GetAsync(created.Headers.Location, TestCancellationToken);
        Assert.Equal(created.Headers.ETag, current.Headers.ETag);
        Assert.Equal(await created.Content.ReadAsStringAsync(TestCancellationToken), await current.Content.ReadAsStringAsync(TestCancellationToken));
    }

    [Theory]
    [InlineData("accounts", "PUT")]
    [InlineData("accounts", "PATCH")]
    [InlineData("categories", "PUT")]
    [InlineData("categories", "PATCH")]
    public async Task No_op_commands_still_use_sql_concurrency_and_advance_version_with_a_fixed_clock(string resource, string method)
    {
        using var factory = new FinancialChangeApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var created = await CreateResourceAsync(client, resource);
        object command = new { name = "Original" };
        if (method == "PATCH")
        {
            command = new { isActive = true };
        }
        else if (resource == "accounts")
        {
            command = new { name = "Original", type = "checking" };
        }

        using var first = await ChangeAsync(client, created.Headers.Location!, method, command, created.Headers.ETag!.ToString());
        await AssertSuccessAsync(first);
        using var repeated = await ChangeAsync(client, created.Headers.Location!, method, command, first.Headers.ETag!.ToString());

        await AssertSuccessAsync(repeated);
        Assert.NotEqual(created.Headers.ETag, first.Headers.ETag);
        Assert.NotEqual(first.Headers.ETag, repeated.Headers.ETag);
        using var stale = await ChangeAsync(client, created.Headers.Location!, method, command, first.Headers.ETag!.ToString());
        await AssertProblemAsync(stale, 412, "precondition_failed");
        using var current = await client.GetAsync(created.Headers.Location, TestCancellationToken);
        Assert.Equal(repeated.Headers.ETag, current.Headers.ETag);
        Assert.Equal(factory.Instant, (await BodyAsync(current)).GetProperty("updatedAtUtc").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("accounts", "PUT")]
    [InlineData("accounts", "PATCH")]
    [InlineData("categories", "PUT")]
    [InlineData("categories", "PATCH")]
    public async Task Foreign_and_missing_resources_have_the_same_404_and_preserve_owner_data(string resource, string method)
    {
        using var factory = new SqlApiFactory(fixture);
        using var owner = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(owner);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        using var created = await CreateResourceAsync(owner, resource);

        using var foreign = await ChangeAsync(stranger, created.Headers.Location!, method, Command(resource, method), created.Headers.ETag!.ToString());
        using var missing = await ChangeAsync(stranger, new Uri($"/api/v1/{resource}/{Guid.NewGuid()}", UriKind.Relative), method,
            Command(resource, method), created.Headers.ETag.ToString());

        await AssertProblemAsync(foreign, 404, "not_found");
        await AssertProblemAsync(missing, 404, "not_found");
        var foreignBody = await BodyAsync(foreign);
        var missingBody = await BodyAsync(missing);
        foreach (var field in new[] { "status", "title", "code", "type" })
        {
            Assert.Equal(missingBody.GetProperty(field).GetRawText(), foreignBody.GetProperty(field).GetRawText());
        }

        using var unchanged = await owner.GetAsync(created.Headers.Location, TestCancellationToken);
        Assert.Equal(created.Headers.ETag, unchanged.Headers.ETag);
    }

    [Theory]
    [InlineData("accounts", "PUT")]
    [InlineData("accounts", "PATCH")]
    [InlineData("categories", "PUT")]
    [InlineData("categories", "PATCH")]
    public async Task Invalid_bodies_unknown_properties_and_immutable_fields_are_400_without_writes(string resource, string method)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var created = await CreateResourceAsync(client, resource);
        var version = created.Headers.ETag!.ToString();
        var invalidBodies = new List<string> { "null", "{}", "{", "{\"name\":null}", "{\"isActive\":null}" };
        if (method == "PATCH")
        {
            invalidBodies.AddRange(["{\"isActive\":0}", "{\"isActive\":\"false\"}"]);
        }
        else
        {
            invalidBodies.Add(JsonSerializer.Serialize(Command(resource, method, " ")));
            invalidBodies.Add(JsonSerializer.Serialize(Command(resource, method, new string('x', 101))));
            invalidBodies.Add("{\"name\":true,\"type\":\"cash\"}");
            if (resource == "accounts")
            {
                invalidBodies.AddRange(["{\"name\":\"Missing type\"}", "{\"name\":\"Name\",\"type\":\"Cash\"}"]);
            }
        }

        foreach (var body in invalidBodies)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), created.Headers.Location)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("If-Match", version);
            using var response = await client.SendAsync(request, TestCancellationToken);
            await AssertProblemAsync(response, 400, "validation_error");
        }

        var forbidden = new List<string> { "id", "userId", "createdAtUtc", "updatedAtUtc", "rowVersion", "currency", "openingBalance", "openingDate", "kind", "normalizedName" };
        forbidden.AddRange(method == "PATCH" ? ["name", "type"] : ["isActive"]);
        foreach (var field in forbidden)
        {
            var body = JsonSerializer.SerializeToNode(Command(resource, method))!.AsObject();
            object value = field switch
            {
                "id" or "userId" => Guid.NewGuid().ToString("D"),
                "createdAtUtc" or "updatedAtUtc" => DateTimeOffset.UtcNow.ToString("O"),
                "rowVersion" => "AAAAAAAAAAE=",
                "currency" => "BRL",
                "openingBalance" => 1.25m,
                "openingDate" => "2000-01-01",
                "kind" => "income",
                "isActive" => false,
                "type" => "cash",
                _ => "Changed"
            };
            body[field] = JsonSerializer.SerializeToNode(value);
            using var response = await ChangeAsync(client, created.Headers.Location!, method, body, version);

            await AssertProblemAsync(response, 400, "validation_error");
        }

        using var unchanged = await client.GetAsync(created.Headers.Location, TestCancellationToken);
        Assert.Equal(created.Headers.ETag, unchanged.Headers.ETag);
        Assert.True((await BodyAsync(unchanged)).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Category_rename_conflict_is_atomic_and_inactive_names_remain_reserved()
    {
        using var factory = new FinancialChangeApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var reserved = await CreateResourceAsync(client, "categories", "Café");
        using var changing = await CreateResourceAsync(client, "categories", "Other");
        using var inactive = await ChangeAsync(client, reserved.Headers.Location!, "PATCH", new { isActive = false }, reserved.Headers.ETag!.ToString());
        await AssertSuccessAsync(inactive);

        using var conflict = await ChangeAsync(client, changing.Headers.Location!, "PUT", new { name = " Cafe\u0301 " }, changing.Headers.ETag!.ToString());

        await AssertProblemAsync(conflict, 409, "conflict");
        using var afterFailure = await client.GetAsync(changing.Headers.Location, TestCancellationToken);
        Assert.Equal(changing.Headers.ETag, afterFailure.Headers.ETag);
        var preserved = await BodyAsync(afterFailure);
        Assert.Equal("Other", preserved.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, preserved.GetProperty("updatedAtUtc").ValueKind);
        using var accentDistinct = await ChangeAsync(client, changing.Headers.Location!, "PUT", new { name = "Cafe" }, changing.Headers.ETag.ToString());
        await AssertSuccessAsync(accentDistinct);
        using var staleDuplicate = await ChangeAsync(client, changing.Headers.Location!, "PUT", new { name = "CAFÉ" }, changing.Headers.ETag.ToString());
        await AssertProblemAsync(staleDuplicate, 412, "precondition_failed");
        using var reactivated = await ChangeAsync(client, reserved.Headers.Location!, "PATCH", new { isActive = true }, inactive.Headers.ETag!.ToString());
        await AssertSuccessAsync(reactivated);
        using var duplicateCreation = await CreateResourceAsync(client, "categories", "CAFÉ");
        Assert.Equal(HttpStatusCode.Conflict, duplicateCreation.StatusCode);
    }

    [Fact]
    public async Task Account_creation_replay_is_frozen_after_real_api_edits_and_activity_changes()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        const string key = "immutable-creation-snapshot";
        object body = new { name = "Original", type = "checking", currency = "BRL", openingBalance = -50.25m, openingDate = "2000-01-01" };
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts") { Content = JsonContent.Create(body) };
        create.Headers.Add("Idempotency-Key", key);
        using var original = await client.SendAsync(create, TestCancellationToken);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var snapshot = await original.Content.ReadAsStringAsync(TestCancellationToken);

        using var edited = await ChangeAsync(client, original.Headers.Location!, "PUT", new { name = "Changed", type = "cash" }, original.Headers.ETag!.ToString());
        await AssertSuccessAsync(edited);
        using var inactive = await ChangeAsync(client, original.Headers.Location!, "PATCH", new { isActive = false }, edited.Headers.ETag!.ToString());
        await AssertSuccessAsync(inactive);

        using var repeat = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts") { Content = JsonContent.Create(body) };
        repeat.Headers.Add("Idempotency-Key", key);
        using var replay = await client.SendAsync(repeat, TestCancellationToken);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(snapshot, await replay.Content.ReadAsStringAsync(TestCancellationToken));
        Assert.Equal(original.Headers.Location, replay.Headers.Location);
        Assert.Equal(original.Headers.ETag, replay.Headers.ETag);
        using var current = await client.GetAsync(original.Headers.Location, TestCancellationToken);
        var state = await BodyAsync(current);
        Assert.Equal("Changed", state.GetProperty("name").GetString());
        Assert.False(state.GetProperty("isActive").GetBoolean());
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("categories")]
    public async Task Inactive_resources_can_be_edited_and_reactivated_without_losing_data(string resource)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var created = await CreateResourceAsync(client, resource);
        using var inactive = await ChangeAsync(client, created.Headers.Location!, "PATCH", new { isActive = false }, created.Headers.ETag!.ToString());
        await AssertSuccessAsync(inactive);

        using var edited = await ChangeAsync(client, created.Headers.Location!, "PUT", Command(resource, "PUT", "Inactive edit"), inactive.Headers.ETag!.ToString());

        await AssertSuccessAsync(edited);
        using var current = await client.GetAsync(created.Headers.Location, TestCancellationToken);
        var body = await BodyAsync(current);
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.Equal("Inactive edit", body.GetProperty("name").GetString());
        using var active = await ChangeAsync(client, created.Headers.Location!, "PATCH", new { isActive = true }, edited.Headers.ETag!.ToString());
        await AssertSuccessAsync(active);
    }

    [Fact]
    public async Task Category_rename_keeps_uniqueness_scoped_to_owner_and_kind()
    {
        using var factory = new SqlApiFactory(fixture);
        using var owner = factory.CreateClient();
        using var otherOwner = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(owner);
        await CategoryTestRequests.AuthenticateAsync(otherOwner);
        using var changing = await CreateResourceAsync(owner, "categories");
        using var otherKind = await CategoryTestRequests.CreateAsync(owner, "Shared", "income");
        using var foreign = await CreateResourceAsync(otherOwner, "categories", "Shared");
        Assert.Equal(HttpStatusCode.Created, otherKind.StatusCode);
        Assert.Equal(HttpStatusCode.Created, foreign.StatusCode);

        using var renamed = await ChangeAsync(owner, changing.Headers.Location!, "PUT", new { name = " shared " }, changing.Headers.ETag!.ToString());

        await AssertSuccessAsync(renamed);
    }

    internal static async Task AssertSuccessAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(TestCancellationToken));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag.IsWeak);
    }

    internal static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await BodyAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
    }
}
