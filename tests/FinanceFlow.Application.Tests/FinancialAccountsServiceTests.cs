using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Domain.Accounts;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class FinancialAccountsServiceTests
{
    private static readonly DateTimeOffset Instant = new(2026, 11, 14, 1, 0, 0, TimeSpan.Zero);
    private static CreateAccountCommand Command => new(" Account ", "checking", "BRL", 10.000m, new DateOnly(2026, 11, 13));

    [Fact]
    public async Task Owner_and_creation_instant_come_from_trusted_context()
    {
        var store = new RecordingStore();
        var owner = Guid.NewGuid();
        var service = Service(store, owner);

        await service.CreateAsync(Command, "Key", TestContext.Current.CancellationToken);

        var account = Assert.Single(store.Created);
        Assert.Equal(owner, account.UserId);
        Assert.Equal(Instant, account.CreatedAtUtc);
        Assert.Equal("Account", account.Name);
        Assert.Equal(32, Assert.Single(store.Hashes).Length);
        Assert.Equal("Key", store.Key);
    }

    [Fact]
    public async Task Missing_authenticated_owner_never_reaches_persistence()
    {
        var store = new RecordingStore();
        var service = Service(store, Guid.Empty);

        var creation = await service.CreateAsync(Command, "k", TestContext.Current.CancellationToken);
        Assert.Equal(AccountFailure.Unauthenticated, creation.Error);

        var lookup = await service.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(AccountFailure.Unauthenticated, lookup.Error);

        var listing = await service.ListAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal(AccountFailure.Unauthenticated, listing.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" key")]
    [InlineData("key ")]
    [InlineData("has space")]
    [InlineData("a\tb")]
    [InlineData("café")]
    public async Task Invalid_keys_are_rejected_without_persistence(string? key)
    {
        var store = new RecordingStore();
        var service = Service(store, Guid.NewGuid());

        var result = await service.CreateAsync(Command, key, TestContext.Current.CancellationToken);

        Assert.Equal(AccountFailure.InvalidInput, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void Key_boundaries_and_visible_ascii_preserve_case()
    {
        Assert.True(AccountCreationFingerprint.IsKeyValid("!~,/\"\\"));
        Assert.True(AccountCreationFingerprint.IsKeyValid(new string('a', 128)));
        Assert.False(AccountCreationFingerprint.IsKeyValid(new string('a', 129)));
    }

    [Fact]
    public async Task Future_date_uses_sao_paulo_not_the_utc_date()
    {
        var store = new RecordingStore();

        var result = await Service(store, Guid.NewGuid()).CreateAsync(
            Command with { OpeningDate = new(2026, 11, 14) },
            "k",
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountFailure.InvalidInput, result.Error);
        Assert.Equal(0, store.Calls);
        Assert.Equal(new DateOnly(2026, 11, 13), new FinancialCalendar().DateAt(Instant));
        Assert.Equal(new DateOnly(2026, 11, 14), new FinancialCalendar().DateAt(Instant.AddHours(2)));
    }

    [Fact]
    public async Task Invalid_domain_command_is_rejected_before_store()
    {
        var store = new RecordingStore();
        foreach (var command in new[]
        {
            Command with { OpeningBalance = 10.001m },
            Command with { Currency = "USD" },
            Command with { Type = "Checking" },
            Command with { Name = " " }
        })
        {
            var result = await Service(store, Guid.NewGuid()).CreateAsync(command, "k", TestContext.Current.CancellationToken);

            Assert.Equal(AccountFailure.InvalidInput, result.Error);
        }

        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Canonical_hash_ignores_ids_clock_and_equivalent_text_or_decimal_representation()
    {
        var store = new RecordingStore();
        var service = Service(store, Guid.NewGuid());

        await service.CreateAsync(Command with { Name = " Café " }, "A", TestContext.Current.CancellationToken);
        await service.CreateAsync(
            Command with { Name = "Cafe\u0301", OpeningBalance = 10.00m },
            "A",
            TestContext.Current.CancellationToken);

        Assert.Equal(store.Hashes[0], store.Hashes[1]);

        await service.CreateAsync(Command with { Name = "Other" }, "A", TestContext.Current.CancellationToken);
        Assert.False(store.Hashes[0].SequenceEqual(store.Hashes[2]));
    }

    [Fact]
    public async Task Store_conflicts_and_failure_results_are_preserved()
    {
        var store = new RecordingStore
        {
            Outcome = new(Error: AccountFailure.IdempotencyConflict)
        };

        var conflict = await Service(store, Guid.NewGuid()).CreateAsync(Command, "k", TestContext.Current.CancellationToken);

        Assert.Equal(AccountFailure.IdempotencyConflict, conflict.Error);

        store.Outcome = new(Error: AccountFailure.Unavailable);

        var unavailable = await Service(store, Guid.NewGuid()).CreateAsync(Command, "k", TestContext.Current.CancellationToken);

        Assert.Equal(AccountFailure.Unavailable, unavailable.Error);
    }

    [Fact]
    public async Task Reads_and_counts_are_scoped_to_current_owner()
    {
        var store = new RecordingStore();
        var owner = Guid.NewGuid();
        var service = Service(store, owner);

        var missing = await service.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(AccountFailure.NotFound, missing.Error);

        await service.ListAsync(new(IsActive: false, Type: "cash"), TestContext.Current.CancellationToken);

        Assert.Equal(owner, store.Owner);
        Assert.False(store.Query!.IsActive);
        Assert.Equal("cash", store.Query.Type);
    }

    [Fact]
    public async Task Invalid_paging_filters_and_sort_never_reach_store()
    {
        var store = new RecordingStore();
        var service = Service(store, Guid.NewGuid());
        foreach (var query in new[]
        {
            new AccountQuery(Page: 0),
            new(PageSize: 101),
            new(PageSize: 0),
            new(Page: int.MaxValue),
            new(Type: "creditCard"),
            new(Sort: "openingBalance"),
            new(Direction: "sideways")
        })
        {
            var result = await service.ListAsync(query, TestContext.Current.CancellationToken);

            Assert.Equal(AccountFailure.InvalidInput, result.Error);
        }

        Assert.Equal(0, store.Calls);
    }

    private static FinancialAccountsService Service(RecordingStore store, Guid owner) => new(
        store,
        new User(owner),
        new Clock(),
        new FinancialCalendar());

    private sealed class User(Guid id) : ICurrentUser
    {
        public Guid UserId => id;
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Instant;
    }

    private sealed class RecordingStore : IFinancialAccountStore
    {
        public Task<FinancialAccount?> FindForUpdateAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<FinancialAccount?>(null);

        public Task<AccountChangeResult> SaveUpdateAsync(FinancialAccount account, byte[] expectedVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture covers creation and reads only.");

        public List<FinancialAccount> Created { get; } = [];
        public List<byte[]> Hashes { get; } = [];
        public int Calls { get; private set; }
        public string? Key { get; private set; }
        public Guid Owner { get; private set; }
        public AccountQuery? Query { get; private set; }
        public AccountCreationResult Outcome { get; set; } = new();

        public Task<AccountCreationResult> CreateAsync(
            FinancialAccount account,
            string key,
            byte[] requestHash,
            CancellationToken cancellationToken)
        {
            Calls++;
            Created.Add(account);
            Hashes.Add(requestHash);
            Key = key;
            return Task.FromResult(Outcome);
        }

        public Task<FinancialAccountView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            return Task.FromResult<FinancialAccountView?>(null);
        }

        public Task<AccountPage> ListAsync(Guid userId, AccountQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Query = query;
            return Task.FromResult(new AccountPage([], query.Page, query.PageSize, 0, 0));
        }
    }
}
