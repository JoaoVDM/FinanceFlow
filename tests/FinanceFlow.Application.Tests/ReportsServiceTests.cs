using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Reports;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class ReportsServiceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("2024-01-01", "2024-12-31", true)]
    [InlineData("2024-01-01", "2025-01-01", false)]
    [InlineData("2026-11-01", "2026-11-26", true)]
    [InlineData("2026-11-26", "2026-11-26", true)]
    [InlineData("2026-11-26", "2026-11-25", false)]
    [InlineData("2026-11-26", "2026-11-27", false)]
    [InlineData("0001-01-01", "0001-01-01", true)]
    [InlineData("0001-01-01", "9999-12-31", false)]
    [InlineData("9999-12-31", "9999-12-31", false)]
    public async Task Period_validation_uses_inclusive_financial_dates_without_overflow(string from, string to, bool valid)
    {
        var store = new Store();
        var result = await Service(store).GetSummaryAsync(new(DateOnly.Parse(from), DateOnly.Parse(to)), Cancellation);

        Assert.Equal(valid ? null : SummaryFailure.InvalidInput, result.Error);
        Assert.Equal(valid ? 1 : 0, store.Calls);
    }

    [Theory]
    [InlineData("2026-11-27T02:59:59Z", false)]
    [InlineData("2026-11-27T03:00:00Z", true)]
    public async Task Today_uses_the_injected_Sao_Paulo_calendar(string instant, bool accepted)
    {
        var store = new Store();
        var service = new ReportsService(store, new User(Guid.NewGuid()), new Clock(DateTimeOffset.Parse(instant)), new());

        var result = await service.GetSummaryAsync(new(new(2026, 11, 27), new(2026, 11, 27)), Cancellation);

        Assert.Equal(accepted ? null : SummaryFailure.InvalidInput, result.Error);
        Assert.Equal(accepted ? 1 : 0, store.Calls);
    }

    [Fact]
    public async Task Missing_identity_never_reads_financial_storage()
    {
        var store = new Store();
        var result = await Service(store, Guid.Empty).GetSummaryAsync(new(new(2026, 11, 1), new(2026, 11, 26)), Cancellation);
        Assert.Equal(SummaryFailure.Unauthenticated, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Empty_account_filter_is_rejected_before_storage()
    {
        var store = new Store();
        var result = await Service(store).GetSummaryAsync(new(new(2026, 11, 1), new(2026, 11, 26), Guid.Empty), Cancellation);
        Assert.Equal(SummaryFailure.InvalidInput, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_queries_forward_only_the_authenticated_owner_and_preserve_the_result(bool filter)
    {
        var owner = Guid.NewGuid();
        var store = new Store { Result = new(Summary: new("BRL", new(2026, 11, 1), new(2026, 11, 26), 4m, 5m, -1m)) };
        var query = new SummaryQuery(new(2026, 11, 1), new(2026, 11, 26), filter ? Guid.NewGuid() : null);

        var result = await Service(store, owner).GetSummaryAsync(query, Cancellation);

        Assert.Same(store.Result, result);
        Assert.Equal(owner, store.Owner);
        Assert.Equal(query, store.Query);
        Assert.Equal(Cancellation, store.Cancellation);
        Assert.Equal(1, store.Calls);
    }

    [Theory]
    [InlineData(SummaryFailure.NotFound)]
    [InlineData(SummaryFailure.Unavailable)]
    public async Task Store_failures_are_preserved_without_retry(SummaryFailure failure)
    {
        var store = new Store { Result = new(Error: failure) };
        var result = await Service(store).GetSummaryAsync(new(new(2026, 11, 1), new(2026, 11, 26)), Cancellation);
        Assert.Same(store.Result, result);
        Assert.Equal(1, store.Calls);
    }

    private static ReportsService Service(Store store, Guid? owner = null) =>
        new(store, new User(owner ?? Guid.NewGuid()), new Clock(new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero)), new());
    private sealed class Clock(DateTimeOffset instant) : TimeProvider { public override DateTimeOffset GetUtcNow() => instant; }
    private sealed class User(Guid id) : ICurrentUser { public Guid UserId => id; }
    private sealed class Store : IReportStore
    {
        public Task<CategoryReportResult> GetByCategoryAsync(Guid userId, CategoryReportQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This double verifies summary dispatch only.");
        public SummaryResult Result { get; init; } = new();
        public int Calls { get; private set; }
        public Guid Owner { get; private set; }
        public SummaryQuery? Query { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public Task<SummaryResult> GetSummaryAsync(Guid userId, SummaryQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Query = query;
            Cancellation = cancellationToken;
            return Task.FromResult(Result);
        }
    }
}
