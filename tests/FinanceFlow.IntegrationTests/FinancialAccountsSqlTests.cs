using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed partial class FinancialAccountsSqlTests(IdentitySqlFixture fixture)
{
    private static CancellationToken TestCancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Created_account_has_safe_contract_and_real_rowversion()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);

        using var created = await CreateAccountAsync(client, "same-name", "one");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = await ReadAccountAsync(created);
        Assert.Equal("same-name", account.Name);
        Assert.Equal("BRL", account.Currency);
        Assert.True(account.IsActive);
        Assert.Equal(TimeSpan.Zero, account.CreatedAtUtc.Offset);
        Assert.Equal(8, Convert.FromBase64String(account.Version).Length);
        Assert.Equal('"' + account.Version + '"', created.Headers.ETag?.ToString());
        Assert.Equal("/api/v1/accounts/" + account.Id, created.Headers.Location?.OriginalString);
        var body = await created.Content.ReadAsStringAsync(TestCancellationToken);
        Assert.DoesNotContain("userId", body);
        Assert.DoesNotContain("rowVersion", body);
        Assert.DoesNotContain("currentBalance", body);

        using var get = await client.GetAsync(created.Headers.Location, TestCancellationToken);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(created.Headers.ETag, get.Headers.ETag);
        Assert.Equal(body, await get.Content.ReadAsStringAsync(TestCancellationToken));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        var persisted = await db.FinancialAccounts.AsNoTracking().SingleAsync(x => x.Id == account.Id, TestCancellationToken);
        Assert.Equal(owner, persisted.UserId);

        using var duplicateName = await CreateAccountAsync(client, "same-name", "two");
        Assert.Equal(HttpStatusCode.Created, duplicateName.StatusCode);
    }

    [Fact]
    public async Task Namespaces_and_reads_are_isolated_between_two_real_users()
    {
        using var factory = new SqlApiFactory(fixture);
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        await AuthenticateAsync(firstClient);
        await AuthenticateAsync(secondClient);

        using var firstAccount = await CreateAccountAsync(firstClient, "A", "Shared");
        using var secondAccount = await CreateAccountAsync(secondClient, "B", "Shared");

        Assert.Equal(HttpStatusCode.Created, firstAccount.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondAccount.StatusCode);

        using var foreign = await secondClient.GetAsync(firstAccount.Headers.Location, TestCancellationToken);
        using var absent = await secondClient.GetAsync("/api/v1/accounts/" + Guid.NewGuid(), TestCancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(absent.StatusCode, foreign.StatusCode);
        using var foreignProblem = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync(TestCancellationToken));
        using var absentProblem = JsonDocument.Parse(await absent.Content.ReadAsStringAsync(TestCancellationToken));
        Assert.Equal(
            foreignProblem.RootElement.GetProperty("title").GetString(),
            absentProblem.RootElement.GetProperty("title").GetString());
        var firstPage = await firstClient.GetFromJsonAsync<AccountPage>("/api/v1/accounts", TestCancellationToken);
        var secondPage = await secondClient.GetFromJsonAsync<AccountPage>("/api/v1/accounts", TestCancellationToken);

        Assert.Equal("A", Assert.Single(firstPage!.Items).Name);
        Assert.Equal(1, firstPage.TotalCount);
        Assert.Equal("B", Assert.Single(secondPage!.Items).Name);
        Assert.Equal(1, secondPage.TotalCount);

        using var caseKey = await CreateAccountAsync(firstClient, "Separate", "shared");
        Assert.Equal(HttpStatusCode.Created, caseKey.StatusCode);
    }

    [Fact]
    public async Task Paging_filters_and_inactive_default_are_applied_on_owner_query()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        var ids = new List<Guid>();
        foreach (var name in new[]
        {
            "Z",
            "A",
            "A",
            "B"
        })
        {
            using var response = await CreateAccountAsync(client, name, Guid.NewGuid().ToString("N"));
            ids.Add((await ReadAccountAsync(response)).Id);
        }

        await using (var connection = new SqlConnection(fixture.MigrationConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dbo.FinancialAccounts SET IsActive=0, Type='cash' WHERE Id=@id";
            command.Parameters.AddWithValue("@id", ids[0]);
            await command.ExecuteNonQueryAsync(TestCancellationToken);
        }

        var page = await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts?pageSize=2", TestCancellationToken);
        Assert.Equal(4, page!.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.All(page.Items, x => Assert.Equal("A", x.Name));
        var again = await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts?pageSize=2", TestCancellationToken);
        Assert.Equal(page.Items.Select(x => x.Id), again!.Items.Select(x => x.Id));
        var inactive = await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts?isActive=false&type=cash", TestCancellationToken);
        Assert.Equal(ids[0], Assert.Single(inactive!.Items).Id);
        Assert.Equal(1, inactive.TotalCount);
        var empty = await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts?page=99", TestCancellationToken);
        Assert.Empty(empty!.Items);
        Assert.Equal(4, empty.TotalCount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("123.45")]
    [InlineData("-123.45")]
    [InlineData("999999999999.99")]
    [InlineData("-999999999999.99")]
    [InlineData("10.000")]
    public async Task Exact_monetary_boundaries_round_trip_in_sql(string value)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        using var response = await CreateAccountAsync(client, "Money", "boundary", value);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var get = await client.GetAsync(response.Headers.Location, TestCancellationToken);
        Assert.Equal(
            decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            (await ReadAccountAsync(get)).OpeningBalance);
    }

    [Theory]
    [InlineData("10.001")]
    [InlineData("1000000000000")]
    [InlineData("-1000000000000")]
    [InlineData("10.00000000000000000000000000000000000000001")]
    public async Task Invalid_original_amount_does_not_write_any_record(string value)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        using var response = await CreateAccountAsync(client, "Invalid", "invalid", value);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertRecordCountsAsync(factory, owner, 0);
    }

    [Fact]
    public async Task Invalid_contracts_and_keys_are_rejected_before_persistence()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        foreach (var json in new[]
        {
            "{}",
            Payload().Replace("checking", "Checking"),
            Payload().Replace("BRL", "USD"),
            Payload().Replace("Account", ""),
            Payload().Replace("2000-01-01", "2999-01-01"),
            Payload().Replace("\"name\":", "\"userId\":\"" + Guid.NewGuid() + "\",\"name\":")
        })
        {
            using var response = await SendCreationAsync(client, "invalid", json);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        foreach (var key in new string?[]
        {
            null,
            "",
            "has space",
            new string ('a', 129)
        })
        {
            using var response = await SendCreationAsync(client, key, Payload());
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        foreach (var query in new[]
        {
            "page=0",
            "pageSize=101",
            "isActive=invalid",
            "type=creditCard",
            "sort=other",
            "direction=sideways"
        })
        {
            using var response = await client.GetAsync("/api/v1/accounts?" + query, TestCancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        await AssertRecordCountsAsync(factory, owner, 0);
    }

    private static async Task<Guid> AuthenticateAsync(HttpClient client)
    {
        var (_, _, token) = await IdentitySqlTests.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Subject);
    }

    private static string Payload(string name = "Account", string value = "0") =>
        "{\"name\":" + JsonSerializer.Serialize(name)
        + ",\"type\":\"checking\",\"currency\":\"BRL\",\"openingBalance\":" + value
        + ",\"openingDate\":\"2000-01-01\"}";

    private static Task<HttpResponseMessage> CreateAccountAsync(HttpClient client, string name, string key, string value = "0") => SendCreationAsync(
        client,
        key,
        Payload(name, value));

    private static async Task<HttpResponseMessage> SendCreationAsync(HttpClient client, string? key, string payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request, TestCancellationToken);
    }

    private static async Task<FinancialAccountView> ReadAccountAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<FinancialAccountView>(TestCancellationToken))!;

    private static async Task AssertRecordCountsAsync(ApiFactory factory, Guid owner, int expected)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Equal(expected, await db.FinancialAccounts.CountAsync(x => x.UserId == owner, TestCancellationToken));
        Assert.Equal(expected, await db.IdempotencyRecords.CountAsync(x => x.UserId == owner, TestCancellationToken));
    }
}
