using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Domain.Transfers;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class TransferCancellationServiceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 5, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cancellation_forwards_only_authenticated_owner_copied_version_and_injected_clock()
    {
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var version = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var store = new Store { Result = new(Version: "AAAAAAAAAAI=") };

        var result = await Service(store, owner).CancelAsync(id, version, Cancellation);

        Assert.Equal(store.Result, result);
        Assert.Equal(owner, store.Owner);
        Assert.Equal(id, store.Id);
        Assert.Equal(version, store.Version);
        Assert.NotSame(version, store.Version);
        Assert.Equal(Now, store.Now);
        Assert.Equal(Cancellation, store.Cancellation);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task Missing_identity_prevents_storage_access()
    {
        var store = new Store();

        var result = await Service(store, Guid.Empty).CancelAsync(Guid.NewGuid(), new byte[8], Cancellation);

        Assert.Equal(TransferFailure.Unauthenticated, result.Error);
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
        var version = length < 0 ? null! : new byte[length];

        var result = await Service(store).CancelAsync(Guid.NewGuid(), version, Cancellation);

        Assert.Equal(TransferFailure.InvalidInput, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(TransferFailure.NotFound)]
    [InlineData(TransferFailure.VersionConflict)]
    [InlineData(TransferFailure.Unavailable)]
    [InlineData(TransferFailure.InvalidInput)]
    public async Task Store_failures_are_returned_without_retry(TransferFailure failure)
    {
        var store = new Store { Result = new(Error: failure) };

        var result = await Service(store).CancelAsync(Guid.NewGuid(), new byte[8], Cancellation);

        Assert.Equal(failure, result.Error);
        Assert.Equal(1, store.Calls);
    }

    private static TransfersService Service(Store store, Guid? owner = null) =>
        new(store, new User(owner ?? Guid.NewGuid()), new Clock(), new FinancialCalendar());

    private sealed class User(Guid owner) : ICurrentUser { public Guid UserId => owner; }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class Store : ITransferStore
    {
        public TransferCancellationResult Result { get; init; } = new();
        public Guid Owner { get; private set; }
        public Guid Id { get; private set; }
        public byte[]? Version { get; private set; }
        public DateTimeOffset Now { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public int Calls { get; private set; }

        public Task<TransferCancellationResult> CancelAsync(Guid userId, Guid id, byte[] expectedVersion,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Id = id;
            Version = expectedVersion;
            Now = now;
            Cancellation = cancellationToken;
            return Task.FromResult(Result);
        }

        public Task<TransferCreationResult> CreateAsync(Transfer transfer, string key, byte[] hash,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TransferView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
