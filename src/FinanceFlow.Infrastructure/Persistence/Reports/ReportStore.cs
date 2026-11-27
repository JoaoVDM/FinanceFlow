using FinanceFlow.Application.Reports;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Reports;

public sealed class ReportStore(FinanceFlowDbContext db) : IReportStore
{
    public async Task<CategoryReportResult> GetByCategoryAsync(Guid userId, CategoryReportQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await SnapshotReadSession.RunAsync(db, () => ReadByCategoryAsync(userId, query, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (SnapshotReadFailure.IsUnavailable(exception))
        {
            return new(Error: CategoryReportFailure.Unavailable);
        }
    }

    private async Task<CategoryReportResult> ReadByCategoryAsync(Guid userId, CategoryReportQuery query, CancellationToken cancellationToken)
    {
        if (query.AccountId is { } accountId && !await db.FinancialAccounts.AsNoTracking()
            .AnyAsync(account => account.UserId == userId && account.Id == accountId, cancellationToken))
        {
            return new(Error: CategoryReportFailure.NotFound);
        }

        var transactions = db.FinancialTransactions.AsNoTracking()
            .Where(value => value.UserId == userId && !value.IsDeleted
                && value.OccurredOn >= query.From && value.OccurredOn <= query.To);
        if (query.AccountId is { } account)
        {
            transactions = transactions.Where(value => value.AccountId == account);
        }
        if (query.Kind is { } kind)
        {
            transactions = transactions.Where(value => value.Kind == kind);
        }

        // Category identity prevents equal names from merging. The join reads its current
        // name and explicitly preserves both ownership and the compatible financial kind.
        var groups = await (from transaction in transactions
            join category in db.Categories.AsNoTracking().Where(value => value.UserId == userId)
                on new { Id = transaction.CategoryId, transaction.UserId, transaction.Kind }
                equals new { category.Id, category.UserId, category.Kind }
            group transaction by new { CategoryId = category.Id, category.Name, category.Kind } into categoryGroup
            select new { categoryGroup.Key.CategoryId, categoryGroup.Key.Name, categoryGroup.Key.Kind,
                Total = categoryGroup.Sum(value => value.Amount) })
            .OrderBy(value => value.Kind).ThenByDescending(value => value.Total).ThenBy(value => value.CategoryId)
            .Select(value => new CategoryReportGroup(value.CategoryId, value.Name, value.Kind, value.Total))
            .ToListAsync(cancellationToken);
        return new(Groups: groups);
    }

    public async Task<SummaryResult> GetSummaryAsync(Guid userId, SummaryQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await SnapshotReadSession.RunAsync(db, () => ReadSummaryAsync(userId, query, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (SnapshotReadFailure.IsUnavailable(exception))
        {
            return new(Error: SummaryFailure.Unavailable);
        }
    }

    private async Task<SummaryResult> ReadSummaryAsync(Guid userId, SummaryQuery query, CancellationToken cancellationToken)
    {
        if (query.AccountId is { } accountId && !await db.FinancialAccounts.AsNoTracking()
            .AnyAsync(account => account.UserId == userId && account.Id == accountId, cancellationToken))
        {
            return new(Error: SummaryFailure.NotFound);
        }

        var transactions = db.FinancialTransactions.AsNoTracking()
            .Where(value => value.UserId == userId && !value.IsDeleted
                && value.OccurredOn >= query.From && value.OccurredOn <= query.To);
        if (query.AccountId is { } account)
        {
            transactions = transactions.Where(value => value.AccountId == account);
        }

        // Both conditional sums are one SQL aggregate. Keep each SUM as decimal(38,2)
        // and subtract in checked C# decimal, avoiding scale reduction between SQL sums.
        var totals = await transactions.GroupBy(value => 1).Select(group => new
        {
            Income = group.Sum(value => value.Kind == "income" ? value.Amount : 0m),
            Expense = group.Sum(value => value.Kind == "expense" ? value.Amount : 0m)
        }).SingleOrDefaultAsync(cancellationToken);
        var income = totals?.Income ?? 0m;
        var expense = totals?.Expense ?? 0m;
        return new(Summary: new("BRL", query.From, query.To, income, expense, checked(income - expense)));
    }
}
