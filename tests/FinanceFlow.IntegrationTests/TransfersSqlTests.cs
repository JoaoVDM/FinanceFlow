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
using static FinanceFlow.IntegrationTests.TransferSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransfersSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Creation_and_get_preserve_metadata_and_two_opposite_balance_effects_without_transactions()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client, 2000m);
        var destination = await AccountAsync(client, 1000m);
        var body = new TransferBody(source.Id, destination.Id, 500m, new(2000, 1, 3), "  Monthly reserve  ");

        using var created = await PostTransferAsync(client, body);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl?.NoStore);
        Assert.False(created.Headers.ETag!.IsWeak);
        var value = await ReadAsync(created);
        Assert.Equal("/api/v1/transfers/" + value.GetProperty("id").GetGuid().ToString("D"), created.Headers.Location!.ToString());
        Assert.Equal(8, Convert.FromBase64String(value.GetProperty("version").GetString()!).Length);
        Assert.Equal(body.Description, value.GetProperty("description").GetString());
        Assert.Equal(TimeSpan.Zero, value.GetProperty("createdAtUtc").GetDateTimeOffset().Offset);
        Assert.Equal(JsonValueKind.Null, value.GetProperty("updatedAtUtc").ValueKind);
        foreach (var field in new[] { "userId", "kind", "categoryId", "isCanceled", "rowVersion" })
        {
            Assert.False(value.TryGetProperty(field, out _));
        }
        using var get = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(created.Headers.ETag, get.Headers.ETag);
        Assert.True(get.Headers.CacheControl?.NoStore);
        Assert.Equal(value.GetProperty("id").GetGuid(), (await ReadAsync(get)).GetProperty("id").GetGuid());
        Assert.Equal(2000m, await BalanceAsync(client, source.Id, "2000-01-02"));
        Assert.Equal(1000m, await BalanceAsync(client, destination.Id, "2000-01-02"));
        Assert.Equal(1500m, await BalanceAsync(client, source.Id, "2000-01-03"));
        Assert.Equal(1500m, await BalanceAsync(client, destination.Id, "2000-01-03"));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Equal(1, await db.Transfers.CountAsync(transfer => transfer.UserId == owner, Cancellation));
        Assert.Equal(0, await db.FinancialTransactions.CountAsync(transaction => transaction.UserId == owner, Cancellation));
    }

    [Fact]
    public async Task Retroactive_transfers_reconcile_with_existing_transactions_overdraft_inactivity_and_logical_deletion()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client, 2000m);
        var destination = await AccountAsync(client, 1000m);
        using var income = await IncomeExpenseAsync(client, source.Id, "income", 100m);
        using var expense = await IncomeExpenseAsync(client, destination.Id, "expense", 50m);
        using var transfer = await PostTransferAsync(client, new(source.Id, destination.Id, 500m, new(2000, 1, 3), null));
        using var retroactive = await PostTransferAsync(client, new(destination.Id, source.Id, 200m, new(2000, 1, 2), ""));
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retroactive.StatusCode);
        Assert.Equal(2100m, await BalanceAsync(client, source.Id, "2000-01-01"));
        Assert.Equal(950m, await BalanceAsync(client, destination.Id, "2000-01-01"));
        Assert.Equal(2300m, await BalanceAsync(client, source.Id, "2000-01-02"));
        Assert.Equal(750m, await BalanceAsync(client, destination.Id, "2000-01-02"));
        Assert.Equal(1800m, await BalanceAsync(client, source.Id, "2000-01-03"));
        Assert.Equal(1250m, await BalanceAsync(client, destination.Id, "2000-01-03"));
        using var overdraft = await PostTransferAsync(client, new(source.Id, destination.Id, 5000m, new(2000, 1, 4), null));
        Assert.Equal(HttpStatusCode.Created, overdraft.StatusCode);
        await TransactionMutationRequests.DeactivateAsync(client, source.Location, source.Version);
        using var delete = await TransactionMutationRequests.MutateAsync(client, income.Headers.Location!, "DELETE", null, income.Headers.ETag!.ToString());
        await TransactionMutationRequests.SuccessAsync(delete);
        var sourceBalance = await BalanceAsync(client, source.Id);
        var destinationBalance = await BalanceAsync(client, destination.Id);
        Assert.Equal(-3300m, sourceBalance);
        Assert.Equal(6250m, destinationBalance);
        Assert.Equal(2950m, sourceBalance + destinationBalance); // Controlled data, no concurrent writes.
        Assert.Equal(0m, await BalanceAsync(client, source.Id, "1999-12-31"));
        using var list = await client.GetAsync("/api/v1/transactions", Cancellation);
        Assert.Equal(1, (await ReadAsync(list)).GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData(true, "missing", 404)]
    [InlineData(false, "missing", 404)]
    [InlineData(true, "foreign", 404)]
    [InlineData(false, "foreign", 404)]
    [InlineData(true, "inactive", 409)]
    [InlineData(false, "inactive", 409)]
    [InlineData(true, "opening", 400)]
    [InlineData(false, "opening", 400)]
    public async Task Both_references_require_ownership_activity_and_compatible_opening(bool sourceInvalid, string invalid, int status)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        using var stranger = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var valid = await AccountAsync(client);
        var other = invalid == "foreign" ? await AccountAsync(stranger) : await AccountAsync(client, openingDate: invalid == "opening" ? "2000-01-02" : "2000-01-01");
        if (invalid == "inactive")
        {
            await TransactionMutationRequests.DeactivateAsync(client, other.Location, other.Version);
        }
        var invalidId = invalid == "missing" ? Guid.NewGuid() : other.Id;
        using var response = await PostTransferAsync(client, new(sourceInvalid ? invalidId : valid.Id, sourceInvalid ? valid.Id : invalidId, 10m, new(2000, 1, 1), null));
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>().Transfers.CountAsync(value => value.UserId == owner, Cancellation));
    }

    [Fact]
    public async Task Json_money_identifiers_dates_fields_and_keys_are_rejected_before_any_creation()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        var body = new TransferBody(source.Id, destination.Id, 10m, new(2000, 1, 1), null);
        var valid = JsonSerializer.SerializeToNode(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        var invalidBodies = new List<string> { "null", "{}", "{" };
        foreach (var field in new[] { "sourceAccountId", "destinationAccountId", "amount", "occurredOn" })
        {
            var missing = valid.DeepClone().AsObject();
            missing.Remove(field);
            invalidBodies.Add(missing.ToJsonString());
            var nulled = valid.DeepClone().AsObject();
            nulled[field] = null;
            invalidBodies.Add(nulled.ToJsonString());
        }
        foreach (var field in new[] { "id", "userId", "isCanceled", "canceledAtUtc", "rowVersion", "createdAtUtc", "updatedAtUtc", "kind", "categoryId" })
        {
            var unknown = valid.DeepClone().AsObject();
            unknown[field] = "server controlled";
            invalidBodies.Add(unknown.ToJsonString());
        }
        foreach (var amount in new[] { "0", "-1", "10.001", "1000000000000", "10.00000000000000000000000000000000001", "\"10\"" })
        {
            var invalidAmount = valid.DeepClone().AsObject();
            invalidAmount["amount"] = JsonNode.Parse(amount);
            invalidBodies.Add(invalidAmount.ToJsonString());
        }
        foreach (var invalid in new[] { body with { SourceAccountId = Guid.Empty }, body with { DestinationAccountId = Guid.Empty },
            body with { DestinationAccountId = source.Id }, body with { OccurredOn = new(1999, 12, 31) },
            body with { OccurredOn = new(9999, 12, 31) }, body with { Description = new string('x', 501) } })
        {
            invalidBodies.Add(JsonSerializer.Serialize(invalid, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        foreach (var json in invalidBodies)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(request, Cancellation);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var key in new[] { null, "", "contains space", "á", new string('x', 129) })
        {
            using var response = await PostTransferAsync(client, body, key, omitKey: key is null);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>().Transfers.CountAsync(value => value.UserId == owner, Cancellation));
        foreach (var amount in new[] { 0.01m, 10.000m, 999999999999.99m })
        {
            using var accepted = await PostTransferAsync(client, body with { Amount = amount, Description = new string('x', 500) });
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }
    }

    [Fact]
    public async Task Idempotency_is_exact_scoped_and_replays_after_both_accounts_become_inactive()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var key = "Namespace-" + Guid.NewGuid().ToString("N");
        var source = await AccountAsync(client, key: key);
        var destination = await AccountAsync(client);
        using var transaction = await IncomeExpenseAsync(client, source.Id, "income", 10m, key);
        var body = new TransferBody(source.Id, destination.Id, 10m, new(2000, 1, 1), null);
        using var original = await PostTransferAsync(client, body, key);
        using var conflict = await PostTransferAsync(client, body with { Amount = 11m }, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers")
        {
            Content = new StringContent($$"""{"occurredOn":"2000-01-01","description":null,"amount":1e1,"destinationAccountId":"{{destination.Id.ToString("D").ToUpperInvariant()}}","sourceAccountId":"{{source.Id.ToString("D").ToUpperInvariant()}}"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        using var equivalent = await client.SendAsync(request, Cancellation);
        await SameTransferSnapshotAsync(original, equivalent);
        using var distinctCase = await PostTransferAsync(client, body, key.ToLowerInvariant());
        Assert.Equal(HttpStatusCode.Created, distinctCase.StatusCode);
        Assert.NotEqual(original.Headers.Location, distinctCase.Headers.Location);
        var foreignSource = await AccountAsync(stranger);
        var foreignDestination = await AccountAsync(stranger);
        using var foreign = await PostTransferAsync(stranger, body with { SourceAccountId = foreignSource.Id, DestinationAccountId = foreignDestination.Id }, key);
        Assert.Equal(HttpStatusCode.Created, foreign.StatusCode);
        using var denied = await stranger.GetAsync(original.Headers.Location, Cancellation);
        using var missing = await stranger.GetAsync("/api/v1/transfers/" + Guid.NewGuid(), Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal((await ReadAsync(denied)).GetProperty("code").GetString(), (await ReadAsync(missing)).GetProperty("code").GetString());
        await TransactionMutationRequests.DeactivateAsync(client, source.Location, source.Version);
        await TransactionMutationRequests.DeactivateAsync(client, destination.Location, destination.Version);
        using var replay = await PostTransferAsync(client, body, key);
        await SameTransferSnapshotAsync(original, replay);
        Assert.Equal(-10m, await BalanceAsync(client, source.Id));
        Assert.Equal(20m, await BalanceAsync(client, destination.Id));
    }
}

internal sealed record TransferAccount(Guid Id, Uri Location, string Version);
internal sealed record TransferBody(Guid SourceAccountId, Guid DestinationAccountId, decimal Amount, DateOnly OccurredOn, string? Description);

internal static class TransferSqlRequests
{
    internal static async Task<TransferAccount> AccountAsync(HttpClient client, decimal opening = 0m, string openingDate = "2000-01-01", string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts")
        {
            Content = JsonContent.Create(new { name = "Transfer account", type = "cash", currency = "BRL", openingBalance = opening, openingDate })
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new((await ReadAsync(response)).GetProperty("id").GetGuid(), response.Headers.Location!, response.Headers.ETag!.ToString());
    }

    internal static async Task<HttpResponseMessage> PostTransferAsync(HttpClient client, TransferBody body, string? key = null, bool omitKey = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers") { Content = JsonContent.Create(body) };
        if (!omitKey)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        }
        return await client.SendAsync(request, Cancellation);
    }

    internal static async Task<HttpResponseMessage> IncomeExpenseAsync(HttpClient client, Guid accountId, string kind, decimal amount, string? key = null)
    {
        using var category = await CategoryTestRequests.CreateAsync(client, Guid.NewGuid().ToString("N"), kind);
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions")
        {
            Content = JsonContent.Create(new { accountId, categoryId = (await ReadAsync(category)).GetProperty("id").GetGuid(), kind, amount, occurredOn = "2000-01-01" })
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response;
    }

    internal static async Task SameTransferSnapshotAsync(HttpResponseMessage original, HttpResponseMessage replay)
    {
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await original.Content.ReadAsStringAsync(Cancellation), await replay.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(original.Headers.Location, replay.Headers.Location);
        Assert.Equal(original.Headers.ETag, replay.Headers.ETag);
        Assert.Equal(original.Content.Headers.ContentType, replay.Content.Headers.ContentType);
    }
}
