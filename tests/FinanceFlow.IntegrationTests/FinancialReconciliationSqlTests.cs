using System.Data.Common;
using System.Net;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Transactions;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionMutationRequests;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class FinancialReconciliationSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("0")]
    [InlineData("100.25")]
    [InlineData("-100.25")]
    public async Task Empty_accounts_keep_only_their_opening_position_in_every_applicable_cutoff(string openingText)
    {
        using var factory = new ReconciliationFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var opening = Money(openingText);
        var references = await ReferencesAsync(client, opening);

        Assert.Equal(0m, await BalanceAsync(client, references.AccountId, "1999-12-31"));
        Assert.Equal(opening, await BalanceAsync(client, references.AccountId, "2000-01-01"));
        Assert.Equal(opening, await BalanceAsync(client, references.AccountId, "2000-01-02"));
        Assert.Equal(opening, await BalanceAsync(client, references.AccountId));
        using var list = await client.GetAsync($"/api/v1/transactions?accountId={references.AccountId}", Cancellation);
        Assert.Equal(0, (await ReadAsync(list)).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Successive_changes_reconcile_against_independent_cent_exact_positions_in_two_accounts()
    {
        using var factory = new ReconciliationFactory(fixture);
        using var client = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var first = await ReferencesAsync(client, 100.25m);
        var second = await ReferencesAsync(client, -20.10m, "expense");
        var firstExpense = first with { CategoryId = second.CategoryId };
        var secondIncome = second with { CategoryId = first.CategoryId };
        var foreign = await ReferencesAsync(stranger);
        using var foreignIncome = await CreateAsync(stranger, foreign, amount: 999m);
        Assert.Equal(HttpStatusCode.Created, foreignIncome.StatusCode);

        using var openingIncome = await CreateAsync(client, first, amount: 10.10m);
        using var equalExpense = await CreateAsync(client, firstExpense, amount: 10.10m, kind: "expense");
        using var largeExpense = await CreateAsync(client, firstExpense, amount: 150.50m, kind: "expense", occurredOn: "2000-01-02");
        using var laterIncome = await CreateAsync(client, first, amount: 25.25m, occurredOn: "2000-01-04");
        using var secondReceipt = await CreateAsync(client, secondIncome, amount: 40.40m, occurredOn: "2000-01-02");
        using var secondExpense = await CreateAsync(client, second, amount: 1.01m, kind: "expense", occurredOn: "2000-01-03");
        foreach (var response in new[] { openingIncome, equalExpense, largeExpense, laterIncome, secondReceipt, secondExpense })
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        // These literal positions were calculated separately from the production aggregate.
        // Each pair is the first/second account at the same inclusive cutoff.
        await PositionsAsync(client, first, second,
            ("1999-12-31", 0m, 0m), ("2000-01-01", 100.25m, -20.10m),
            ("2000-01-02", -50.25m, 20.30m), ("2000-01-03", -50.25m, 19.29m),
            ("2000-01-04", -25m, 19.29m));

        using var retroactive = await CreateAsync(client, first, amount: 0.07m, occurredOn: "2000-01-02");
        Assert.Equal(HttpStatusCode.Created, retroactive.StatusCode);
        await PositionsAsync(client, first, second, ("2000-01-02", -50.18m, 20.30m), ("2000-01-04", -24.93m, 19.29m));

        using var reducedExpense = await MutateAsync(client, largeExpense.Headers.Location!, "PUT",
            Payload(await ReadAsync(largeExpense)) with { Amount = 50.50m }, largeExpense.Headers.ETag!.ToString());
        await SuccessAsync(reducedExpense);
        await PositionsAsync(client, first, second, ("2000-01-02", 49.82m, 20.30m), ("2000-01-04", 75.07m, 19.29m));

        using var reversedIncome = await MutateAsync(client, laterIncome.Headers.Location!, "PUT",
            Payload(await ReadAsync(laterIncome)) with { Kind = "expense", CategoryId = second.CategoryId }, laterIncome.Headers.ETag!.ToString());
        using var reversedExpense = await MutateAsync(client, secondExpense.Headers.Location!, "PUT",
            Payload(await ReadAsync(secondExpense)) with { Kind = "income", CategoryId = first.CategoryId }, secondExpense.Headers.ETag!.ToString());
        await SuccessAsync(reversedIncome);
        await SuccessAsync(reversedExpense);
        await PositionsAsync(client, first, second, ("2000-01-03", 49.82m, 21.31m), ("2000-01-04", 24.57m, 21.31m));

        using var moved = await MutateAsync(client, retroactive.Headers.Location!, "PUT",
            Payload(await ReadAsync(retroactive)) with { AccountId = second.AccountId, OccurredOn = new(2000, 1, 3) }, retroactive.Headers.ETag!.ToString());
        await SuccessAsync(moved);
        await PositionsAsync(client, first, second, ("2000-01-02", 49.75m, 20.30m), ("2000-01-04", 24.50m, 21.38m));

        using var laterExpense = await MutateAsync(client, largeExpense.Headers.Location!, "PUT",
            Payload(await ReadAsync(largeExpense)) with { Amount = 50.50m, OccurredOn = new(2000, 1, 4) }, reducedExpense.Headers.ETag!.ToString());
        await SuccessAsync(laterExpense);
        await PositionsAsync(client, first, second, ("2000-01-02", 100.25m, 20.30m), ("2000-01-04", 24.50m, 21.38m));

        using var deletedFirstIncome = await MutateAsync(client, openingIncome.Headers.Location!, "DELETE", null, openingIncome.Headers.ETag!.ToString());
        using var deletedSecondIncome = await MutateAsync(client, secondReceipt.Headers.Location!, "DELETE", null, secondReceipt.Headers.ETag!.ToString());
        await SuccessAsync(deletedFirstIncome);
        await SuccessAsync(deletedSecondIncome);
        await PositionsAsync(client, first, second, ("2000-01-01", 90.15m, -20.10m), ("2000-01-04", 14.40m, -19.02m));

        using var deletedExpense = await MutateAsync(client, largeExpense.Headers.Location!, "DELETE", null, laterExpense.Headers.ETag!.ToString());
        await SuccessAsync(deletedExpense);
        await DeactivateAsync(client, first.AccountLocation, first.AccountVersion);
        await DeactivateAsync(client, second.CategoryLocation, second.CategoryVersion);
        await PositionsAsync(client, first, second, ("2000-01-04", 64.90m, -19.02m));
        Assert.Equal(64.90m, await BalanceAsync(client, first.AccountId));
        Assert.Equal(-19.02m, await BalanceAsync(client, second.AccountId));

        using var page = await client.GetAsync("/api/v1/transactions?pageSize=1", Cancellation);
        var list = await ReadAsync(page);
        Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(4, list.GetProperty("totalCount").GetInt32());
        using var beyond = await client.GetAsync("/api/v1/transactions?page=99&pageSize=1", Cancellation);
        Assert.Empty((await ReadAsync(beyond)).GetProperty("items").EnumerateArray());
        Assert.Equal(64.90m, await BalanceAsync(client, first.AccountId, "2000-01-04"));
        Assert.Equal(-19.02m, await BalanceAsync(client, second.AccountId, "2000-01-04"));
        Assert.Equal(999m, await BalanceAsync(stranger, foreign.AccountId));
    }

    [Fact]
    public async Task Financial_midnight_changes_creation_edit_and_default_cutoff_without_using_the_machine_clock()
    {
        using var factory = new ReconciliationFactory(fixture);
        factory.Clock.Instant = new(2026, 11, 19, 2, 59, 59, TimeSpan.Zero);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var premature = await CreateAsync(client, references, occurredOn: "2026-11-19");
        Assert.Equal(HttpStatusCode.BadRequest, premature.StatusCode);
        using var original = await CreateAsync(client, references, occurredOn: "2026-11-18");
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var body = Payload(await ReadAsync(original)) with { OccurredOn = new(2026, 11, 19) };
        using var prematureEdit = await MutateAsync(client, original.Headers.Location!, "PUT", body, original.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, prematureEdit.StatusCode);
        using var before = await client.GetAsync($"/api/v1/accounts/{references.AccountId}/balance", Cancellation);
        Assert.Equal("2026-11-18", (await ReadAsync(before)).GetProperty("asOf").GetString());
        using var futureCutoff = await client.GetAsync($"/api/v1/accounts/{references.AccountId}/balance?asOf=2026-11-19", Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, futureCutoff.StatusCode);

        factory.Clock.Instant = new(2026, 11, 19, 3, 0, 0, TimeSpan.Zero);

        using var edited = await MutateAsync(client, original.Headers.Location!, "PUT", body, original.Headers.ETag.ToString());
        await SuccessAsync(edited);
        using var today = await CreateAsync(client, references, amount: 0.01m, occurredOn: "2026-11-19");
        Assert.Equal(HttpStatusCode.Created, today.StatusCode);
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId, "2026-11-18"));
        Assert.Equal(10.01m, await BalanceAsync(client, references.AccountId, "2026-11-19"));
        using var after = await client.GetAsync($"/api/v1/accounts/{references.AccountId}/balance", Cancellation);
        var position = await ReadAsync(after);
        Assert.Equal("2026-11-19", position.GetProperty("asOf").GetString());
        Assert.Equal(10.01m, position.GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task Sql_aggregate_overflow_returns_a_safe_error_instead_of_a_partial_balance()
    {
        var overflow = new AggregateOverflow();
        using var factory = new ReconciliationFactory(fixture, overflow);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client, 100.25m);
        overflow.Armed = true;

        using var failed = await client.GetAsync($"/api/v1/accounts/{references.AccountId}/balance", Cancellation);

        Assert.Equal(8115, overflow.SqlError);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.True(failed.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", failed.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync(failed);
        Assert.Equal("service_unavailable", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("balance", out _));
        Assert.False(problem.TryGetProperty("sqlError", out _));
        Assert.False(problem.TryGetProperty("stackTrace", out _));
        var publicBody = await failed.Content.ReadAsStringAsync(Cancellation);
        Assert.DoesNotContain("Arithmetic overflow", publicBody);
        Assert.DoesNotContain("SqlException", publicBody);
        Assert.DoesNotContain("Error Number", publicBody);
        Assert.Equal(100.25m, await BalanceAsync(client, references.AccountId));
    }

    private static async Task PositionsAsync(HttpClient client, TransactionReferences first, TransactionReferences second,
        params (string Cutoff, decimal First, decimal Second)[] positions)
    {
        foreach (var position in positions)
        {
            Assert.Equal(position.First, await BalanceAsync(client, first.AccountId, position.Cutoff));
            Assert.Equal(position.Second, await BalanceAsync(client, second.AccountId, position.Cutoff));
        }
    }

    private sealed class FinancialClock : TimeProvider
    {
        public DateTimeOffset Instant { get; set; } = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Instant;
    }

    private sealed class ReconciliationFactory(IdentitySqlFixture fixture, params IInterceptor[] interceptors) : ApiFactory()
    {
        public FinancialClock Clock { get; } = new();
        public override string ConnectionString => fixture.ApplicationConnectionString;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Control the financial calendar only. JWT still uses its real, unmodified clock
                // and validation, so this boundary test does not weaken authentication.
                services.AddScoped(provider => new FinancialTransactionsService(
                    provider.GetRequiredService<IFinancialTransactionStore>(), provider.GetRequiredService<ICurrentUser>(),
                    Clock, provider.GetRequiredService<FinancialCalendar>()));
                services.AddDbContext<FinanceFlowDbContext>(options =>
                {
                    options.AddInterceptors(interceptors);
                    if (interceptors.OfType<AggregateOverflow>().SingleOrDefault() is { } overflow)
                    {
                        // ExecuteReader can succeed before SQL reports an error while reading.
                        // Observe EF's iteration failure privately, never the sanitized HTTP body.
                        options.LogTo((eventId, _) => eventId == CoreEventId.QueryIterationFailed, overflow.QueryFailed);
                    }
                });
            });
        }
    }

    private sealed class AggregateOverflow : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int? SqlError { get; private set; }
        private bool injected;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("SUM(", StringComparison.Ordinal)
                && command.CommandText.Contains("[dbo].[FinancialTransactions]", StringComparison.Ordinal))
            {
                Armed = false;
                injected = true;
                // Fault injection exercises a real SQL arithmetic error, not normal bounded input.
                // Reaching decimal(38,2) overflow with valid individual amounts is impractical here.
                var maximum = new string('9', 36) + ".99";
                command.CommandText = $"SELECT SUM(amount) FROM (VALUES (CAST('{maximum}' AS decimal(38,2))), (CAST('{maximum}' AS decimal(38,2)))) AS inputs(amount)";
            }
            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (injected && eventData.Exception is SqlException exception)
            {
                SqlError = exception.Number;
            }
            return Task.CompletedTask;
        }

        public void QueryFailed(EventData eventData)
        {
            if (injected && eventData is DbContextTypeErrorEventData { Exception: SqlException exception })
            {
                SqlError = exception.Number;
            }
        }
    }
}
