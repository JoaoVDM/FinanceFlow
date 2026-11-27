using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;

namespace FinanceFlow.Application.Reports;

public sealed class ReportsService(IReportStore store, ICurrentUser currentUser, TimeProvider clock, FinancialCalendar calendar)
{
    public Task<SummaryResult> GetSummaryAsync(SummaryQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return Task.FromResult(new SummaryResult(Error: SummaryFailure.Unauthenticated));
        }

        if (!IsValidPeriod(query.From, query.To, query.AccountId))
        {
            return Task.FromResult(new SummaryResult(Error: SummaryFailure.InvalidInput));
        }

        return store.GetSummaryAsync(userId, query, cancellationToken);
    }

    public Task<CategoryReportResult> GetByCategoryAsync(CategoryReportQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return Task.FromResult(new CategoryReportResult(Error: CategoryReportFailure.Unauthenticated));
        }
        if (!IsValidPeriod(query.From, query.To, query.AccountId)
            || (query.Kind is not null && query.Kind is not ("income" or "expense")))
        {
            return Task.FromResult(new CategoryReportResult(Error: CategoryReportFailure.InvalidInput));
        }

        return store.GetByCategoryAsync(userId, query, cancellationToken);
    }

    private bool IsValidPeriod(DateOnly from, DateOnly to, Guid? accountId)
    {
        var today = calendar.DateAt(clock.GetUtcNow());
        // DayNumber subtraction also handles DateOnly extremes without AddDays overflow.
        return from <= to && to.DayNumber - from.DayNumber < 366
            && from <= today && to <= today && accountId != Guid.Empty;
    }
}
