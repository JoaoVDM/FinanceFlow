namespace FinanceFlow.Application.Statements;

public interface IAccountStatementStore
{
    Task<StatementResult> GetAsync(Guid userId, Guid accountId, StatementQuery query, CancellationToken cancellationToken);
}

public sealed record StatementQuery(DateOnly From, DateOnly To, int Page = 1, int PageSize = 20);

// OperationType distinguishes transaction and transfer IDs even if their GUID values coincide.
// Amount is signed; category applies only to transactions, counterparty only to transfers.
public sealed record StatementMovement(Guid OperationId, string OperationType, string Nature, DateOnly OccurredOn,
    decimal Amount, string? Description, Guid? CategoryId, Guid? CounterpartyAccountId);

public sealed record AccountStatement(Guid AccountId, string Currency, DateOnly From, DateOnly To,
    DateOnly AccountOpeningDate, decimal AccountOpeningBalance, decimal OpeningBalance, decimal AccountOpeningContribution,
    decimal Income, decimal Expense, decimal Result, decimal TransferIn, decimal TransferOut, decimal ClosingBalance,
    IReadOnlyList<StatementMovement> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public enum StatementFailure { InvalidInput, Unauthenticated, NotFound, Unavailable }
public sealed record StatementResult(AccountStatement? Statement = null, StatementFailure? Error = null);
