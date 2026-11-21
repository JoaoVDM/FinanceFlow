using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Domain.Transfers;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class TransfersServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 19);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static CreateTransferCommand Command => new(Guid.NewGuid(), Guid.NewGuid(), 10m, Today);

    [Fact]
    public async Task Create_and_read_use_only_the_authenticated_owner_clock_and_canonical_fingerprint()
    {
        var owner = Guid.NewGuid();
        var store = new Store();
        var service = Service(store, owner);
        Assert.Null((await service.CreateAsync(Command, "Exact-Key", Cancellation)).Error);
        Assert.Equal(owner, store.Created!.UserId);
        Assert.NotEqual(Guid.Empty, store.Created.Id);
        Assert.Equal(Now, store.Created.CreatedAtUtc);
        Assert.Equal("Exact-Key", store.Key);
        Assert.Equal(TransferCreationFingerprint.Compute(store.Created), store.Hash);
        var id = store.Created.Id;
        Assert.Equal(TransferFailure.NotFound, (await service.GetAsync(id, Cancellation)).Error);
        Assert.Equal(owner, store.Owner);
        Assert.Equal(id, store.Id);
        Assert.Equal(Cancellation, store.Cancellation);
        store.View = new(id, Command.SourceAccountId, Command.DestinationAccountId, 10m, Today, null, Now, null, "AAAAAAAAAAE=");
        Assert.Equal(store.View, (await service.GetAsync(id, Cancellation)).Transfer);
    }

    [Fact]
    public async Task No_identity_blocks_both_operations_before_storage()
    {
        var store = new Store();
        var service = Service(store, Guid.Empty);
        Assert.Equal(TransferFailure.Unauthenticated, (await service.CreateAsync(Command, "key", Cancellation)).Error);
        Assert.Equal(TransferFailure.Unauthenticated, (await service.GetAsync(Guid.NewGuid(), Cancellation)).Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("á")]
    public async Task Invalid_keys_do_not_reach_storage(string? key)
    {
        var store = new Store();
        Assert.Equal(TransferFailure.InvalidInput, (await Service(store).CreateAsync(Command, key, Cancellation)).Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Financial_future_invalid_ids_amounts_and_description_do_not_reach_storage()
    {
        var store = new Store();
        var service = Service(store);
        var command = Command;
        foreach (var invalid in new[] { command with { Amount = 10.001m }, command with { Amount = 0m },
            command with { SourceAccountId = Guid.Empty }, command with { DestinationAccountId = command.SourceAccountId },
            command with { OccurredOn = Today.AddDays(1) }, command with { Description = new string('x', 501) } })
        {
            Assert.Equal(TransferFailure.InvalidInput, (await service.CreateAsync(invalid, "key", Cancellation)).Error);
        }
        Assert.Equal(TransferFailure.InvalidInput, (await service.CreateAsync(command, new string('x', 129), Cancellation)).Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(TransferFailure.InvalidInput)]
    [InlineData(TransferFailure.NotFound)]
    [InlineData(TransferFailure.InactiveAccount)]
    [InlineData(TransferFailure.IdempotencyConflict)]
    [InlineData(TransferFailure.Unavailable)]
    public async Task Store_failures_are_forwarded_without_retry(TransferFailure failure)
    {
        var store = new Store { Result = new(Error: failure) };
        Assert.Equal(failure, (await Service(store).CreateAsync(Command, "key", Cancellation)).Error);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public void Fingerprint_ignores_server_fields_and_decimal_scale_but_preserves_direction_date_and_description()
    {
        var command = Command;
        Transfer Create(decimal amount, DateOnly date, string? description, bool reverse = false) => Transfer.Create(Guid.NewGuid(), Guid.NewGuid(),
            reverse ? command.DestinationAccountId : command.SourceAccountId, reverse ? command.SourceAccountId : command.DestinationAccountId,
            amount, date, description, Today, Now.AddMinutes(1));
        var original = TransferCreationFingerprint.Compute(Create(10m, Today, null));
        Assert.Equal(original, TransferCreationFingerprint.Compute(Create(10.000m, Today, null)));
        Assert.NotEqual(original, TransferCreationFingerprint.Compute(Create(10m, Today, "")));
        Assert.NotEqual(original, TransferCreationFingerprint.Compute(Create(10m, Today, null, true)));
        Assert.NotEqual(original, TransferCreationFingerprint.Compute(Create(10m, Today.AddDays(-1), null)));
    }

    private static TransfersService Service(Store store, Guid? owner = null) => new(store, new User(owner ?? Guid.NewGuid()), new Clock(), new FinancialCalendar());
    private sealed class User(Guid owner) : ICurrentUser { public Guid UserId => owner; }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store : ITransferStore
    {
        public TransferCreationResult Result { get; init; } = new();
        public TransferView? View { get; set; }
        public Transfer? Created { get; private set; }
        public string? Key { get; private set; }
        public byte[]? Hash { get; private set; }
        public Guid Owner { get; private set; }
        public Guid Id { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public int Calls { get; private set; }
        public Task<TransferCreationResult> CreateAsync(Transfer transfer, string key, byte[] hash, CancellationToken cancellationToken)
        {
            Calls++;
            Created = transfer;
            Key = key;
            Hash = hash;
            Cancellation = cancellationToken;
            return Task.FromResult(Result);
        }
        public Task<TransferView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Id = id;
            Cancellation = cancellationToken;
            return Task.FromResult(View);
        }
    }
}
