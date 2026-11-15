using FinanceFlow.Application.Common;
using FinanceFlow.Domain.Categories;

namespace FinanceFlow.Application.Categories;

public sealed class CategoriesService(ICategoryStore store, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<CategoryResult> CreateAsync(CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return new(Error: CategoryFailure.Unauthenticated);
        }

        Category category;
        try
        {
            category = Category.Create(Guid.NewGuid(), userId, command.Name, command.Kind, clock.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return new(Error: CategoryFailure.InvalidInput);
        }

        return await store.CreateAsync(category, cancellationToken);
    }

    public async Task<CategoryResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return new(Error: CategoryFailure.Unauthenticated);
        }

        var category = await store.GetAsync(userId, id, cancellationToken);
        return category is null ? new(Error: CategoryFailure.NotFound) : new(Category: category);
    }

    public async Task<CategoryListResult> ListAsync(CategoryQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return new(Error: CategoryFailure.Unauthenticated);
        }

        if (query.Page < 1
            || query.PageSize is < 1 or > 100
            || (long)(query.Page - 1) * query.PageSize > int.MaxValue
            || query.Sort is not ("name" or "id")
            || query.Direction is not ("asc" or "desc")
            || (query.Kind is not null && !Category.IsKindValid(query.Kind)))
        {
            return new(Error: CategoryFailure.InvalidInput);
        }

        return new(Page: await store.ListAsync(userId, query, cancellationToken));
    }

    public Task<CategoryChangeResult> RenameAsync(
        Guid id, RenameCategoryCommand command, byte[] expectedVersion, CancellationToken cancellationToken) =>
        ChangeAsync(id, expectedVersion, (category, now) => category.Rename(command.Name, now), cancellationToken);

    public Task<CategoryChangeResult> SetActivityAsync(
        Guid id, bool isActive, byte[] expectedVersion, CancellationToken cancellationToken) =>
        ChangeAsync(id, expectedVersion, (category, now) => category.SetActivity(isActive, now), cancellationToken);

    private async Task<CategoryChangeResult> ChangeAsync(
        Guid id,
        byte[] expectedVersion,
        Action<Category, DateTimeOffset> change,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return new(Error: CategoryFailure.Unauthenticated);
        }

        if (expectedVersion is not { Length: 8 })
        {
            return new(Error: CategoryFailure.InvalidInput);
        }

        var category = await store.FindForUpdateAsync(userId, id, cancellationToken);
        if (category is null || category.UserId != userId)
        {
            return new(Error: CategoryFailure.NotFound);
        }

        try
        {
            change(category, clock.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return new(Error: CategoryFailure.InvalidInput);
        }

        return await store.SaveUpdateAsync(category, expectedVersion, cancellationToken);
    }
}
