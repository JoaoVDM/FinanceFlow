using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.CategoryReportRequests;
using static FinanceFlow.IntegrationTests.ReportTestRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class ConsolidatedReconciliationSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Independent_expected_positions_reconcile_all_read_models_after_each_historical_change()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        using var foreign = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(foreign);
        var a = await AccountAsync(client, 5000m, "2026-11-01");
        var b = await AccountAsync(client, 2000m, "2026-11-01");
        var other = await AccountAsync(foreign, 999m, "2026-11-01");
        var otherCategory = await CategoryAsync(foreign, "Salário", "income");
        using var hidden = await CategoryMovementAsync(foreign, other.Id, otherCategory.Id, "income", 999m);
        var salary = await CategoryAsync(client, "Salário", "income");
        var gym = await CategoryAsync(client, "Academia", "expense");
        var food = await CategoryAsync(client, "Alimentação", "expense");
        using var income = await CategoryMovementAsync(client, a.Id, salary.Id, "income", 4500m, "2026-11-05");
        using var expense = await CategoryMovementAsync(client, a.Id, gym.Id, "expense", 150m, "2026-11-10");
        using var deleted = await CategoryMovementAsync(client, a.Id, food.Id, "expense", 350m, "2026-11-15");
        using var transfer = await PostTransferAsync(client, new(a.Id, b.Id, 1000m, new(2026, 11, 20), null));
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        await CheckAsync(client, a.Id, b.Id, new(4500, 500, 0, 0, 8000, 3000, 11000, 3, 1));

        using var edit = new HttpRequestMessage(HttpMethod.Put, expense.Headers.Location)
        {
            Content = JsonContent.Create(new { accountId = a.Id, categoryId = gym.Id, kind = "expense", amount = 200m, occurredOn = "2026-11-10" })
        };
        edit.Headers.Add("If-Match", expense.Headers.ETag!.ToString());
        using var edited = await client.SendAsync(edit, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        await CheckAsync(client, a.Id, b.Id, new(4500, 550, 0, 0, 7950, 3000, 10950, 3, 1));

        using var retroactive = await CategoryMovementAsync(client, b.Id, salary.Id, "income", 250m, "2026-11-02");
        await CheckAsync(client, a.Id, b.Id, new(4500, 550, 250, 0, 7950, 3250, 11200, 4, 1));
        using var remove = new HttpRequestMessage(HttpMethod.Delete, deleted.Headers.Location);
        remove.Headers.Add("If-Match", deleted.Headers.ETag!.ToString());
        using var removed = await client.SendAsync(remove, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await CheckAsync(client, a.Id, b.Id, new(4500, 200, 250, 0, 8300, 3250, 11550, 3, 1));

        using var cancel = await TransferCancellationRequests.CancelAsync(client, transfer.Headers.Location!, transfer.Headers.ETag!.ToString());
        await TransferCancellationRequests.AssertSuccessAsync(cancel);
        await CheckAsync(client, a.Id, b.Id, new(4500, 200, 250, 0, 9300, 2250, 11550, 3, 0));
        using var rename = new HttpRequestMessage(HttpMethod.Put, salary.Location) { Content = JsonContent.Create(new { name = "Pagamento" }) };
        rename.Headers.Add("If-Match", salary.Version);
        using var renamed = await client.SendAsync(rename, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        await DeactivateAsync(client, a.Location, a.Version);
        await DeactivateAsync(client, gym.Location, gym.Version);
        await CheckAsync(client, a.Id, b.Id, new(4500, 200, 250, 0, 9300, 2250, 11550, 3, 0));
        var groups = await GroupsAsync(client);
        Assert.Equal(new[] { "Academia", "Pagamento" }, groups.Select(value => value.Name));
        using var inactive = await client.GetAsync("/api/v1/accounts?isActive=false", Cancellation);
        Assert.Equal(a.Id, Assert.Single((await ReadAsync(inactive)).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    private sealed record Expected(decimal IncomeA, decimal ExpenseA, decimal IncomeB, decimal ExpenseB,
        decimal BalanceA, decimal BalanceB, decimal Wealth, int Transactions, int Transfers);

    private static async Task CheckAsync(HttpClient client, Guid a, Guid b, Expected expected)
    {
        // Values passed by the test are independent expectations, never computed from another endpoint.
        var income = expected.IncomeA + expected.IncomeB;
        var expense = expected.ExpenseA + expected.ExpenseB;
        Totals(await SummaryAsync(client), income, expense);
        var groups = await GroupsAsync(client);
        Assert.Equal(income, groups.Where(value => value.Kind == "income").Sum(value => value.Total));
        Assert.Equal(expense, groups.Where(value => value.Kind == "expense").Sum(value => value.Total));
        var statementA = await FinancialReadRequests.StatementAsync(client, a, "2026-11-01", "2026-11-26", 1, 1);
        var statementB = await FinancialReadRequests.StatementAsync(client, b, "2026-11-01", "2026-11-26", 1, 1);
        AssertPosition(statementA, 5000, expected.IncomeA, expected.ExpenseA, expected.BalanceA, 0, expected.Transfers * 1000m);
        AssertPosition(statementB, 2000, expected.IncomeB, expected.ExpenseB, expected.BalanceB, expected.Transfers * 1000m, 0);
        var beyond = await FinancialReadRequests.StatementAsync(client, a, "2026-11-01", "2026-11-26", 99, 1);
        Assert.Equal(0, beyond.GetProperty("items").GetArrayLength());
        foreach (var property in new[] { "openingBalance", "closingBalance", "income", "expense", "transferIn", "transferOut", "totalCount", "totalPages" })
        {
            Assert.Equal(statementA.GetProperty(property).GetRawText(), beyond.GetProperty(property).GetRawText());
        }
        Assert.Equal(expected.BalanceA, await BalanceAsync(client, a, "2026-11-26"));
        Assert.Equal(expected.BalanceB, await BalanceAsync(client, b, "2026-11-26"));
        Assert.Equal(expected.Wealth, statementA.GetProperty("closingBalance").GetDecimal() + statementB.GetProperty("closingBalance").GetDecimal());
        using var transactions = await client.GetAsync("/api/v1/transactions?from=2026-11-01&to=2026-11-26&pageSize=100", Cancellation);
        var page = await ReadAsync(transactions);
        Assert.Equal(expected.Transactions, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(expected.Transactions, page.GetProperty("items").GetArrayLength());
        Assert.Equal(income, page.GetProperty("items").EnumerateArray().Where(value => value.GetProperty("kind").GetString() == "income").Sum(value => value.GetProperty("amount").GetDecimal()));
        Assert.Equal(expense, page.GetProperty("items").EnumerateArray().Where(value => value.GetProperty("kind").GetString() == "expense").Sum(value => value.GetProperty("amount").GetDecimal()));
        foreach (var account in new[] { a, b })
        {
            using var transfers = await client.GetAsync($"/api/v1/transfers?accountId={account}&from=2026-11-01&to=2026-11-26", Cancellation);
            var transferPage = await ReadAsync(transfers);
            Assert.Equal(expected.Transfers, transferPage.GetProperty("totalCount").GetInt32());
            Assert.Equal(expected.Transfers, transferPage.GetProperty("items").GetArrayLength());
            Assert.Equal(expected.Transfers * 1000m, transferPage.GetProperty("items").EnumerateArray().Sum(value => value.GetProperty("amount").GetDecimal()));
        }
    }

    private static void AssertPosition(JsonElement statement, decimal opening, decimal income, decimal expense, decimal closing, decimal transferIn, decimal transferOut)
    {
        Assert.Equal(opening, statement.GetProperty("openingBalance").GetDecimal());
        Assert.Equal(income, statement.GetProperty("income").GetDecimal());
        Assert.Equal(expense, statement.GetProperty("expense").GetDecimal());
        Assert.Equal(income - expense, statement.GetProperty("result").GetDecimal());
        Assert.Equal(transferIn, statement.GetProperty("transferIn").GetDecimal());
        Assert.Equal(transferOut, statement.GetProperty("transferOut").GetDecimal());
        Assert.Equal(closing, statement.GetProperty("closingBalance").GetDecimal());
    }

    private static async Task DeactivateAsync(HttpClient client, Uri location, string version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, location) { Content = JsonContent.Create(new { isActive = false }) };
        request.Headers.Add("If-Match", version);
        using var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
