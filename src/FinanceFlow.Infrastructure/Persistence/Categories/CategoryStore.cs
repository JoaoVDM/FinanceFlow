using System.Linq.Expressions;
using FinanceFlow.Application.Categories;
using FinanceFlow.Domain.Categories;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Categories;

public sealed class CategoryStore(FinanceFlowDbContext db) : ICategoryStore
{
    private static readonly Expression<Func<Category, CategoryView>> Projection = category => new(
        category.Id,
        category.Name,
        category.Kind,
        category.IsActive,
        category.CreatedAtUtc,
        category.UpdatedAtUtc,
        Convert.ToBase64String(category.RowVersion));

    public async Task<CategoryResult> CreateAsync(Category category, CancellationToken cancellationToken)
    {
        db.Categories.Add(category);
        try
        {
            // The unique SQL index decides races; there is no check-then-insert gap.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateName(exception))
        {
            db.Entry(category).State = EntityState.Detached;
            return new(Error: CategoryFailure.Duplicate);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 1205 or -2 })
        {
            return new(Error: CategoryFailure.Unavailable);
        }

        return new(Category: new(
            category.Id,
            category.Name,
            category.Kind,
            category.IsActive,
            category.CreatedAtUtc,
            category.UpdatedAtUtc,
            Convert.ToBase64String(category.RowVersion)));
    }

    public Task<CategoryView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        db.Categories.AsNoTracking()
            .Where(category => category.UserId == userId && category.Id == id)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<CategoryPage> ListAsync(Guid userId, CategoryQuery query, CancellationToken cancellationToken)
    {
        var categories = db.Categories.AsNoTracking().Where(category => category.UserId == userId);
        if (query.Kind is { } kind)
        {
            categories = categories.Where(category => category.Kind == kind);
        }

        if (query.IsActive is { } isActive)
        {
            categories = categories.Where(category => category.IsActive == isActive);
        }

        var totalCount = await categories.CountAsync(cancellationToken);
        var orderedCategories = (query.Sort, query.Direction) switch
        {
            ("id", "asc") => categories.OrderBy(category => category.Id),
            ("id", "desc") => categories.OrderByDescending(category => category.Id),
            ("name", "desc") => categories.OrderByDescending(category => category.Name).ThenBy(category => category.Id),
            _ => categories.OrderBy(category => category.Name).ThenBy(category => category.Id)
        };
        var items = await orderedCategories
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(Projection)
            .ToListAsync(cancellationToken);

        var totalPages = (int)Math.Ceiling((decimal)totalCount / query.PageSize);
        return new(items, query.Page, query.PageSize, totalCount, totalPages);
    }

    private static bool IsDuplicateName(DbUpdateException exception) =>
        exception.Entries.Count == 1
        && exception.Entries[0].Entity is Category
        && exception.InnerException is SqlException sql
        && sql.Errors.Cast<SqlError>().Any(error => error.Number == 2601
            && error.Message.StartsWith(
                "Cannot insert duplicate key row in object 'dbo.Categories' with unique index '"
                    + CategoryConfiguration.UniqueNameIndex + "'.",
                StringComparison.Ordinal));
}
