using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransactionMutationRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransactionMutationSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("income", "100", "200", "200")]
    [InlineData("expense", "300", "150", "-150")]
    public async Task Put_updates_exact_value_timestamps_version_and_balance(string kind, string originalText, string amountText, string balanceText)
    {
        using var factory = new FinancialChangeApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client, kind: kind);
        using var created = await CreateAsync(client, references, amount: Money(originalText), kind: kind);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var original = await ReadAsync(created);
        var body = Payload(original) with { Amount = Money(amountText), Description = null };

        using var changed = await MutateAsync(client, created.Headers.Location!, "PUT", body, created.Headers.ETag!.ToString());

        await SuccessAsync(changed);
        Assert.NotEqual(created.Headers.ETag, changed.Headers.ETag);
        using var current = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(changed.Headers.ETag, current.Headers.ETag);
        var updated = await ReadAsync(current);
        Assert.Equal(body.Amount, updated.GetProperty("amount").GetDecimal());
        Assert.Equal(factory.Instant, updated.GetProperty("updatedAtUtc").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, updated.GetProperty("updatedAtUtc").GetDateTimeOffset().Offset);
        Assert.Equal(original.GetProperty("createdAtUtc").GetRawText(), updated.GetProperty("createdAtUtc").GetRawText());
        Assert.Null(updated.GetProperty("description").GetString());
        Assert.Equal(Money(balanceText), await BalanceAsync(client, references.AccountId));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(value => value.UserId == owner, Cancellation));
    }

    [Fact]
    public async Task Put_can_reverse_kind_move_accounts_and_recalculate_inclusive_historical_cutoffs()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await ReferencesAsync(client);
        var target = await ReferencesAsync(client, kind: "expense");
        using var created = await CreateAsync(client, source, amount: 100m, occurredOn: "2000-01-03");
        var original = await ReadAsync(created);
        Assert.Equal(100m, await BalanceAsync(client, source.AccountId, "2000-01-03"));
        using var changed = await MutateAsync(client, created.Headers.Location!, "PUT", Payload(original) with
        {
            AccountId = target.AccountId, CategoryId = target.CategoryId, Kind = "expense", Amount = 150m, OccurredOn = new(2000, 1, 2)
        }, created.Headers.ETag!.ToString());
        await SuccessAsync(changed);
        Assert.Equal(0m, await BalanceAsync(client, source.AccountId));
        Assert.Equal(0m, await BalanceAsync(client, target.AccountId, "2000-01-01"));
        Assert.Equal(-150m, await BalanceAsync(client, target.AccountId, "2000-01-02"));
        Assert.Equal(-150m, await BalanceAsync(client, target.AccountId, "2000-01-03"));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("category")]
    [InlineData("both")]
    public async Task Description_and_no_op_updates_preserve_inactive_historical_links(string inactive)
    {
        using var factory = new FinancialChangeApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        var payload = Payload(await ReadAsync(created));
        if (inactive is "account" or "both") await DeactivateAsync(client, references.AccountLocation, references.AccountVersion);
        if (inactive is "category" or "both") await DeactivateAsync(client, references.CategoryLocation, references.CategoryVersion);

        using var corrected = await MutateAsync(client, created.Headers.Location!, "PUT", payload with { Description = "Correction" }, created.Headers.ETag!.ToString());
        await SuccessAsync(corrected);
        using var noOp = await MutateAsync(client, created.Headers.Location!, "PUT", payload with { Description = "Correction" }, corrected.Headers.ETag!.ToString());
        await SuccessAsync(noOp);
        Assert.NotEqual(corrected.Headers.ETag, noOp.Headers.ETag);
        using var staleNoOp = await MutateAsync(client, created.Headers.Location!, "PUT", payload with { Description = "Correction" }, corrected.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleNoOp.StatusCode);
        Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("date")]
    [InlineData("account")]
    [InlineData("category")]
    [InlineData("kind")]
    public async Task Inactive_source_account_blocks_non_descriptive_edits_without_partial_changes(string field)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        var other = await ReferencesAsync(client, kind: field == "kind" ? "expense" : "income");
        using var created = await CreateAsync(client, references);
        var original = await ReadAsync(created);
        var body = Payload(original);
        body = field switch
        {
            "amount" => body with { Amount = 20m },
            "date" => body with { OccurredOn = new(2000, 1, 2) },
            "account" => body with { AccountId = other.AccountId },
            "category" => body with { CategoryId = other.CategoryId },
            _ => body with { CategoryId = other.CategoryId, Kind = "expense" }
        };
        await DeactivateAsync(client, references.AccountLocation, references.AccountVersion);
        using var rejected = await MutateAsync(client, created.Headers.Location!, "PUT", body, created.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var current = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(created.Headers.ETag, current.Headers.ETag);
        Assert.Equal(Payload(original), Payload(await ReadAsync(current)));
        Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
        Assert.Equal(0m, await BalanceAsync(client, other.AccountId));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("category")]
    public async Task New_inactive_links_are_rejected_and_existing_inactive_category_does_not_block_value_correction(string resource)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        var target = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        var payload = Payload(await ReadAsync(created));
        await DeactivateAsync(client, resource == "account" ? target.AccountLocation : target.CategoryLocation,
            resource == "account" ? target.AccountVersion : target.CategoryVersion);
        var changed = resource == "account" ? payload with { AccountId = target.AccountId } : payload with { CategoryId = target.CategoryId };
        using var rejected = await MutateAsync(client, created.Headers.Location!, "PUT", changed, created.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        await DeactivateAsync(client, references.CategoryLocation, references.CategoryVersion);
        using var accepted = await MutateAsync(client, created.Headers.Location!, "PUT", payload with { Amount = 20m }, created.Headers.ETag.ToString());
        await SuccessAsync(accepted);
        Assert.Equal(20m, await BalanceAsync(client, references.AccountId));
    }

    [Fact]
    public async Task Delete_preserves_data_with_inactive_links_removes_all_balances_and_never_repeats()
    {
        using var factory = new FinancialChangeApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client, 25m);
        using var created = await CreateAsync(client, references, amount: 100m);
        var original = Payload(await ReadAsync(created));
        await DeactivateAsync(client, references.AccountLocation, references.AccountVersion);
        await DeactivateAsync(client, references.CategoryLocation, references.CategoryVersion);
        using var deleted = await MutateAsync(client, created.Headers.Location!, "DELETE", null, created.Headers.ETag!.ToString());
        await SuccessAsync(deleted);
        using var get = await client.GetAsync(created.Headers.Location, Cancellation);
        using var list = await client.GetAsync("/api/v1/transactions", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Empty((await ReadAsync(list)).GetProperty("items").EnumerateArray());
        Assert.Equal(25m, await BalanceAsync(client, references.AccountId));
        Assert.Equal(25m, await BalanceAsync(client, references.AccountId, "2000-01-01"));
        foreach (var version in new[] { created.Headers.ETag.ToString(), deleted.Headers.ETag!.ToString() })
        {
            using var repeat = await MutateAsync(client, created.Headers.Location!, "DELETE", null, version);
            using var editDeleted = await MutateAsync(client, created.Headers.Location!, "PUT", original, version);
            Assert.Equal(HttpStatusCode.NotFound, repeat.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, editDeleted.StatusCode);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        var row = await db.FinancialTransactions.AsNoTracking().SingleAsync(value => value.UserId == owner, Cancellation);
        Assert.True(row.IsDeleted);
        Assert.Equal(factory.Instant, row.DeletedAtUtc);
        Assert.Equal(factory.Instant, row.UpdatedAtUtc);
        Assert.Equal(original, new MutationBody(row.AccountId, row.CategoryId, row.Kind, row.Amount, row.OccurredOn, row.Description));
        Assert.NotEqual(created.Headers.ETag.Tag.Trim('"'), Convert.ToBase64String(row.RowVersion));
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Missing_empty_malformed_and_stale_preconditions_never_modify_the_resource(string method)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        var body = Payload(await ReadAsync(created));
        using var missing = await MutateAsync(client, created.Headers.Location!, method, body, null);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var emptyClient = new HttpClient(factory.Server.CreateHandler(context => context.Request.Headers["If-Match"] = string.Empty)) { BaseAddress = client.BaseAddress };
        emptyClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        using var empty = await MutateAsync(emptyClient, created.Headers.Location!, method, body, "");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        foreach (var version in new[] { "*", "W/\"AAAAAAAAAAE=\"", "\"AAAAAAAAAAE=\",\"AAAAAAAAAAI=\"", "AAAAAAAAAAE=", "\"invalid!!!==\"", "\"AAAAAAAAAA==\"", "\"AAAAAAAAAAJ=\"" })
        {
            using var rejected = await MutateAsync(client, created.Headers.Location!, method, body, version);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using var edited = await MutateAsync(client, created.Headers.Location!, "PUT", body with { Amount = 20m }, created.Headers.ETag!.ToString());
        await SuccessAsync(edited);
        using var stale = await MutateAsync(client, created.Headers.Location!, method, body, created.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("precondition_failed", (await ReadAsync(stale)).GetProperty("code").GetString());
        Assert.Equal(20m, await BalanceAsync(client, references.AccountId));
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Foreign_missing_and_deleted_resource_errors_do_not_disclose_ownership(string method)
    {
        using var factory = new SqlApiFactory(fixture);
        using var owner = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(owner);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var references = await ReferencesAsync(owner);
        using var created = await CreateAsync(owner, references);
        var body = Payload(await ReadAsync(created));
        using var foreign = await MutateAsync(stranger, created.Headers.Location!, method, body, created.Headers.ETag!.ToString());
        using var absent = await MutateAsync(stranger, new Uri("/api/v1/transactions/" + Guid.NewGuid(), UriKind.Relative), method, body, created.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var foreignBody = await ReadAsync(foreign);
        var absentBody = await ReadAsync(absent);
        foreach (var field in new[] { "status", "title", "code", "type" })
        {
            Assert.Equal(foreignBody.GetProperty(field).GetRawText(), absentBody.GetProperty(field).GetRawText());
        }
        Assert.Equal(10m, await BalanceAsync(owner, references.AccountId));
        using var deleted = await MutateAsync(owner, created.Headers.Location!, "DELETE", null, created.Headers.ETag.ToString());
        await SuccessAsync(deleted);
        using var deletedForeign = await MutateAsync(stranger, created.Headers.Location!, method, body, deleted.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, deletedForeign.StatusCode);
    }

    [Fact]
    public async Task Foreign_references_incompatible_kind_and_invalid_destination_date_are_rejected()
    {
        using var factory = new SqlApiFactory(fixture);
        using var owner = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(owner);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var references = await ReferencesAsync(owner);
        var foreign = await ReferencesAsync(stranger);
        using var created = await CreateAsync(owner, references);
        var body = Payload(await ReadAsync(created));
        foreach (var invalid in new[] { body with { AccountId = foreign.AccountId }, body with { CategoryId = foreign.CategoryId }, body with { AccountId = Guid.NewGuid() }, body with { CategoryId = Guid.NewGuid() } })
        {
            using var response = await MutateAsync(owner, created.Headers.Location!, "PUT", invalid, created.Headers.ETag!.ToString());
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var incompatible = await MutateAsync(owner, created.Headers.Location!, "PUT", body with { Kind = "expense" }, created.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, incompatible.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts")
        {
            Content = JsonContent.Create(new { name = "Later", type = "cash", currency = "BRL", openingBalance = 0m, openingDate = "2000-01-02" })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var later = await owner.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.Created, later.StatusCode);
        using var invalidDate = await MutateAsync(owner, created.Headers.Location!, "PUT", body with { AccountId = (await ReadAsync(later)).GetProperty("id").GetGuid() }, created.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, invalidDate.StatusCode);
        Assert.Equal(10m, await BalanceAsync(owner, references.AccountId));
    }

    [Fact]
    public async Task Invalid_put_bodies_fields_and_exact_money_never_change_values()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        var body = Payload(await ReadAsync(created));
        var valid = JsonSerializer.SerializeToNode(body, WebJson)!.AsObject();
        var invalidBodies = new List<string> { "null", "{}", "{", "{\"amount\":null}" };
        foreach (var field in new[] { "accountId", "categoryId", "kind", "amount", "occurredOn" })
        {
            var incomplete = valid.DeepClone().AsObject();
            incomplete.Remove(field);
            invalidBodies.Add(incomplete.ToJsonString());
        }
        foreach (var field in new[] { "id", "userId", "createdAtUtc", "updatedAtUtc", "isDeleted", "deletedAtUtc", "rowVersion", "unexpected" })
        {
            var unknown = valid.DeepClone().AsObject();
            object value = field switch
            {
                "id" or "userId" => Guid.NewGuid().ToString("D"),
                "createdAtUtc" or "updatedAtUtc" or "deletedAtUtc" => DateTimeOffset.UtcNow.ToString("O"),
                "isDeleted" => true,
                "rowVersion" => "AAAAAAAAAAE=",
                _ => "not accepted"
            };
            unknown[field] = JsonSerializer.SerializeToNode(value);
            invalidBodies.Add(unknown.ToJsonString());
        }
        foreach (var amount in new[] { "0", "-1", "10.001", "1000000000000", "10.000000000000000000000000000000000000001", "\"10.00\"" })
        {
            var invalidAmount = valid.DeepClone().AsObject();
            invalidAmount["amount"] = JsonNode.Parse(amount);
            invalidBodies.Add(invalidAmount.ToJsonString());
        }
        invalidBodies.Add(JsonSerializer.Serialize(body with { OccurredOn = new(9999, 12, 31) }, WebJson));
        invalidBodies.Add(JsonSerializer.Serialize(body with { Description = new string('x', 501) }, WebJson));
        foreach (var json in invalidBodies)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, created.Headers.Location) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.ToString());
            using var response = await client.SendAsync(request, Cancellation);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var amount in new[] { 0.01m, 10.000m, 999999999999.99m })
        {
            using var current = await client.GetAsync(created.Headers.Location, Cancellation);
            using var accepted = await MutateAsync(client, created.Headers.Location!, "PUT", body with { Amount = amount }, current.Headers.ETag!.ToString());
            await SuccessAsync(accepted);
        }
    }

    [Fact]
    public async Task Post_replay_remains_frozen_after_put_and_delete_and_delete_rejects_a_body()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        var key = Guid.NewGuid().ToString("N");
        using var created = await CreateAsync(client, references, key);
        var snapshot = await created.Content.ReadAsStringAsync(Cancellation);
        using var changed = await MutateAsync(client, created.Headers.Location!, "PUT", Payload(await ReadAsync(created)) with { Amount = 20m }, created.Headers.ETag!.ToString());
        await SuccessAsync(changed);
        using (var request = new HttpRequestMessage(HttpMethod.Delete, created.Headers.Location) { Content = JsonContent.Create(new { isDeleted = true }) })
        {
            request.Headers.TryAddWithoutValidation("If-Match", changed.Headers.ETag!.ToString());
            using var invalid = await client.SendAsync(request, Cancellation);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var replayAfterEdit = await CreateAsync(client, references, key);
        Assert.Equal(snapshot, await replayAfterEdit.Content.ReadAsStringAsync(Cancellation));
        using var deleted = await MutateAsync(client, created.Headers.Location!, "DELETE", null, changed.Headers.ETag!.ToString());
        await SuccessAsync(deleted);
        using var replay = await CreateAsync(client, references, key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(snapshot, await replay.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(created.Headers.Location, replay.Headers.Location);
        Assert.Equal(created.Headers.ETag, replay.Headers.ETag);
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId));
    }
}

internal sealed record MutationBody(Guid AccountId, Guid CategoryId, string Kind, decimal Amount, DateOnly OccurredOn, string? Description);

internal static class TransactionMutationRequests
{
    internal static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    internal static MutationBody Payload(JsonElement value) => new(value.GetProperty("accountId").GetGuid(), value.GetProperty("categoryId").GetGuid(),
        value.GetProperty("kind").GetString()!, value.GetProperty("amount").GetDecimal(),
        DateOnly.ParseExact(value.GetProperty("occurredOn").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture), value.GetProperty("description").GetString());
    internal static decimal Money(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    internal static async Task<HttpResponseMessage> MutateAsync(HttpClient client, Uri location, string method, object? body, string? version)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), location);
        if (method == "PUT") request.Content = JsonContent.Create(body);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return await client.SendAsync(request, Cancellation);
    }

    internal static async Task SuccessAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Cancellation));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag.IsWeak);
    }

    internal static async Task DeactivateAsync(HttpClient client, Uri location, string version)
    {
        using var response = await FinancialChangeRequests.ChangeAsync(client, location, "PATCH", new { isActive = false }, version);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
