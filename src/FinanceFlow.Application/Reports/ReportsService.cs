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

        var today = calendar.DateAt(clock.GetUtcNow());
        // DayNumber subtraction also handles DateOnly extremes without AddDays overflow.
        if (query.From > query.To || query.To.DayNumber - query.From.DayNumber >= 366
            || query.From > today || query.To > today || query.AccountId == Guid.Empty)
        {
            return Task.FromResult(new SummaryResult(Error: SummaryFailure.InvalidInput));
        }

        return store.GetSummaryAsync(userId, query, cancellationToken);
    }
}
