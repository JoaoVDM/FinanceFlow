using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Statements;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class AccountStatementsServiceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("2024-01-01", "2024-12-31", true)]
    [InlineData("2024-01-01", "2025-01-01", false)]
    [InlineData("2025-01-01", "2025-01-01", true)]
    [InlineData("2025-01-02", "2025-01-01", false)]
    [InlineData("0001-01-01", "0001-01-01", true)]
    [InlineData("0001-01-01", "9999-12-31", false)]
    [InlineData("9999-12-31", "9999-12-31", false)]
    [InlineData("2026-11-21", "2026-11-22", false)]
    public async Task Inclusive_periods_validate_without_overflow(string from, string to, bool valid)
    {
        var store = new Store();
        var service = Service(store);
        var query = new StatementQuery(DateOnly.Parse(from), DateOnly.Parse(to));

        var result = await service.GetAsync(Guid.NewGuid(), query, Cancellation);

        Assert.Equal(valid ? null : StatementFailure.InvalidInput, result.Error);
        Assert.Equal(valid ? 1 : 0, store.Calls);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task Invalid_pagination_never_reaches_storage(int page, int pageSize)
    {
        var store = new Store();

        var result = await Service(store).GetAsync(Guid.NewGuid(), new(new(2000, 1, 1), new(2000, 1, 1), page, pageSize), Cancellation);

        Assert.Equal(StatementFailure.InvalidInput, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Only_authenticated_owner_and_validated_query_are_forwarded()
    {
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        var store = new Store { Result = new(Error: StatementFailure.NotFound) };
        var query = new StatementQuery(new(2000, 1, 1), new(2000, 1, 31), 2, 10);

        var result = await Service(store, owner).GetAsync(account, query, Cancellation);

        Assert.Same(store.Result, result);
        Assert.Equal(owner, store.Owner);
        Assert.Equal(account, store.Account);
        Assert.Equal(query, store.Query);
        Assert.Equal(Cancellation, store.Cancellation);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task Missing_identity_never_reads_financial_storage()
    {
        var store = new Store();

        var result = await Service(store, Guid.Empty).GetAsync(Guid.NewGuid(), new(new(2000, 1, 1), new(2000, 1, 1)), Cancellation);

        Assert.Equal(StatementFailure.Unauthenticated, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("2026-11-22T02:59:59Z", false)]
    [InlineData("2026-11-22T03:00:00Z", true)]
    public async Task Today_is_the_injected_Sao_Paulo_day_not_the_UTC_day(string instant, bool accepted)
    {
        var store = new Store();
        var service = new AccountStatementsService(store, new User(Guid.NewGuid()), new Clock(DateTimeOffset.Parse(instant)), new());

        var result = await service.GetAsync(Guid.NewGuid(), new(new(2026, 11, 22), new(2026, 11, 22)), Cancellation);

        Assert.Equal(accepted ? null : StatementFailure.InvalidInput, result.Error);
        Assert.Equal(accepted ? 1 : 0, store.Calls);
    }

    private static AccountStatementsService Service(Store store, Guid? owner = null) =>
        new(store, new User(owner ?? Guid.NewGuid()), new Clock(new(2026, 11, 22, 2, 0, 0, TimeSpan.Zero)), new());

    private sealed class User(Guid id) : ICurrentUser { public Guid UserId => id; }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Store : IAccountStatementStore
    {
        public StatementResult Result { get; init; } = new();
        public int Calls { get; private set; }
        public Guid Owner { get; private set; }
        public Guid Account { get; private set; }
        public StatementQuery? Query { get; private set; }
        public CancellationToken Cancellation { get; private set; }

        public Task<StatementResult> GetAsync(Guid userId, Guid accountId, StatementQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Account = accountId;
            Query = query;
            Cancellation = cancellationToken;
            return Task.FromResult(Result);
        }
    }
}
