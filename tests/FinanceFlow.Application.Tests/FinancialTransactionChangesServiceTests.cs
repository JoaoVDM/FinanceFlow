using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Transactions;
using FinanceFlow.Domain.Transactions;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class FinancialTransactionChangesServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 1, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Version = [1, 2, 3, 4, 5, 6, 7, 8];
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static UpdateTransactionCommand Command => new(Guid.NewGuid(), Guid.NewGuid(), "income", 100m, new(2026, 11, 18));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changes_forward_only_authenticated_owner_expected_version_and_injected_clock(bool delete)
    {
        var owner = Guid.NewGuid();
        var store = new Store();
        var service = Service(store, owner);
        var id = Guid.NewGuid();
        var command = Command;
        var result = delete ? await service.DeleteAsync(id, Version, Cancellation) : await service.UpdateAsync(id, command, Version, Cancellation);
        Assert.Null(result.Error);
        Assert.Equal(store.Result.Version, result.Version);
        Assert.Equal(owner, store.Owner);
        Assert.Equal(id, store.Id);
        Assert.Equal(Version, store.Version);
        Assert.NotSame(Version, store.Version);
        Assert.Equal(Now, store.Now);
        Assert.Equal(Cancellation, store.Cancellation);
        if (!delete)
        {
            Assert.Equal(new DateOnly(2026, 11, 18), store.Today);
            Assert.Equal(new TransactionDetails(command.AccountId, command.CategoryId, command.Kind, command.Amount, command.OccurredOn, null), store.Details);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_identity_blocks_all_mutations(bool delete)
    {
        var store = new Store();
        var service = Service(store, Guid.Empty);
        var result = delete ? await service.DeleteAsync(Guid.NewGuid(), Version, Cancellation)
            : await service.UpdateAsync(Guid.NewGuid(), Command, Version, Cancellation);
        Assert.Equal(TransactionFailure.Unauthenticated, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public async Task Invalid_versions_are_rejected_before_storage(int length)
    {
        var store = new Store();
        var service = Service(store, Guid.NewGuid());
        var version = length < 0 ? null : new byte[length];
        Assert.Equal(TransactionFailure.InvalidInput, (await service.UpdateAsync(Guid.NewGuid(), Command, version!, Cancellation)).Error);
        Assert.Equal(TransactionFailure.InvalidInput, (await service.DeleteAsync(Guid.NewGuid(), version!, Cancellation)).Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Invalid_financial_commands_and_financial_future_do_not_reach_storage()
    {
        var store = new Store();
        var service = Service(store, Guid.NewGuid());
        foreach (var command in new[]
        {
            Command with { Amount = 0m }, Command with { Amount = 10.001m }, Command with { Kind = "Income" },
            Command with { AccountId = Guid.Empty }, Command with { CategoryId = Guid.Empty },
            Command with { OccurredOn = new(2026, 11, 19) }, Command with { Description = new string('x', 501) }
        })
        {
            Assert.Equal(TransactionFailure.InvalidInput, (await service.UpdateAsync(Guid.NewGuid(), command, Version, Cancellation)).Error);
        }
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(TransactionFailure.NotFound)]
    [InlineData(TransactionFailure.VersionConflict)]
    [InlineData(TransactionFailure.InactiveReference)]
    [InlineData(TransactionFailure.Unavailable)]
    public async Task Store_failure_is_forwarded_without_retry_or_reinterpretation(TransactionFailure failure)
    {
        var store = new Store { Result = new(Error: failure) };
        var service = Service(store, Guid.NewGuid());
        Assert.Equal(failure, (await service.UpdateAsync(Guid.NewGuid(), Command, Version, Cancellation)).Error);
        Assert.Equal(failure, (await service.DeleteAsync(Guid.NewGuid(), Version, Cancellation)).Error);
        Assert.Equal(2, store.Calls);
    }

    [Fact]
    public async Task Identical_commands_are_still_forwarded_to_the_sql_precondition()
    {
        var store = new Store();
        var service = Service(store, Guid.NewGuid());
        var command = Command;
        var id = Guid.NewGuid();
        await service.UpdateAsync(id, command, Version, Cancellation);
        await service.UpdateAsync(id, command, Version, Cancellation);
        Assert.Equal(2, store.Calls);
    }

    private static FinancialTransactionsService Service(Store store, Guid owner) => new(store, new User(owner), new Clock(), new FinancialCalendar());
    private sealed class User(Guid owner) : ICurrentUser { public Guid UserId => owner; }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store : IFinancialTransactionStore
    {
        public TransactionChangeResult Result { get; init; } = new(Version: "AAAAAAAAAAI=");
        public int Calls { get; private set; }
        public Guid Owner { get; private set; }
        public Guid Id { get; private set; }
        public TransactionDetails? Details { get; private set; }
        public byte[]? Version { get; private set; }
        public DateOnly Today { get; private set; }
        public DateTimeOffset Now { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public Task<TransactionChangeResult> UpdateAsync(Guid userId, Guid id, TransactionDetails details, byte[] expectedVersion,
            DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Details = details;
            Today = today;
            return DeleteAsync(userId, id, expectedVersion, now, cancellationToken);
        }
        public Task<TransactionChangeResult> DeleteAsync(Guid userId, Guid id, byte[] expectedVersion, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Id = id;
            Version = expectedVersion;
            Now = now;
            Cancellation = cancellationToken;
            return Task.FromResult(Result);
        }
        public Task<TransactionCreationResult> CreateAsync(FinancialTransaction transaction, string key, byte[] hash, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialTransactionView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TransactionListResult> ListAsync(Guid userId, TransactionQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AccountBalance?> BalanceAsync(Guid userId, Guid accountId, DateOnly asOf, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
