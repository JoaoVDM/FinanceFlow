using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Transactions;
using FinanceFlow.Domain.Transactions;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class FinancialTransactionsServiceTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 1, 0, 0, TimeSpan.Zero); // Still November 17 in Sao Paulo.
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static CreateTransactionCommand Command => new(Guid.NewGuid(), Guid.NewGuid(), "income", 10m, new DateOnly(2026, 11, 17));

    [Fact]
    public async Task Creation_uses_authenticated_owner_financial_day_clock_and_fingerprint()
    {
        var store = new Store();
        var command = Command;

        var result = await Service(store).CreateAsync(command, "Exact-Key", Cancellation);

        Assert.Null(result.Error);
        Assert.Equal(Owner, store.Created!.UserId);
        Assert.Equal(Now, store.Created.CreatedAtUtc);
        Assert.Equal("Exact-Key", store.Key);
        Assert.Equal(TransactionCreationFingerprint.Compute(store.Created), store.Hash);
        Assert.Equal(Cancellation, store.Cancellation);
        var future = await Service(store).CreateAsync(command with { OccurredOn = new(2026, 11, 18) }, "key", Cancellation);
        Assert.Equal(TransactionFailure.InvalidInput, future.Error);
        Assert.Equal(1, store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("contains space")]
    [InlineData("á")]
    public async Task Invalid_keys_never_reach_the_store(string? key)
    {
        var store = new Store();
        Assert.Equal(TransactionFailure.InvalidInput, (await Service(store).CreateAsync(Command, key, Cancellation)).Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Missing_identity_blocks_every_operation()
    {
        var store = new Store();
        var service = Service(store, Guid.Empty);
        Assert.Equal(TransactionFailure.Unauthenticated, (await service.CreateAsync(Command, "key", Cancellation)).Error);
        Assert.Equal(TransactionFailure.Unauthenticated, (await service.GetAsync(Guid.NewGuid(), Cancellation)).Error);
        Assert.Equal(TransactionFailure.Unauthenticated, (await service.ListAsync(new(), Cancellation)).Error);
        Assert.Equal(TransactionFailure.Unauthenticated, (await service.BalanceAsync(Guid.NewGuid(), null, Cancellation)).Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Invalid_commands_and_queries_are_rejected_before_persistence()
    {
        var store = new Store();
        var service = Service(store);
        foreach (var command in new[] { Command with { Amount = 0m }, Command with { Amount = 10.001m }, Command with { AccountId = Guid.Empty }, Command with { Description = new string('x', 501) } })
        {
            Assert.Equal(TransactionFailure.InvalidInput, (await service.CreateAsync(command, "key", Cancellation)).Error);
        }
        foreach (var query in new TransactionQuery[]
        {
            new(Page: 0), new(PageSize: 101), new(Page: int.MaxValue), new(Sort: "unknown"), new(Direction: "DESC"), new(Kind: "Income"),
            new(From: new(2026, 11, 18)), new(From: new(2026, 11, 17), To: new(2026, 11, 16)),
            new(MinAmount: 10.001m), new(MinAmount: 2m, MaxAmount: 1m), new(AccountId: Guid.Empty), new(CategoryId: Guid.Empty)
        })
        {
            Assert.Equal(TransactionFailure.InvalidInput, (await service.ListAsync(query, Cancellation)).Error);
        }
        Assert.Equal(TransactionFailure.InvalidInput, (await service.BalanceAsync(Guid.NewGuid(), new(2026, 11, 18), Cancellation)).Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Reads_and_balances_use_owner_and_default_financial_cutoff()
    {
        var store = new Store();
        var service = Service(store);
        Assert.Equal(TransactionFailure.NotFound, (await service.GetAsync(Guid.NewGuid(), Cancellation)).Error);
        Assert.Equal(TransactionFailure.NotFound, (await service.BalanceAsync(Guid.NewGuid(), null, Cancellation)).Error);
        Assert.Equal(Owner, store.Owner);
        Assert.Equal(new DateOnly(2026, 11, 17), store.Cutoff);
        var query = new TransactionQuery(From: new(2000, 1, 1), To: new(2026, 11, 17));
        Assert.Null((await service.ListAsync(query, Cancellation)).Error);
        Assert.Equal(query, store.Query);
    }

    [Theory]
    [InlineData(TransactionFailure.NotFound)]
    [InlineData(TransactionFailure.InactiveReference)]
    [InlineData(TransactionFailure.IdempotencyConflict)]
    [InlineData(TransactionFailure.Unavailable)]
    public async Task Store_outcomes_are_not_retried_or_reinterpreted(TransactionFailure failure)
    {
        var store = new Store { Failure = failure };
        Assert.Equal(failure, (await Service(store).CreateAsync(Command, "key", Cancellation)).Error);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public void Canonical_hash_ignores_ids_timestamps_and_decimal_scale_but_preserves_command_content()
    {
        var command = Command;
        FinancialTransaction Transaction(decimal amount, string? description) => FinancialTransaction.Create(Guid.NewGuid(), Owner,
            command.AccountId, command.CategoryId, command.Kind, amount, command.OccurredOn, description, command.OccurredOn, Now);
        Assert.Equal(TransactionCreationFingerprint.Compute(Transaction(10m, null)), TransactionCreationFingerprint.Compute(Transaction(10.000m, null)));
        Assert.NotEqual(TransactionCreationFingerprint.Compute(Transaction(10m, null)), TransactionCreationFingerprint.Compute(Transaction(10m, "")));
        Assert.NotEqual(TransactionCreationFingerprint.Compute(Transaction(10m, null)), TransactionCreationFingerprint.Compute(Transaction(11m, null)));
    }

    private static FinancialTransactionsService Service(Store store, Guid? owner = null) => new(store, new User(owner ?? Owner), new Clock(), new FinancialCalendar());
    private sealed class User(Guid owner) : ICurrentUser { public Guid UserId => owner; }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store : IFinancialTransactionStore
    {
        public Task<TransactionChangeResult> UpdateAsync(Guid userId, Guid id, TransactionDetails details, byte[] expectedVersion,
            DateOnly today, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TransactionChangeResult> DeleteAsync(Guid userId, Guid id, byte[] expectedVersion,
            DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public int Calls { get; private set; }
        public FinancialTransaction? Created { get; private set; }
        public string? Key { get; private set; }
        public byte[]? Hash { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public TransactionFailure? Failure { get; init; }
        public Guid Owner { get; private set; }
        public DateOnly Cutoff { get; private set; }
        public TransactionQuery? Query { get; private set; }

        public Task<TransactionCreationResult> CreateAsync(FinancialTransaction transaction, string key, byte[] hash, CancellationToken cancellationToken)
        {
            Calls++;
            Created = transaction;
            Key = key;
            Hash = hash;
            Cancellation = cancellationToken;
            return Task.FromResult(new TransactionCreationResult(Error: Failure));
        }
        public Task<FinancialTransactionView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            return Task.FromResult<FinancialTransactionView?>(null);
        }
        public Task<TransactionListResult> ListAsync(Guid userId, TransactionQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Query = query;
            return Task.FromResult(new TransactionListResult(Page: new([], query.Page, query.PageSize, 0, 0)));
        }
        public Task<AccountBalance?> BalanceAsync(Guid userId, Guid accountId, DateOnly asOf, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Cutoff = asOf;
            return Task.FromResult<AccountBalance?>(null);
        }
    }
}
