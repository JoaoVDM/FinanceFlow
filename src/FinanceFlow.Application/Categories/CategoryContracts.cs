using FinanceFlow.Domain.Categories;

namespace FinanceFlow.Application.Categories;

public interface ICategoryStore
{
    Task<CategoryResult> CreateAsync(Category category, CancellationToken cancellationToken);

    Task<CategoryView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task<CategoryPage> ListAsync(Guid userId, CategoryQuery query, CancellationToken cancellationToken);
}

public sealed record CreateCategoryCommand(string Name, string Kind);

public sealed record CategoryQuery(
    int Page = 1,
    int PageSize = 20,
    string? Kind = null,
    bool? IsActive = null,
    string Sort = "name",
    string Direction = "asc");

public sealed record CategoryView(
    Guid Id,
    string Name,
    string Kind,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    string Version);

public sealed record CategoryPage(
    IReadOnlyList<CategoryView> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public enum CategoryFailure
{
    InvalidInput,
    Unauthenticated,
    NotFound,
    Duplicate,
    Unavailable
}

public sealed record CategoryResult(CategoryView? Category = null, CategoryFailure? Error = null);

public sealed record CategoryListResult(CategoryPage? Page = null, CategoryFailure? Error = null);
