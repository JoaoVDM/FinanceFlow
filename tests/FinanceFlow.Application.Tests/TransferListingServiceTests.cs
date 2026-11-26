using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Domain.Transfers;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class TransferListingServiceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 20, "occurredOn", "desc")]
    [InlineData(1, 0, "occurredOn", "desc")]
    [InlineData(1, 101, "occurredOn", "desc")]
    [InlineData(int.MaxValue, 100, "occurredOn", "desc")]
    [InlineData(1, 20, "description", "desc")]
    [InlineData(1, 20, "amount", "DESC")]
    [InlineData(1, 20, "id", "up")]
    public async Task Invalid_page_or_sort_does_not_read_storage(int page, int size, string sort, string direction)
    {
        var store = new Store();

        var result = await Service(store).ListAsync(new(page, size, Sort: sort, Direction: direction), Cancellation);

        Assert.Equal(TransferFailure.InvalidInput, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("0001-01-01", "2026-11-21", true)]
    [InlineData(null, "2026-11-21", true)]
    [InlineData("2026-11-21", null, true)]
    [InlineData("2026-11-22", null, false)]
    [InlineData(null, "2026-11-22", false)]
    [InlineData("2001-01-01", "2000-01-01", false)]
    public async Task Optional_dates_reject_future_and_reversal_without_a_statement_range_limit(string? from, string? to, bool valid)
    {
        var store = new Store();
        var query = new TransferQuery(From: from is null ? null : DateOnly.Parse(from), To: to is null ? null : DateOnly.Parse(to));

        var result = await Service(store).ListAsync(query, Cancellation);

        Assert.Equal(valid ? null : TransferFailure.InvalidInput, result.Error);
        Assert.Equal(valid ? 1 : 0, store.Calls);
    }

    [Fact]
    public async Task Empty_account_reference_is_invalid()
    {
        var store = new Store();

        var result = await Service(store).ListAsync(new(AccountId: Guid.Empty), Cancellation);

        Assert.Equal(TransferFailure.InvalidInput, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Missing_owner_prevents_reads()
    {
        var store = new Store();

        var result = await Service(store, Guid.Empty).ListAsync(new(), Cancellation);

        Assert.Equal(TransferFailure.Unauthenticated, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_query_and_store_result_are_preserved_without_retry(bool missing)
    {
        var owner = Guid.NewGuid();
        var store = new Store { Result = missing ? new(Error: TransferFailure.NotFound) : new(Page: new([], 2, 10, 0, 0)) };
        var query = new TransferQuery(2, 10, Guid.NewGuid(), new(2000, 1, 1), new(2001, 1, 1), "amount", "asc");

        var result = await Service(store, owner).ListAsync(query, Cancellation);

        Assert.Same(store.Result, result);
        Assert.Equal(owner, store.Owner);
        Assert.Equal(query, store.Query);
        Assert.Equal(Cancellation, store.Cancellation);
        Assert.Equal(1, store.Calls);
    }

    private static TransfersService Service(Store store, Guid? owner = null) =>
        new(store, new User(owner ?? Guid.NewGuid()), new Clock(), new FinancialCalendar());
    private sealed class User(Guid id) : ICurrentUser { public Guid UserId => id; }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 22, 2, 0, 0, TimeSpan.Zero);
    }
    private sealed class Store : ITransferStore
    {
        public TransferListResult Result { get; init; } = new();
        public int Calls { get; private set; }
        public Guid Owner { get; private set; }
        public TransferQuery? Query { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public Task<TransferListResult> ListAsync(Guid userId, TransferQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Query = query;
            Cancellation = cancellationToken;
            return Task.FromResult(Result);
        }
        public Task<TransferCreationResult> CreateAsync(Transfer transfer, string key, byte[] hash, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TransferView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TransferCancellationResult> CancelAsync(Guid userId, Guid id, byte[] expectedVersion, DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
