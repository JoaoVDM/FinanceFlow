using FinanceFlow.Domain.Accounts;

namespace FinanceFlow.Application.Accounts;

public sealed class FinancialAccountsService(IFinancialAccountStore store, ICurrentUser currentUser,
    TimeProvider clock, FinancialCalendar calendar)
{
    public async Task<AccountCreationResult> CreateAsync(CreateAccountCommand command, string? key, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty) return new(Error: AccountFailure.Unauthenticated);
        if (!AccountCreationFingerprint.IsKeyValid(key)) return new(Error: AccountFailure.InvalidInput);
        FinancialAccount account;
        try
        {
            var now = clock.GetUtcNow();
            account = FinancialAccount.Create(Guid.NewGuid(), userId, command.Name, command.Type, command.Currency,
                command.OpeningBalance, command.OpeningDate, calendar.DateAt(now), now);
        }
        catch (ArgumentException) { return new(Error: AccountFailure.InvalidInput); }
        return await store.CreateAsync(account, key!, AccountCreationFingerprint.Compute(account), cancellationToken);
    }

    public async Task<AccountReadResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty) return new(Error: AccountFailure.Unauthenticated);
        var account = await store.GetAsync(userId, id, cancellationToken);
        return account is null ? new(Error: AccountFailure.NotFound) : new(Account: account);
    }

    public async Task<AccountListResult> ListAsync(AccountQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty) return new(Error: AccountFailure.Unauthenticated);
        if (query.Page < 1 || query.PageSize is < 1 or > 100 || (long)(query.Page - 1) * query.PageSize > int.MaxValue
            || query.Sort is not ("name" or "id") || query.Direction is not ("asc" or "desc")
            || (query.Type is not null && !FinancialAccount.IsTypeValid(query.Type)))
            return new(Error: AccountFailure.InvalidInput);
        return new(Page: await store.ListAsync(userId, query, cancellationToken));
    }
}
