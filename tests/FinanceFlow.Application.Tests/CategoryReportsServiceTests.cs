using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Reports;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class CategoryReportsServiceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("2024-01-01", "2024-12-31", true)]
    [InlineData("2024-01-01", "2025-01-01", false)]
    [InlineData("2026-11-26", "2026-11-26", true)]
    [InlineData("2026-11-26", "2026-11-01", false)]
    [InlineData("2026-11-26", "2026-11-27", false)]
    [InlineData("0001-01-01", "0001-01-01", true)]
    [InlineData("0001-01-01", "9999-12-31", false)]
    public async Task Inclusive_periods_are_validated_before_storage(string from, string to, bool valid)
    {
        var store = new Store();
        var result = await Service(store).GetByCategoryAsync(new(DateOnly.Parse(from), DateOnly.Parse(to)), Cancellation);
        Assert.Equal(valid ? null : CategoryReportFailure.InvalidInput, result.Error);
        Assert.Equal(valid ? 1 : 0, store.Calls);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("income", true)]
    [InlineData("expense", true)]
    [InlineData("Income", false)]
    [InlineData("EXPENSE", false)]
    [InlineData("expense ", false)]
    [InlineData("", false)]
    [InlineData("transfer", false)]
    public async Task Kind_is_optional_but_never_normalized_or_inferred(string? kind, bool valid)
    {
        var store = new Store();
        var result = await Service(store).GetByCategoryAsync(new(new(2026, 11, 1), new(2026, 11, 26), Kind: kind), Cancellation);
        Assert.Equal(valid ? null : CategoryReportFailure.InvalidInput, result.Error);
        Assert.Equal(valid ? 1 : 0, store.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_and_nonempty_account_are_required(bool missingOwner)
    {
        var store = new Store();
        var result = await Service(store, missingOwner ? Guid.Empty : Guid.NewGuid())
            .GetByCategoryAsync(new(new(2026, 11, 1), new(2026, 11, 26), Guid.Empty), Cancellation);
        Assert.Equal(missingOwner ? CategoryReportFailure.Unauthenticated : CategoryReportFailure.InvalidInput, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "expense")]
    public async Task Valid_filters_owner_token_and_groups_are_forwarded_unchanged(bool account, string? kind)
    {
        var owner = Guid.NewGuid();
        var store = new Store { Result = new(Groups: [new(Guid.NewGuid(), "History", "expense", 1.01m)]) };
        var query = new CategoryReportQuery(new(2026, 11, 1), new(2026, 11, 26), account ? Guid.NewGuid() : null, kind);
        var result = await Service(store, owner).GetByCategoryAsync(query, Cancellation);
        Assert.Same(store.Result, result);
        Assert.Equal(owner, store.Owner);
        Assert.Equal(query, store.Query);
        Assert.Equal(Cancellation, store.Token);
        Assert.Equal(1, store.Calls);
    }

    [Theory]
    [InlineData(CategoryReportFailure.NotFound)]
    [InlineData(CategoryReportFailure.Unavailable)]
    public async Task Store_failures_are_preserved_without_retry(CategoryReportFailure failure)
    {
        var store = new Store { Result = new(Error: failure) };
        var result = await Service(store).GetByCategoryAsync(new(new(2026, 11, 1), new(2026, 11, 26)), Cancellation);
        Assert.Same(store.Result, result);
        Assert.Equal(1, store.Calls);
    }

    [Theory]
    [InlineData("2026-11-27T02:59:59Z", false)]
    [InlineData("2026-11-27T03:00:00Z", true)]
    public async Task Future_uses_the_injected_financial_day(string instant, bool valid)
    {
        var store = new Store();
        var service = new ReportsService(store, new User(Guid.NewGuid()), new Clock(DateTimeOffset.Parse(instant)), new());
        var result = await service.GetByCategoryAsync(new(new(2026, 11, 27), new(2026, 11, 27)), Cancellation);
        Assert.Equal(valid ? null : CategoryReportFailure.InvalidInput, result.Error);
        Assert.Equal(valid ? 1 : 0, store.Calls);
    }

    private static ReportsService Service(Store store, Guid? owner = null) =>
        new(store, new User(owner ?? Guid.NewGuid()), new Clock(new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero)), new());
    private sealed class User(Guid id) : ICurrentUser { public Guid UserId => id; }
    private sealed class Clock(DateTimeOffset instant) : TimeProvider { public override DateTimeOffset GetUtcNow() => instant; }
    private sealed class Store : IReportStore
    {
        public CategoryReportResult Result { get; init; } = new(Groups: []);
        public Guid Owner { get; private set; }
        public CategoryReportQuery? Query { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }
        public Task<CategoryReportResult> GetByCategoryAsync(Guid userId, CategoryReportQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Query = query;
            Token = cancellationToken;
            return Task.FromResult(Result);
        }
        public Task<SummaryResult> GetSummaryAsync(Guid userId, SummaryQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Category reports must use their own read contract.");
    }
}
