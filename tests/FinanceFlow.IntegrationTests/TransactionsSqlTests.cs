using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransactionsSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("income", "0")]
    [InlineData("expense", "0")]
    [InlineData("income", "-50.25")]
    [InlineData("expense", "100.25")]
    public async Task Creation_retrieval_and_current_balance_use_the_correct_sign(string kind, string openingText)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var opening = decimal.Parse(openingText, System.Globalization.CultureInfo.InvariantCulture);
        var references = await ReferencesAsync(client, opening, kind);

        using var created = await CreateAsync(client, references, kind: kind, amount: 12.34m);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(created.Headers.CacheControl?.NoStore);
        Assert.False(created.Headers.ETag!.IsWeak);
        var body = await ReadAsync(created);
        Assert.Equal(12.34m, body.GetProperty("amount").GetDecimal());
        Assert.False(body.TryGetProperty("userId", out _));
        Assert.False(body.TryGetProperty("isDeleted", out _));
        using var retrieved = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, retrieved.StatusCode);
        Assert.Equal(created.Headers.ETag, retrieved.Headers.ETag);
        Assert.Equal(body.GetProperty("id").GetGuid(), (await ReadAsync(retrieved)).GetProperty("id").GetGuid());
        Assert.Equal(opening + (kind == "income" ? 12.34m : -12.34m), await BalanceAsync(client, references.AccountId));
    }

    [Fact]
    public async Task Historical_cutoffs_are_inclusive_retroactive_and_keep_inactive_accounts()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client, -100.25m);
        using var later = await CreateAsync(client, references, amount: 30.10m, occurredOn: "2000-01-03");
        Assert.Equal(HttpStatusCode.Created, later.StatusCode);
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId, "1999-12-31"));
        Assert.Equal(-100.25m, await BalanceAsync(client, references.AccountId, "2000-01-01"));
        Assert.Equal(-70.15m, await BalanceAsync(client, references.AccountId, "2000-01-03"));

        using var retroactive = await CreateAsync(client, references, amount: 10.05m, occurredOn: "2000-01-02");
        Assert.Equal(HttpStatusCode.Created, retroactive.StatusCode);
        Assert.Equal(-90.20m, await BalanceAsync(client, references.AccountId, "2000-01-02"));
        Assert.Equal(-60.10m, await BalanceAsync(client, references.AccountId, "2000-01-03"));
        using var expenseCategory = await CategoryTestRequests.CreateAsync(client, "Expense-" + Guid.NewGuid().ToString("N"), "expense");
        Assert.Equal(HttpStatusCode.Created, expenseCategory.StatusCode);
        var expenseReferences = references with { CategoryId = (await ReadAsync(expenseCategory)).GetProperty("id").GetGuid() };
        using var expense = await CreateAsync(client, expenseReferences, amount: 5.05m, kind: "expense", occurredOn: "2000-01-03");
        Assert.Equal(HttpStatusCode.Created, expense.StatusCode);
        using var inactive = await FinancialChangeRequests.ChangeAsync(client, references.AccountLocation, "PATCH", new { isActive = false }, references.AccountVersion);
        Assert.Equal(HttpStatusCode.NoContent, inactive.StatusCode);
        Assert.Equal(-65.15m, await BalanceAsync(client, references.AccountId));
    }

    [Fact]
    public async Task Idempotency_replays_the_frozen_response_even_after_references_become_inactive()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        var key = Guid.NewGuid().ToString("N");
        using var original = await CreateAsync(client, references, key);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        using var inactive = await FinancialChangeRequests.ChangeAsync(client, references.CategoryLocation, "PATCH", new { isActive = false }, references.CategoryVersion);
        Assert.Equal(HttpStatusCode.NoContent, inactive.StatusCode);

        using var replay = await CreateAsync(client, references, key);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await original.Content.ReadAsStringAsync(Cancellation), await replay.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(original.Headers.ETag, replay.Headers.ETag);
        Assert.Equal(original.Headers.Location, replay.Headers.Location);
        using var changed = await CreateAsync(client, references, key, amount: 11m);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("category")]
    public async Task Inactive_references_reject_new_keys_without_affecting_balance(string resource)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        var location = resource == "account" ? references.AccountLocation : references.CategoryLocation;
        var version = resource == "account" ? references.AccountVersion : references.CategoryVersion;
        using var inactive = await FinancialChangeRequests.ChangeAsync(client, location, "PATCH", new { isActive = false }, version);
        Assert.Equal(HttpStatusCode.NoContent, inactive.StatusCode);
        using var rejected = await CreateAsync(client, references);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId));
    }

    [Fact]
    public async Task Foreign_references_resources_filters_and_balances_are_indistinguishable_from_missing()
    {
        using var factory = new SqlApiFactory(fixture);
        using var owner = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(owner);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var references = await ReferencesAsync(owner);
        var ownReferences = await ReferencesAsync(stranger);
        using var created = await CreateAsync(owner, references);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var foreignCreation = await CreateAsync(stranger, references);
        using var foreignAccount = await CreateAsync(stranger, ownReferences with { AccountId = references.AccountId });
        using var foreignCategory = await CreateAsync(stranger, ownReferences with { CategoryId = references.CategoryId });
        Assert.Equal(HttpStatusCode.NotFound, foreignCreation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignAccount.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignCategory.StatusCode);
        foreach (var path in new[]
        {
            created.Headers.Location!.ToString(), "/api/v1/transactions/" + Guid.NewGuid(),
            $"/api/v1/accounts/{references.AccountId}/balance", $"/api/v1/accounts/{Guid.NewGuid()}/balance",
            $"/api/v1/transactions?accountId={references.AccountId}", $"/api/v1/transactions?categoryId={references.CategoryId}",
            $"/api/v1/transactions?accountId={Guid.NewGuid()}"
        })
        {
            using var response = await stranger.GetAsync(path, Cancellation);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", (await ReadAsync(response)).GetProperty("code").GetString());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        using var list = await stranger.GetAsync("/api/v1/transactions", Cancellation);
        Assert.Equal(0, (await ReadAsync(list)).GetProperty("totalCount").GetInt32());
        // Namespace also includes the authenticated owner, not just the key text.
        using var ownCreation = await CreateAsync(stranger, ownReferences, "same-key");
        using var ownerCreation = await CreateAsync(owner, references, "same-key");
        Assert.Equal(HttpStatusCode.Created, ownCreation.StatusCode);
        Assert.Equal(HttpStatusCode.Created, ownerCreation.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10.001")]
    [InlineData("1000000000000")]
    [InlineData("10.0000000000000000000000000000000001")]
    public async Task Invalid_money_never_reaches_persistence(string amount)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions")
        {
            Content = new StringContent($$"""{"accountId":"{{references.AccountId}}","categoryId":"{{references.CategoryId}}","kind":"income","amount":{{amount}},"occurredOn":"2000-01-01"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId));
    }

    [Fact]
    public async Task Boundaries_dates_kind_keys_and_unknown_server_fields_follow_the_contract()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var maximum = await CreateAsync(client, references, amount: 999999999999.99m);
        Assert.Equal(HttpStatusCode.Created, maximum.StatusCode);
        using var incompatible = await CreateAsync(client, references, kind: "expense");
        using var early = await CreateAsync(client, references, occurredOn: "1999-12-31");
        using var future = await CreateAsync(client, references, occurredOn: "9999-12-31");
        Assert.Equal(HttpStatusCode.BadRequest, incompatible.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        foreach (var body in new object[]
        {
            new { accountId = references.AccountId, categoryId = references.CategoryId, kind = "income", amount = 1m, occurredOn = "2000-01-01", userId = Guid.NewGuid() },
            new { accountId = references.AccountId, categoryId = references.CategoryId, kind = "income", amount = 1m, occurredOn = "2000-01-01", isDeleted = false }
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var invalid = await client.SendAsync(request, Cancellation);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var noKey = await client.PostAsJsonAsync("/api/v1/transactions", Command(references), Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Equal(999999999999.99m, await BalanceAsync(client, references.AccountId));
        using var secondMaximum = await CreateAsync(client, references, amount: 999999999999.99m);
        using var cent = await CreateAsync(client, references, amount: 0.01m);
        Assert.Equal(HttpStatusCode.Created, secondMaximum.StatusCode);
        Assert.Equal(HttpStatusCode.Created, cent.StatusCode);
        Assert.Equal(1999999999999.99m, await BalanceAsync(client, references.AccountId));
    }

    [Fact]
    public async Task Pagination_filters_and_ordering_are_executed_in_sql_and_validate_all_inputs()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        foreach (var amount in new[] { 30m, 10m, 20m })
        {
            using var created = await CreateAsync(client, references, amount: amount);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        using var first = await client.GetAsync($"/api/v1/transactions?accountId={references.AccountId}&categoryId={references.CategoryId}&kind=income&from=2000-01-01&to=2000-01-01&minAmount=10.000&maxAmount=30&sort=amount&direction=asc&pageSize=2", Cancellation);
        var page = await ReadAsync(first);
        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.GetProperty("totalPages").GetInt32());
        Assert.Equal(new[] { 10m, 20m }, page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("amount").GetDecimal()));
        using var second = await client.GetAsync("/api/v1/transactions?sort=amount&direction=asc&pageSize=2&page=2", Cancellation);
        Assert.Equal(30m, (await ReadAsync(second)).GetProperty("items")[0].GetProperty("amount").GetDecimal());
        using var beyond = await client.GetAsync("/api/v1/transactions?page=99", Cancellation);
        Assert.Empty((await ReadAsync(beyond)).GetProperty("items").EnumerateArray());
        using var tiedFirst = await client.GetAsync("/api/v1/transactions?sort=occurredOn&direction=asc&pageSize=2&minAmount=0", Cancellation);
        using var tiedRepeat = await client.GetAsync("/api/v1/transactions?sort=occurredOn&direction=asc&pageSize=2", Cancellation);
        using var tiedSecond = await client.GetAsync("/api/v1/transactions?sort=occurredOn&direction=asc&pageSize=2&page=2", Cancellation);
        var firstIds = (await ReadAsync(tiedFirst)).GetProperty("items").EnumerateArray().Select(value => value.GetProperty("id").GetGuid()).ToArray();
        var repeatedIds = (await ReadAsync(tiedRepeat)).GetProperty("items").EnumerateArray().Select(value => value.GetProperty("id").GetGuid()).ToArray();
        var remainingIds = (await ReadAsync(tiedSecond)).GetProperty("items").EnumerateArray().Select(value => value.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(firstIds, repeatedIds);
        Assert.Equal(3, firstIds.Concat(remainingIds).Distinct().Count());
        foreach (var filter in new[] { "page=0", "pageSize=101", "page=2147483647", "sort=unknown", "direction=up", "kind=Income", "from=2000-02-01&to=2000-01-01", "to=9999-12-31", "from=01-01-2000", "minAmount=10.001", "minAmount=10.0000000000000000000000000000000001", "minAmount=2&maxAmount=1", "unknown=1", "page=1&page=2", "accountId=bad" })
        {
            using var invalid = await client.GetAsync("/api/v1/transactions?" + filter, Cancellation);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var invalidCutoff = await client.GetAsync($"/api/v1/accounts/{references.AccountId}/balance?asOf=9999-12-31", Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCutoff.StatusCode);
    }
}

internal sealed record TransactionReferences(Guid AccountId, Guid CategoryId, Uri AccountLocation, Uri CategoryLocation, string AccountVersion, string CategoryVersion);

internal static class TransactionSqlRequests
{
    internal static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    internal static async Task<TransactionReferences> ReferencesAsync(HttpClient client, decimal opening = 0m, string kind = "income")
    {
        using var accountRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts")
        {
            Content = JsonContent.Create(new { name = "Transaction account", type = "cash", currency = "BRL", openingBalance = opening, openingDate = "2000-01-01" })
        };
        accountRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var account = await client.SendAsync(accountRequest, Cancellation);
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        using var category = await CategoryTestRequests.CreateAsync(client, "Category-" + Guid.NewGuid().ToString("N"), kind);
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        return new((await ReadAsync(account)).GetProperty("id").GetGuid(), (await ReadAsync(category)).GetProperty("id").GetGuid(),
            account.Headers.Location!, category.Headers.Location!, account.Headers.ETag!.ToString(), category.Headers.ETag!.ToString());
    }

    internal static object Command(TransactionReferences references, decimal amount = 10m, string kind = "income", string occurredOn = "2000-01-01") =>
        new { accountId = references.AccountId, categoryId = references.CategoryId, kind, amount, occurredOn, description = "Fictitious transaction" };

    internal static async Task<HttpResponseMessage> CreateAsync(HttpClient client, TransactionReferences references,
        string? key = null, decimal amount = 10m, string kind = "income", string occurredOn = "2000-01-01")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions") { Content = JsonContent.Create(Command(references, amount, kind, occurredOn)) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request, Cancellation);
    }

    internal static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        return json.RootElement.Clone();
    }

    internal static async Task<decimal> BalanceAsync(HttpClient client, Guid accountId, string? asOf = null)
    {
        using var response = await client.GetAsync($"/api/v1/accounts/{accountId}/balance" + (asOf is null ? "" : "?asOf=" + asOf), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await ReadAsync(response)).GetProperty("balance").GetDecimal();
    }
}
