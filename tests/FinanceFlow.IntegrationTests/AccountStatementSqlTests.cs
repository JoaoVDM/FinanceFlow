using System.Net;
using System.Net.Http.Json;
using FinanceFlow.Domain.Transactions;
using FinanceFlow.Domain.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.TransferCancellationRequests;
using static FinanceFlow.IntegrationTests.FinancialReadRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class AccountStatementSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("2000-01-01", 800, 800, 0, 800)]
    [InlineData("2000-01-03", 800, 800, 0, 800)]
    [InlineData("2000-01-05", 800, 0, 800, 800)]
    [InlineData("2000-01-07", 800, 0, 0, 0)]
    [InlineData("2000-01-03", 0, 0, 0, 0)]
    [InlineData("2000-01-03", -800, -800, 0, -800)]
    [InlineData("2000-01-05", -800, 0, -800, -800)]
    public async Task Empty_account_opening_is_not_a_fictitious_income(string openingDate, int registered, int opening, int contribution, int closing)
    {
        using var factory = new FinancialReadApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client, registered, openingDate);

        var statement = await StatementAsync(client, account.Id);

        Assert.Equal((decimal)registered, statement.GetProperty("accountOpeningBalance").GetDecimal());
        Assert.Equal(openingDate, statement.GetProperty("accountOpeningDate").GetString());
        Assert.Equal((decimal)opening, statement.GetProperty("openingBalance").GetDecimal());
        Assert.Equal((decimal)contribution, statement.GetProperty("accountOpeningContribution").GetDecimal());
        Assert.Equal((decimal)closing, statement.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(0m, statement.GetProperty("income").GetDecimal());
        Assert.Empty(statement.GetProperty("items").EnumerateArray());
        Assert.Equal(0, statement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, statement.GetProperty("totalPages").GetInt32());
        Assert.Equal(await BalanceAsync(client, account.Id, "2000-01-06"), statement.GetProperty("closingBalance").GetDecimal());
    }

    [Fact]
    public async Task Complete_ledger_reconciles_independent_totals_history_counterparties_and_every_page()
    {
        using var factory = new FinancialReadApiFactory(fixture);
        using var client = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var a = await AccountAsync(client, 900m);
        var b = await AccountAsync(client, 100m);
        using var priorIncome = await MovementAsync(client, a.Id, "income", 100m, "2000-01-02");
        using var priorExpense = await MovementAsync(client, a.Id, "expense", 20m, "2000-01-02");
        using var priorIn = await PostTransferAsync(client, new(b.Id, a.Id, 30m, new(2000, 1, 2), null));
        using var priorOut = await PostTransferAsync(client, new(a.Id, b.Id, 10m, new(2000, 1, 2), null));
        using var income = await MovementAsync(client, a.Id, "income", 500m, "2000-01-03");
        using var expense = await MovementAsync(client, a.Id, "expense", 200m, "2000-01-06");
        using var incoming = await PostTransferAsync(client, new(b.Id, a.Id, 300m, new(2000, 1, 3), "Incoming"));
        using var outgoing = await PostTransferAsync(client, new(a.Id, b.Id, 100m, new(2000, 1, 6), "Outgoing"));
        using var canceled = await PostTransferAsync(client, new(a.Id, b.Id, 999m, new(2000, 1, 3), null));
        using var cancel = await CancelAsync(client, canceled.Headers.Location!, canceled.Headers.ETag!.ToString());
        await AssertSuccessAsync(cancel);
        using var deleted = await MovementAsync(client, a.Id, "income", 999m);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, deleted.Headers.Location);
        deleteRequest.Headers.Add("If-Match", deleted.Headers.ETag!.ToString());
        using var delete = await client.SendAsync(deleteRequest, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var afterPeriod = await MovementAsync(client, a.Id, "income", 777m, "2000-01-07");
        var foreign = await AccountAsync(stranger, 9999m);
        using var foreignIncome = await MovementAsync(stranger, foreign.Id, "income", 9999m);
        var historicalCategory = (await ReadAsync(income)).GetProperty("categoryId").GetGuid();
        using var categoryGet = await client.GetAsync($"/api/v1/categories/{historicalCategory}", Cancellation);
        using var categoryPatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/categories/{historicalCategory}")
        {
            Content = JsonContent.Create(new { isActive = false })
        };
        categoryPatch.Headers.Add("If-Match", categoryGet.Headers.ETag!.ToString());
        using var categoryDeactivated = await client.SendAsync(categoryPatch, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, categoryDeactivated.StatusCode);
        await TransactionMutationRequests.DeactivateAsync(client, a.Location, a.Version);

        var observed = new List<Guid>();
        var dates = new List<string?>();
        for (var page = 1; page <= 3; page++)
        {
            var statement = await StatementAsync(client, a.Id, page: page, size: 2);
            // Fixed expectations calculated from the fictitious ledger, not the production balance function.
            Assert.Equal(1000m, statement.GetProperty("openingBalance").GetDecimal());
            Assert.Equal(0m, statement.GetProperty("accountOpeningContribution").GetDecimal());
            Assert.Equal(500m, statement.GetProperty("income").GetDecimal());
            Assert.Equal(200m, statement.GetProperty("expense").GetDecimal());
            Assert.Equal(300m, statement.GetProperty("result").GetDecimal());
            Assert.Equal(300m, statement.GetProperty("transferIn").GetDecimal());
            Assert.Equal(100m, statement.GetProperty("transferOut").GetDecimal());
            Assert.Equal(1500m, statement.GetProperty("closingBalance").GetDecimal());
            Assert.Equal(4, statement.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, statement.GetProperty("totalPages").GetInt32());
            foreach (var item in statement.GetProperty("items").EnumerateArray())
            {
                observed.Add(item.GetProperty("operationId").GetGuid());
                dates.Add(item.GetProperty("occurredOn").GetString());
                var nature = item.GetProperty("nature").GetString();
                var transfer = nature is "transferIn" or "transferOut";
                Assert.Equal(transfer ? "transfer" : "transaction", item.GetProperty("operationType").GetString());
                Assert.Equal(transfer, item.GetProperty("categoryId").ValueKind == System.Text.Json.JsonValueKind.Null);
                if (transfer)
                {
                    Assert.Equal(b.Id, item.GetProperty("counterpartyAccountId").GetGuid());
                }
                else
                {
                    Assert.Equal(System.Text.Json.JsonValueKind.Null, item.GetProperty("counterpartyAccountId").ValueKind);
                }
                Assert.Equal(nature is "income" or "transferIn", item.GetProperty("amount").GetDecimal() > 0m);
                Assert.False(item.TryGetProperty("userId", out _));
                Assert.False(item.TryGetProperty("runningBalance", out _));
            }
        }
        Assert.Equal(4, observed.Distinct().Count());
        Assert.Equal(new[] { "2000-01-06", "2000-01-06", "2000-01-03", "2000-01-03" }, dates);
        Assert.Equal(new[] { income, expense, incoming, outgoing }.Select(response => response.Headers.Location!.ToString().Split('/').Last())
            .Select(Guid.Parse).Order(), observed.Order());
        Assert.Equal(1500m, await BalanceAsync(client, a.Id, "2000-01-06"));
        Assert.Equal(-120m, await BalanceAsync(client, b.Id, "2000-01-06"));
        Assert.Equal(1380m, await BalanceAsync(client, a.Id, "2000-01-06") + await BalanceAsync(client, b.Id, "2000-01-06"));
        var otherStatement = await StatementAsync(client, b.Id);
        Assert.Equal(100m, otherStatement.GetProperty("transferIn").GetDecimal());
        Assert.Equal(300m, otherStatement.GetProperty("transferOut").GetDecimal());
        Assert.Equal(0m, otherStatement.GetProperty("result").GetDecimal());
        Assert.Equal(1000m, await BalanceAsync(client, a.Id, "2000-01-02"));
    }

    [Fact]
    public async Task Opening_during_period_adds_registered_balance_once_and_does_not_inflate_income()
    {
        using var factory = new FinancialReadApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client, 800m, "2000-01-05");
        using var income = await MovementAsync(client, account.Id, "income", 200m, "2000-01-05");
        using var expense = await MovementAsync(client, account.Id, "expense", 50m, "2000-01-06");

        var statement = await StatementAsync(client, account.Id);

        Assert.Equal(0m, statement.GetProperty("openingBalance").GetDecimal());
        Assert.Equal(800m, statement.GetProperty("accountOpeningContribution").GetDecimal());
        Assert.Equal(200m, statement.GetProperty("income").GetDecimal());
        Assert.Equal(950m, statement.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(2, statement.GetProperty("totalCount").GetInt32());
        Assert.Equal(950m, await BalanceAsync(client, account.Id, "2000-01-06"));
    }

    [Fact]
    public async Task SQL_pagination_and_totals_cover_150_cent_movements_with_stable_reference_ties()
    {
        using var factory = new FinancialReadApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var a = await AccountAsync(client, -2m);
        var b = await AccountAsync(client);
        using var categoryResponse = await CategoryTestRequests.CreateAsync(client, Guid.NewGuid().ToString("N"), "income");
        var category = (await ReadAsync(categoryResponse)).GetProperty("id").GetGuid();
        var options = new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(fixture.MigrationConnectionString).Options;
        await using (var seed = new FinanceFlowDbContext(options))
        {
            var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 150; i++)
            {
                seed.FinancialTransactions.Add(FinancialTransaction.Create(Guid.NewGuid(), owner, a.Id, category, "income",
                    0.01m, new(2000, 1, 3), null, new(2026, 11, 21), now));
            }
            // Same GUID in different source tables must still be two distinct references.
            var sharedId = Guid.NewGuid();
            seed.FinancialTransactions.Add(FinancialTransaction.Create(sharedId, owner, a.Id, category, "income", 0.01m,
                new(2000, 1, 3), null, new(2026, 11, 21), now));
            seed.Transfers.Add(Transfer.Create(sharedId, owner, a.Id, b.Id, 0.01m, new(2000, 1, 3), null, new(2026, 11, 21), now));
            await seed.SaveChangesAsync(Cancellation);
        }

        var seen = new List<(Guid, string)>();
        for (var page = 1; page <= 3; page++)
        {
            var statement = await StatementAsync(client, a.Id, page: page, size: 100);
            Assert.Equal(152, statement.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, statement.GetProperty("totalPages").GetInt32());
            Assert.Equal(1.51m, statement.GetProperty("income").GetDecimal());
            Assert.Equal(0.01m, statement.GetProperty("transferOut").GetDecimal());
            Assert.Equal(-0.50m, statement.GetProperty("closingBalance").GetDecimal());
            seen.AddRange(statement.GetProperty("items").EnumerateArray().Select(item =>
                (item.GetProperty("operationId").GetGuid(), item.GetProperty("operationType").GetString()!)));
        }
        Assert.Equal(152, seen.Count);
        Assert.Equal(152, seen.Distinct().Count());
        var repeat = await StatementAsync(client, a.Id, page: 1, size: 100);
        Assert.Equal(seen.Take(100), repeat.GetProperty("items").EnumerateArray().Select(item =>
            (item.GetProperty("operationId").GetGuid(), item.GetProperty("operationType").GetString()!)));
    }

    [Fact]
    public async Task Retroactive_edit_reassignment_delete_and_transfer_cancellation_recompute_valid_history()
    {
        using var factory = new FinancialReadApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var a = await AccountAsync(client, 10m);
        var b = await AccountAsync(client);
        using var income = await MovementAsync(client, a.Id, "income", 100m, "2000-01-02");
        Assert.Equal(110m, (await StatementAsync(client, a.Id)).GetProperty("openingBalance").GetDecimal());
        using var expenseCategory = await CategoryTestRequests.CreateAsync(client, Guid.NewGuid().ToString("N"), "expense");
        using var updateRequest = new HttpRequestMessage(HttpMethod.Put, income.Headers.Location)
        {
            Content = JsonContent.Create(new { accountId = b.Id, categoryId = (await ReadAsync(expenseCategory)).GetProperty("id").GetGuid(),
                kind = "expense", amount = 25.01m, occurredOn = "2000-01-04", description = (string?)null })
        };
        updateRequest.Headers.Add("If-Match", income.Headers.ETag!.ToString());
        using var update = await client.SendAsync(updateRequest, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(10m, (await StatementAsync(client, a.Id)).GetProperty("closingBalance").GetDecimal());
        Assert.Equal(-25.01m, (await StatementAsync(client, b.Id)).GetProperty("closingBalance").GetDecimal());
        using var transfer = await PostTransferAsync(client, new(a.Id, b.Id, 5.01m, new(2000, 1, 4), null));
        Assert.Equal(4.99m, (await StatementAsync(client, a.Id)).GetProperty("closingBalance").GetDecimal());
        using var cancel = await CancelAsync(client, transfer.Headers.Location!, transfer.Headers.ETag!.ToString());
        await AssertSuccessAsync(cancel);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, income.Headers.Location);
        deleteRequest.Headers.Add("If-Match", update.Headers.ETag!.ToString());
        using var delete = await client.SendAsync(deleteRequest, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var finalA = await StatementAsync(client, a.Id);
        var finalB = await StatementAsync(client, b.Id);
        Assert.Equal(10m, finalA.GetProperty("closingBalance").GetDecimal());
        Assert.Equal(0m, finalB.GetProperty("closingBalance").GetDecimal());
        Assert.Empty(finalA.GetProperty("items").EnumerateArray());
        Assert.Empty(finalB.GetProperty("items").EnumerateArray());
        Assert.Equal(10m, await BalanceAsync(client, a.Id, "2000-01-06"));
        Assert.Equal(0m, await BalanceAsync(client, b.Id, "2000-01-06"));
    }
}
