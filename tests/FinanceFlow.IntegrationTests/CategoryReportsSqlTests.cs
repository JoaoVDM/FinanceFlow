using System.Data.SqlTypes;
using System.Net;
using System.Net.Http.Json;
using FinanceFlow.Application.Reports;
using FinanceFlow.Infrastructure.Persistence;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.TransferCancellationRequests;
using static FinanceFlow.IntegrationTests.ReportTestRequests;
using static FinanceFlow.IntegrationTests.CategoryReportRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class CategoryReportsSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Reference_categories_reconcile_with_summary_statements_and_balances_without_opening_or_transfers()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var nubank = await AccountAsync(client, 5000m, "2026-11-01");
        var itau = await AccountAsync(client, 2000m, "2026-11-01");
        var salary = await CategoryAsync(client, "Salário", "income");
        var gym = await CategoryAsync(client, "Academia", "expense");
        var food = await CategoryAsync(client, "Alimentação", "expense");
        using var income = await CategoryMovementAsync(client, nubank.Id, salary.Id, "income", 4500m, "2026-11-05");
        using var expense1 = await CategoryMovementAsync(client, nubank.Id, gym.Id, "expense", 150m, "2026-11-10");
        using var expense2 = await CategoryMovementAsync(client, nubank.Id, food.Id, "expense", 350m, "2026-11-15");
        using var transfer = await PostTransferAsync(client, new(nubank.Id, itau.Id, 1000m, new(2026, 11, 20), null));
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        CategoryReportGroup[] expected = [new(food.Id, "Alimentação", "expense", 350m), new(gym.Id, "Academia", "expense", 150m), new(salary.Id, "Salário", "income", 4500m)];

        Assert.Equal(expected, await GroupsAsync(client));
        Assert.Equal(expected, await GroupsAsync(client, nubank.Id));
        Assert.Empty(await GroupsAsync(client, itau.Id));
        Assert.Equal(expected.Take(2), await GroupsAsync(client, kind: "expense"));
        Assert.Equal(expected.Skip(2), await GroupsAsync(client, nubank.Id, "income"));
        var summary = await SummaryAsync(client);
        Totals(summary, 4500m, 500m);
        Assert.Equal(summary.GetProperty("income").GetDecimal(), expected.Where(value => value.Kind == "income").Sum(value => value.Total));
        Assert.Equal(summary.GetProperty("expense").GetDecimal(), expected.Where(value => value.Kind == "expense").Sum(value => value.Total));
        var statement = await FinancialReadRequests.StatementAsync(client, nubank.Id, "2026-11-01", "2026-11-26");
        Assert.Equal(4000m, statement.GetProperty("result").GetDecimal());
        Assert.Equal(8000m, await BalanceAsync(client, nubank.Id, "2026-11-26"));
        Assert.Equal(3000m, await BalanceAsync(client, itau.Id, "2026-11-26"));
        using var canceled = await CancelAsync(client, transfer.Headers.Location!, transfer.Headers.ETag!.ToString());
        await AssertSuccessAsync(canceled);
        Assert.Equal(expected, await GroupsAsync(client));
    }

    [Fact]
    public async Task Identity_current_names_and_inactive_history_survive_filters_and_other_owners()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        using var other = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(other);
        var a = await AccountAsync(client, -99m);
        var b = await AccountAsync(client, 500m);
        var foreign = await AccountAsync(other);
        var expense = await CategoryAsync(client, "Shared", "expense");
        var income = await CategoryAsync(client, "Shared", "income");
        _ = await CategoryAsync(client, "Unused", "expense");
        var foreignCategory = await CategoryAsync(other, "Shared", "expense");
        using var first = await CategoryMovementAsync(client, a.Id, expense.Id, "expense", 1.01m);
        using var last = await CategoryMovementAsync(client, a.Id, expense.Id, "expense", 2.02m, "2026-11-26");
        using var secondAccount = await CategoryMovementAsync(client, b.Id, expense.Id, "expense", 3.03m, "2026-11-10");
        using var ownIncome = await CategoryMovementAsync(client, a.Id, income.Id, "income", 8.08m, "2026-11-05");
        using var otherAccountIncome = await CategoryMovementAsync(client, b.Id, income.Id, "income", 4.04m, "2026-11-15");
        using var foreignExpense = await CategoryMovementAsync(other, foreign.Id, foreignCategory.Id, "expense", 999m);
        var groups = await GroupsAsync(client);
        Assert.Equal(new[] { new CategoryReportGroup(expense.Id, "Shared", "expense", 6.06m), new(income.Id, "Shared", "income", 12.12m) }, groups);
        Assert.Equal(999m, Assert.Single(await GroupsAsync(other)).Total);
        Assert.Equal(new[] { new CategoryReportGroup(expense.Id, "Shared", "expense", 3.03m), new(income.Id, "Shared", "income", 4.04m) }, await GroupsAsync(client, b.Id));
        Assert.Equal(4.04m, Assert.Single(await GroupsAsync(client, b.Id, "income")).Total);
        Assert.Equal(3.03m, Assert.Single(await GroupsAsync(client, b.Id, "expense")).Total);
        Totals(await SummaryAsync(client, b.Id), 4.04m, 3.03m);
        Assert.Equal(1.01m, Assert.Single(await GroupsAsync(client, a.Id, "expense", "2026-11-01", "2026-11-01")).Total);
        Assert.Equal(2.02m, Assert.Single(await GroupsAsync(client, a.Id, "expense", "2026-11-26", "2026-11-26")).Total);
        using var rename = new HttpRequestMessage(HttpMethod.Put, expense.Location) { Content = JsonContent.Create(new { name = "Diversos" }) };
        rename.Headers.Add("If-Match", expense.Version);
        using var renamed = await client.SendAsync(rename, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        await DeactivateAsync(client, expense.Location, renamed.Headers.ETag!.ToString());
        await DeactivateAsync(client, a.Location, a.Version);

        var inactive = await GroupsAsync(client, a.Id);
        Assert.Equal(new[] { new CategoryReportGroup(expense.Id, "Diversos", "expense", 3.03m), new(income.Id, "Shared", "income", 8.08m) }, inactive);
        var summary = await SummaryAsync(client, a.Id);
        Totals(summary, 8.08m, 3.03m);
        Assert.Equal(summary.GetProperty("result").GetDecimal(), inactive.Where(value => value.Kind == "income").Sum(value => value.Total)
            - inactive.Where(value => value.Kind == "expense").Sum(value => value.Total));
    }

    [Fact]
    public async Task Empty_histories_and_categories_without_movements_produce_only_an_empty_array()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        Assert.Empty(await GroupsAsync(client));
        var account = await AccountAsync(client, 900m, "2026-11-05");
        _ = await CategoryAsync(client, "Never used", "income");
        Assert.Empty(await GroupsAsync(client, account.Id));
        Assert.Empty(await GroupsAsync(client, account.Id, "expense"));
        Assert.Empty(await GroupsAsync(client, account.Id, from: "2026-11-01", to: "2026-11-04"));
    }

    [Fact]
    public async Task Edits_retroactive_dates_and_deletions_change_only_current_valid_groups()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var a = await AccountAsync(client);
        var b = await AccountAsync(client);
        var income = await CategoryAsync(client, "Income", "income");
        var expense = await CategoryAsync(client, "Expense", "expense");
        using var before = await CategoryMovementAsync(client, a.Id, expense.Id, "expense", 99m, "2026-10-31");
        using var after = await CategoryMovementAsync(client, a.Id, expense.Id, "expense", 88m, "2026-11-26");
        using var original = await CategoryMovementAsync(client, a.Id, income.Id, "income", 10m, "2026-11-02");
        using var edit = new HttpRequestMessage(HttpMethod.Put, original.Headers.Location)
        {
            Content = JsonContent.Create(new { accountId = b.Id, categoryId = expense.Id, kind = "expense", amount = 5.55m, occurredOn = "2026-11-01" })
        };
        edit.Headers.Add("If-Match", original.Headers.ETag!.ToString());
        using var edited = await client.SendAsync(edit, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        using var retroactive = await CategoryMovementAsync(client, a.Id, income.Id, "income", 3.03m);
        var groups = await GroupsAsync(client, from: "2026-11-01", to: "2026-11-25");
        Assert.Equal(new[] { new CategoryReportGroup(expense.Id, "Expense", "expense", 5.55m), new(income.Id, "Income", "income", 3.03m) }, groups);
        Assert.Empty(await GroupsAsync(client, a.Id, "expense", "2026-11-01", "2026-11-25"));
        using var delete = new HttpRequestMessage(HttpMethod.Delete, original.Headers.Location);
        delete.Headers.Add("If-Match", edited.Headers.ETag!.ToString());
        using var deleted = await client.SendAsync(delete, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(new CategoryReportGroup(income.Id, "Income", "income", 3.03m), Assert.Single(await GroupsAsync(client, from: "2026-11-01", to: "2026-11-25")));
    }

    [Fact]
    public async Task Exact_large_totals_and_SQL_GUID_ties_follow_the_fixed_order()
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client);
        var large = await CategoryAsync(client, "Large", "income");
        var small = await CategoryAsync(client, "Small", "income");
        var firstExpense = await CategoryAsync(client, "First expense", "expense");
        var secondExpense = await CategoryAsync(client, "Second expense", "expense");
        using var first = await CategoryMovementAsync(client, account.Id, large.Id, "income", 999999999999.99m);
        using var second = await CategoryMovementAsync(client, account.Id, large.Id, "income", 999999999999.99m);
        using var tiny = await CategoryMovementAsync(client, account.Id, small.Id, "income", 1.01m);
        using var expenseA = await CategoryMovementAsync(client, account.Id, firstExpense.Id, "expense", 2.02m);
        using var expenseB = await CategoryMovementAsync(client, account.Id, secondExpense.Id, "expense", 2.02m);
        CategoryReportGroup[] expenses = [new(firstExpense.Id, "First expense", "expense", 2.02m), new(secondExpense.Id, "Second expense", "expense", 2.02m)];
        var expected = expenses.OrderBy(value => new SqlGuid(value.CategoryId))
            .Concat([new(large.Id, "Large", "income", 1999999999999.98m), new(small.Id, "Small", "income", 1.01m)]).ToArray();
        Assert.Equal(expected, await GroupsAsync(client));
        Assert.Equal(expected, await GroupsAsync(client, account.Id));
        Totals(await SummaryAsync(client, account.Id), 2000000000000.99m, 4.04m);
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, Cancellation);
    }

    private static async Task DeactivateAsync(HttpClient client, Uri location, string version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, location) { Content = JsonContent.Create(new { isActive = false }) };
        request.Headers.Add("If-Match", version);
        using var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
