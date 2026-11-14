using FinanceFlow.Application.Categories;
using FinanceFlow.Application.Common;
using FinanceFlow.Domain.Categories;
using Xunit;

namespace FinanceFlow.Application.Tests;

public sealed class CategoriesServiceTests
{
    private static readonly DateTimeOffset Instant = new(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);
    private static CancellationToken TestCancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creation_uses_only_authenticated_owner_clock_and_domain_normalization()
    {
        var owner = Guid.NewGuid();
        var store = new RecordingStore();
        var service = CreateService(store, owner);

        await service.CreateAsync(new(" cafe\u0301 ", "expense"), TestCancellationToken);

        var category = Assert.Single(store.Created);
        Assert.NotEqual(Guid.Empty, category.Id);
        Assert.Equal(owner, category.UserId);
        Assert.Equal(Instant, category.CreatedAtUtc);
        Assert.Equal("café", category.Name);
        Assert.Equal("CAFÉ", category.NormalizedName);
    }

    [Fact]
    public async Task Missing_user_prevents_all_persistence_calls()
    {
        var store = new RecordingStore();
        var service = CreateService(store, Guid.Empty);

        var creation = await service.CreateAsync(new("Category", "income"), TestCancellationToken);
        var lookup = await service.GetAsync(Guid.NewGuid(), TestCancellationToken);
        var listing = await service.ListAsync(new(), TestCancellationToken);

        Assert.Equal(CategoryFailure.Unauthenticated, creation.Error);
        Assert.Equal(CategoryFailure.Unauthenticated, lookup.Error);
        Assert.Equal(CategoryFailure.Unauthenticated, listing.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("", "income")]
    [InlineData(" \t ", "expense")]
    [InlineData("Category", "Income")]
    [InlineData("Category", "expense ")]
    public async Task Invalid_command_does_not_reach_store(string name, string kind)
    {
        var store = new RecordingStore();
        var service = CreateService(store, Guid.NewGuid());

        var result = await service.CreateAsync(new(name, kind), TestCancellationToken);

        Assert.Equal(CategoryFailure.InvalidInput, result.Error);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(CategoryFailure.Duplicate)]
    [InlineData(CategoryFailure.Unavailable)]
    public async Task Known_store_failures_are_preserved(CategoryFailure failure)
    {
        var store = new RecordingStore { CreationResult = new(Error: failure) };
        var service = CreateService(store, Guid.NewGuid());

        var result = await service.CreateAsync(new("Category", "expense"), TestCancellationToken);

        Assert.Equal(failure, result.Error);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task Unexpected_failure_is_not_reinterpreted_as_duplicate()
    {
        var store = new RecordingStore { CreationException = new InvalidOperationException("test failure") };
        var service = CreateService(store, Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new("Category", "income"), TestCancellationToken));
    }

    [Fact]
    public async Task Individual_lookup_passes_owner_and_requested_id_and_preserves_view()
    {
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var view = new CategoryView(id, "Category", "expense", true, Instant, null, "version");
        var store = new RecordingStore { Found = view };
        var service = CreateService(store, owner);

        var result = await service.GetAsync(id, TestCancellationToken);

        Assert.Same(view, result.Category);
        Assert.Equal(owner, store.Owner);
        Assert.Equal(id, store.RequestedId);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Missing_category_returns_generic_not_found()
    {
        var store = new RecordingStore();
        var service = CreateService(store, Guid.NewGuid());

        var result = await service.GetAsync(Guid.NewGuid(), TestCancellationToken);

        Assert.Equal(CategoryFailure.NotFound, result.Error);
        Assert.Null(result.Category);
    }

    [Fact]
    public async Task Listing_passes_owner_and_filters_without_default_activity_filter()
    {
        var owner = Guid.NewGuid();
        var store = new RecordingStore();
        var service = CreateService(store, owner);

        await service.ListAsync(new(), TestCancellationToken);

        Assert.Equal(owner, store.Owner);
        Assert.Null(store.Query!.IsActive);
        Assert.Equal(20, store.Query.PageSize);

        var filtered = new CategoryQuery(Page: 2, PageSize: 100, Kind: "income", IsActive: false, Sort: "id", Direction: "desc");
        var result = await service.ListAsync(filtered, TestCancellationToken);

        Assert.Same(filtered, store.Query);
        Assert.NotNull(result.Page);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Invalid_queries_never_reach_persistence()
    {
        var store = new RecordingStore();
        var service = CreateService(store, Guid.NewGuid());
        var invalidQueries = new[]
        {
            new CategoryQuery(Page: 0),
            new(PageSize: 0),
            new(PageSize: 101),
            new(Page: int.MaxValue),
            new(Kind: "Income"),
            new(Sort: "normalizedName"),
            new(Direction: "ASC")
        };

        foreach (var query in invalidQueries)
        {
            var result = await service.ListAsync(query, TestCancellationToken);

            Assert.Equal(CategoryFailure.InvalidInput, result.Error);
        }

        Assert.Equal(0, store.Calls);
    }

    private static CategoriesService CreateService(RecordingStore store, Guid owner) =>
        new(store, new CurrentUser(owner), new FixedClock());

    private sealed class CurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId => userId;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Instant;
    }

    private sealed class RecordingStore : ICategoryStore
    {
        public List<Category> Created { get; } = [];
        public int Calls { get; private set; }
        public Guid Owner { get; private set; }
        public Guid RequestedId { get; private set; }
        public CategoryQuery? Query { get; private set; }
        public CategoryView? Found { get; init; }
        public CategoryResult CreationResult { get; init; } = new();
        public Exception? CreationException { get; init; }

        public Task<CategoryResult> CreateAsync(Category category, CancellationToken cancellationToken)
        {
            Calls++;
            Created.Add(category);
            if (CreationException is not null)
            {
                throw CreationException;
            }

            return Task.FromResult(CreationResult);
        }

        public Task<CategoryView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            RequestedId = id;
            return Task.FromResult(Found);
        }

        public Task<CategoryPage> ListAsync(Guid userId, CategoryQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Owner = userId;
            Query = query;
            return Task.FromResult(new CategoryPage([], query.Page, query.PageSize, 0, 0));
        }
    }
}
