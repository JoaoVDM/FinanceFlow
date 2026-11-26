using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;

namespace FinanceFlow.Application.Statements;

public sealed class AccountStatementsService(IAccountStatementStore store, ICurrentUser currentUser,
    TimeProvider clock, FinancialCalendar calendar)
{
    public Task<StatementResult> GetAsync(Guid accountId, StatementQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return Task.FromResult(new StatementResult(Error: StatementFailure.Unauthenticated));
        }

        var today = calendar.DateAt(clock.GetUtcNow());
        // DayNumber subtraction is safe at both DateOnly extremes; no AddDays(-1) is needed.
        if (query.From > query.To || query.To.DayNumber - query.From.DayNumber >= 366
            || query.From > today || query.To > today || query.Page < 1 || query.PageSize is < 1 or > 100
            || (long)(query.Page - 1) * query.PageSize > int.MaxValue)
        {
            return Task.FromResult(new StatementResult(Error: StatementFailure.InvalidInput));
        }

        return store.GetAsync(userId, accountId, query, cancellationToken);
    }
}
