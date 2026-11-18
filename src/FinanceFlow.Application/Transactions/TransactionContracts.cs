using FinanceFlow.Domain.Transactions;

namespace FinanceFlow.Application.Transactions;

public interface IFinancialTransactionStore
{
    Task<TransactionCreationResult> CreateAsync(FinancialTransaction transaction, string key, byte[] hash, CancellationToken cancellationToken);
    Task<FinancialTransactionView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);
    Task<TransactionListResult> ListAsync(Guid userId, TransactionQuery query, CancellationToken cancellationToken);
    Task<AccountBalance?> BalanceAsync(Guid userId, Guid accountId, DateOnly asOf, CancellationToken cancellationToken);
}

public sealed record CreateTransactionCommand(Guid AccountId, Guid CategoryId, string Kind, decimal Amount, DateOnly OccurredOn, string? Description = null);

public sealed record TransactionQuery(int Page = 1, int PageSize = 20, Guid? AccountId = null, Guid? CategoryId = null,
    string? Kind = null, DateOnly? From = null, DateOnly? To = null, decimal? MinAmount = null, decimal? MaxAmount = null,
    string Sort = "occurredOn", string Direction = "desc");

public sealed record FinancialTransactionView(Guid Id, Guid AccountId, Guid CategoryId, string Kind, decimal Amount,
    DateOnly OccurredOn, string? Description, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc, string Version);

public sealed record TransactionPage(IReadOnlyList<FinancialTransactionView> Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record AccountBalance(Guid AccountId, string Currency, DateOnly AsOf, decimal Balance);
public sealed record StoredTransactionCreation(int StatusCode, string Body, string Location, string ETag, string ContentType);
public enum TransactionFailure { InvalidInput, Unauthenticated, NotFound, InactiveReference, IdempotencyConflict, Unavailable }
public sealed record TransactionCreationResult(StoredTransactionCreation? Response = null, TransactionFailure? Error = null);
public sealed record TransactionReadResult(FinancialTransactionView? Transaction = null, TransactionFailure? Error = null);
public sealed record TransactionListResult(TransactionPage? Page = null, TransactionFailure? Error = null);
public sealed record AccountBalanceResult(AccountBalance? Balance = null, TransactionFailure? Error = null);
