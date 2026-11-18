using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Domain.Common;
using FinanceFlow.Domain.Transactions;

namespace FinanceFlow.Application.Transactions;

public sealed class FinancialTransactionsService(IFinancialTransactionStore store, ICurrentUser currentUser,
    TimeProvider clock, FinancialCalendar calendar)
{
    public async Task<TransactionCreationResult> CreateAsync(CreateTransactionCommand command, string? key, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return new(Error: TransactionFailure.Unauthenticated);
        }

        if (!IdempotencyKey.IsValid(key))
        {
            return new(Error: TransactionFailure.InvalidInput);
        }

        FinancialTransaction transaction;
        try
        {
            var now = clock.GetUtcNow();
            transaction = FinancialTransaction.Create(Guid.NewGuid(), userId, command.AccountId, command.CategoryId,
                command.Kind, command.Amount, command.OccurredOn, command.Description, calendar.DateAt(now), now);
        }
        catch (ArgumentException)
        {
            return new(Error: TransactionFailure.InvalidInput);
        }

        return await store.CreateAsync(transaction, key!, TransactionCreationFingerprint.Compute(transaction), cancellationToken);
    }

    public async Task<TransactionReadResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return new(Error: TransactionFailure.Unauthenticated);
        }

        var transaction = await store.GetAsync(userId, id, cancellationToken);
        return transaction is null ? new(Error: TransactionFailure.NotFound) : new(Transaction: transaction);
    }

    public Task<TransactionListResult> ListAsync(TransactionQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return Task.FromResult(new TransactionListResult(Error: TransactionFailure.Unauthenticated));
        }

        var today = calendar.DateAt(clock.GetUtcNow());
        if (query.Page < 1 || query.PageSize is < 1 or > 100 || (long)(query.Page - 1) * query.PageSize > int.MaxValue
            || query.Sort is not ("occurredOn" or "amount" or "id") || query.Direction is not ("asc" or "desc")
            || (query.Kind is not null && !FinancialTransaction.IsKindValid(query.Kind))
            || query.AccountId == Guid.Empty || query.CategoryId == Guid.Empty
            || query.From > today || query.To > today || query.From > query.To
            || (query.MinAmount is { } min && !IsAmountFilterValid(min))
            || (query.MaxAmount is { } max && !IsAmountFilterValid(max)) || query.MinAmount > query.MaxAmount)
        {
            return Task.FromResult(new TransactionListResult(Error: TransactionFailure.InvalidInput));
        }

        return store.ListAsync(userId, query, cancellationToken);
    }

    public async Task<AccountBalanceResult> BalanceAsync(Guid id, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return new(Error: TransactionFailure.Unauthenticated);
        }

        var today = calendar.DateAt(clock.GetUtcNow());
        var cutoff = asOf ?? today;
        if (cutoff > today)
        {
            return new(Error: TransactionFailure.InvalidInput);
        }

        var balance = await store.BalanceAsync(userId, id, cutoff, cancellationToken);
        return balance is null ? new(Error: TransactionFailure.NotFound) : new(Balance: balance);
    }

    private static bool IsAmountFilterValid(decimal amount) =>
        amount >= 0m && amount <= MonetaryLimits.MaximumAmount && amount % 0.01m == 0m;
}
