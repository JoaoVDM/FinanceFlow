using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Categories;
using FinanceFlow.Application.Common;
using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Categories;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class FinancialChangesServiceTests
{
    private static readonly DateTimeOffset Instant = new(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);
    private static readonly byte[] ExpectedVersion = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly string NewVersion = Convert.ToBase64String([2, 2, 3, 4, 5, 6, 7, 8]);
    private static CancellationToken TestCancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Account_edit_uses_owner_clock_domain_rules_and_expected_version()
    {
        var owner = Guid.NewGuid();
        var store = new AccountStore(owner);
        var service = Accounts(store, owner);

        var result = await service.UpdateAsync(store.Loaded!.Id, new(" Cafe\u0301 ", "cash"), ExpectedVersion, TestCancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(NewVersion, result.Version);
        Assert.Equal(owner, store.RequestedOwner);
        Assert.Equal(store.Loaded.Id, store.RequestedId);
        Assert.Equal(ExpectedVersion, store.SavedVersion);
        Assert.Equal("Café", store.Loaded.Name);
        Assert.Equal("cash", store.Loaded.Type);
        Assert.Equal(Instant, store.Loaded.UpdatedAtUtc);
        Assert.Equal(TestCancellationToken, store.Cancellation);
    }

    [Fact]
    public async Task Category_rename_uses_owner_clock_normalization_and_expected_version()
    {
        var owner = Guid.NewGuid();
        var store = new CategoryStore(owner);
        var service = Categories(store, owner);

        var result = await service.RenameAsync(store.Loaded!.Id, new(" cafe\u0301 "), ExpectedVersion, TestCancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(NewVersion, result.Version);
        Assert.Equal(owner, store.RequestedOwner);
        Assert.Equal(store.Loaded.Id, store.RequestedId);
        Assert.Equal(ExpectedVersion, store.SavedVersion);
        Assert.Equal("café", store.Loaded.Name);
        Assert.Equal("CAFÉ", store.Loaded.NormalizedName);
        Assert.Equal(Instant, store.Loaded.UpdatedAtUtc);
        Assert.Equal(TestCancellationToken, store.Cancellation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activity_changes_and_repeated_values_are_forwarded(bool category)
    {
        var owner = Guid.NewGuid();
        if (category)
        {
            var store = new CategoryStore(owner);
            var service = Categories(store, owner);

            var deactivated = await service.SetActivityAsync(store.Loaded!.Id, false, ExpectedVersion, TestCancellationToken);
            Assert.Null(deactivated.Error);
            Assert.False(store.Loaded.IsActive);
            var repeated = await service.SetActivityAsync(store.Loaded.Id, false, ExpectedVersion, TestCancellationToken);

            Assert.Null(repeated.Error);
            Assert.Equal(2, store.Saves);
            Assert.Equal(ExpectedVersion, store.SavedVersion);
        }
        else
        {
            var store = new AccountStore(owner);
            var service = Accounts(store, owner);

            var deactivated = await service.SetActivityAsync(store.Loaded!.Id, false, ExpectedVersion, TestCancellationToken);
            Assert.Null(deactivated.Error);
            Assert.False(store.Loaded.IsActive);
            var repeated = await service.SetActivityAsync(store.Loaded.Id, false, ExpectedVersion, TestCancellationToken);

            Assert.Null(repeated.Error);
            Assert.Equal(2, store.Saves);
            Assert.Equal(ExpectedVersion, store.SavedVersion);
        }
    }

    [Fact]
    public async Task Missing_identity_blocks_both_commands_for_both_resources()
    {
        var accounts = new AccountStore(Guid.NewGuid());
        var categories = new CategoryStore(Guid.NewGuid());

        var accountEdit = await Accounts(accounts, Guid.Empty).UpdateAsync(Guid.NewGuid(), new("Name", "cash"), ExpectedVersion, TestCancellationToken);
        var accountActivity = await Accounts(accounts, Guid.Empty).SetActivityAsync(Guid.NewGuid(), false, ExpectedVersion, TestCancellationToken);
        var categoryEdit = await Categories(categories, Guid.Empty).RenameAsync(Guid.NewGuid(), new("Name"), ExpectedVersion, TestCancellationToken);
        var categoryActivity = await Categories(categories, Guid.Empty).SetActivityAsync(Guid.NewGuid(), false, ExpectedVersion, TestCancellationToken);

        Assert.Equal(AccountFailure.Unauthenticated, accountEdit.Error);
        Assert.Equal(AccountFailure.Unauthenticated, accountActivity.Error);
        Assert.Equal(CategoryFailure.Unauthenticated, categoryEdit.Error);
        Assert.Equal(CategoryFailure.Unauthenticated, categoryActivity.Error);
        Assert.Equal(0, accounts.Loads + accounts.Saves + categories.Loads + categories.Saves);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(9)]
    public async Task Invalid_version_is_rejected_before_loading_either_resource(int length)
    {
        var owner = Guid.NewGuid();
        var accounts = new AccountStore(owner);
        var categories = new CategoryStore(owner);
        var version = length < 0 ? null : new byte[length];

        var accountEdit = await Accounts(accounts, owner).UpdateAsync(Guid.NewGuid(), new("Name", "cash"), version!, TestCancellationToken);
        var accountActivity = await Accounts(accounts, owner).SetActivityAsync(Guid.NewGuid(), false, version!, TestCancellationToken);
        var categoryEdit = await Categories(categories, owner).RenameAsync(Guid.NewGuid(), new("Name"), version!, TestCancellationToken);
        var categoryActivity = await Categories(categories, owner).SetActivityAsync(Guid.NewGuid(), false, version!, TestCancellationToken);

        Assert.Equal(AccountFailure.InvalidInput, accountEdit.Error);
        Assert.Equal(AccountFailure.InvalidInput, accountActivity.Error);
        Assert.Equal(CategoryFailure.InvalidInput, categoryEdit.Error);
        Assert.Equal(CategoryFailure.InvalidInput, categoryActivity.Error);
        Assert.Equal(0, accounts.Loads + accounts.Saves + categories.Loads + categories.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_foreign_entity_is_not_saved(bool foreign)
    {
        var owner = Guid.NewGuid();
        var accounts = new AccountStore(Guid.NewGuid());
        var categories = new CategoryStore(Guid.NewGuid());
        if (!foreign)
        {
            accounts.Loaded = null;
            categories.Loaded = null;
        }

        var accountEdit = await Accounts(accounts, owner).UpdateAsync(Guid.NewGuid(), new("Name", "cash"), ExpectedVersion, TestCancellationToken);
        var categoryEdit = await Categories(categories, owner).RenameAsync(Guid.NewGuid(), new("Name"), ExpectedVersion, TestCancellationToken);

        Assert.Equal(AccountFailure.NotFound, accountEdit.Error);
        Assert.Equal(CategoryFailure.NotFound, categoryEdit.Error);
        Assert.Equal(owner, accounts.RequestedOwner);
        Assert.Equal(owner, categories.RequestedOwner);
        Assert.Equal(0, accounts.Saves + categories.Saves);
    }

    [Fact]
    public async Task Invalid_names_and_types_do_not_reach_save()
    {
        var owner = Guid.NewGuid();
        var accounts = new AccountStore(owner);
        var categories = new CategoryStore(owner);
        var badName = await Accounts(accounts, owner).UpdateAsync(accounts.Loaded!.Id, new(" ", "cash"), ExpectedVersion, TestCancellationToken);
        var badType = await Accounts(accounts, owner).UpdateAsync(accounts.Loaded.Id, new("Name", "Cash"), ExpectedVersion, TestCancellationToken);
        var badCategory = await Categories(categories, owner).RenameAsync(categories.Loaded!.Id, new(new string('x', 101)), ExpectedVersion, TestCancellationToken);

        Assert.Equal(AccountFailure.InvalidInput, badName.Error);
        Assert.Equal(AccountFailure.InvalidInput, badType.Error);
        Assert.Equal(CategoryFailure.InvalidInput, badCategory.Error);
        Assert.Equal(0, accounts.Saves + categories.Saves);
    }

    [Theory]
    [InlineData(AccountFailure.VersionConflict)]
    [InlineData(AccountFailure.NotFound)]
    [InlineData(AccountFailure.Unavailable)]
    public async Task Account_save_outcome_is_not_retried_or_reinterpreted(AccountFailure failure)
    {
        var owner = Guid.NewGuid();
        var store = new AccountStore(owner) { Result = new(Error: failure) };

        var result = await Accounts(store, owner).SetActivityAsync(store.Loaded!.Id, false, ExpectedVersion, TestCancellationToken);

        Assert.Equal(failure, result.Error);
        Assert.Equal(1, store.Saves);
    }

    [Theory]
    [InlineData(CategoryFailure.VersionConflict)]
    [InlineData(CategoryFailure.Duplicate)]
    [InlineData(CategoryFailure.NotFound)]
    [InlineData(CategoryFailure.Unavailable)]
    public async Task Category_save_outcome_is_not_retried_or_reinterpreted(CategoryFailure failure)
    {
        var owner = Guid.NewGuid();
        var store = new CategoryStore(owner) { Result = new(Error: failure) };

        var result = await Categories(store, owner).RenameAsync(store.Loaded!.Id, new("Name"), ExpectedVersion, TestCancellationToken);

        Assert.Equal(failure, result.Error);
        Assert.Equal(1, store.Saves);
    }

    private static FinancialAccountsService Accounts(AccountStore store, Guid owner) =>
        new(store, new User(owner), new Clock(), new FinancialCalendar());

    private static CategoriesService Categories(CategoryStore store, Guid owner) => new(store, new User(owner), new Clock());

    private sealed class User(Guid owner) : ICurrentUser
    {
        public Guid UserId => owner;
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Instant;
    }

    private sealed class AccountStore(Guid owner) : IFinancialAccountStore
    {
        public FinancialAccount? Loaded { get; set; } = FinancialAccount.Create(Guid.NewGuid(), owner, "Original", "cash", "BRL", -10m,
            new DateOnly(2020, 1, 1), new DateOnly(2026, 11, 14), Instant.AddDays(-1));
        public Guid RequestedOwner { get; private set; }
        public Guid RequestedId { get; private set; }
        public byte[]? SavedVersion { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public int Loads { get; private set; }
        public int Saves { get; private set; }
        public AccountChangeResult Result { get; init; } = new(Version: NewVersion);

        public Task<FinancialAccount?> FindForUpdateAsync(Guid userId, Guid id, CancellationToken cancellationToken)
        {
            Loads++;
            RequestedOwner = userId;
            RequestedId = id;
            return Task.FromResult(Loaded);
        }

        public Task<AccountChangeResult> SaveUpdateAsync(FinancialAccount account, byte[] expectedVersion, CancellationToken cancellationToken)
        {
            Saves++;
            SavedVersion = expectedVersion.ToArray();
            Cancellation = cancellationToken;
            return Task.FromResult(Result);
        }

        public Task<AccountCreationResult> CreateAsync(FinancialAccount account, string key, byte[] requestHash, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialAccountView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AccountPage> ListAsync(Guid userId, AccountQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CategoryStore(Guid owner) : ICategoryStore
    {
        public Category? Loaded { get; set; } = Category.Create(Guid.NewGuid(), owner, "Original", "expense", Instant.AddDays(-1));
        public Guid RequestedOwner { get; private set; }
        public Guid RequestedId { get; private set; }
        public byte[]? SavedVersion { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public int Loads { get; private set; }
        public int Saves { get; private set; }
        public CategoryChangeResult Result { get; init; } = new(Version: NewVersion);

        public Task<Category?> FindForUpdateAsync(Guid userId, Guid id, CancellationToken cancellationToken)
        {
            Loads++;
            RequestedOwner = userId;
            RequestedId = id;
            return Task.FromResult(Loaded);
        }

        public Task<CategoryChangeResult> SaveUpdateAsync(Category category, byte[] expectedVersion, CancellationToken cancellationToken)
        {
            Saves++;
            SavedVersion = expectedVersion.ToArray();
            Cancellation = cancellationToken;
            return Task.FromResult(Result);
        }

        public Task<CategoryResult> CreateAsync(Category category, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CategoryView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CategoryPage> ListAsync(Guid userId, CategoryQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
