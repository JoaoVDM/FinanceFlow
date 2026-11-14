using FinanceFlow.Domain.Accounts;

namespace FinanceFlow.Application.Accounts;

public interface ICurrentUser { Guid UserId { get; } }

public interface IFinancialAccountStore
{
    Task<AccountCreationResult> CreateAsync(FinancialAccount account, string key, byte[] requestHash, CancellationToken cancellationToken);
    Task<FinancialAccountView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);
    Task<AccountPage> ListAsync(Guid userId, AccountQuery query, CancellationToken cancellationToken);
}

public sealed record CreateAccountCommand(string Name, string Type, string Currency, decimal OpeningBalance, DateOnly OpeningDate);
public sealed record AccountQuery(int Page = 1, int PageSize = 20, bool? IsActive = null, string? Type = null,
    string Sort = "name", string Direction = "asc");
public sealed record FinancialAccountView(Guid Id, string Name, string Type, string Currency, decimal OpeningBalance,
    DateOnly OpeningDate, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc, string Version);
public sealed record AccountPage(IReadOnlyList<FinancialAccountView> Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record StoredAccountCreation(int StatusCode, string Body, string Location, string ETag, string ContentType);
public enum AccountFailure { InvalidInput, Unauthenticated, NotFound, IdempotencyConflict, Unavailable }
public sealed record AccountCreationResult(StoredAccountCreation? Response = null, AccountFailure? Error = null);
public sealed record AccountReadResult(FinancialAccountView? Account = null, AccountFailure? Error = null);
public sealed record AccountListResult(AccountPage? Page = null, AccountFailure? Error = null);
