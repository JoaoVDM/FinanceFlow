using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Categories;
using FinanceFlow.Domain.Common;

namespace FinanceFlow.Domain.Transactions;

public enum TransactionLinkStatus { Valid, Invalid, Inactive }

public sealed class FinancialTransaction
{
    private FinancialTransaction() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid CategoryId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public DateOnly OccurredOn { get; private set; }
    public string? Description { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static bool IsKindValid(string? kind) => kind is "income" or "expense";

    public static FinancialTransaction Create(Guid id, Guid userId, Guid accountId, Guid categoryId,
        string kind, decimal amount, DateOnly occurredOn, string? description, DateOnly today, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || userId == Guid.Empty || accountId == Guid.Empty || categoryId == Guid.Empty)
        {
            throw new ArgumentException("Transaction, owner, account and category identifiers are required.");
        }

        if (!IsKindValid(kind) || !MonetaryLimits.IsPositiveAmount(amount))
        {
            throw new ArgumentException("A valid kind and positive amount with exact cents are required.");
        }

        if (occurredOn > today || description is { Length: > 500 })
        {
            throw new ArgumentException("Invalid financial date or description length.");
        }

        return new FinancialTransaction
        {
            Id = id,
            UserId = userId,
            AccountId = accountId,
            CategoryId = categoryId,
            Kind = kind,
            Amount = amount,
            OccurredOn = occurredOn,
            Description = description,
            CreatedAtUtc = createdAt.ToUniversalTime()
        };
    }

    // Persistence must call this while holding the reference reads until commit (ADR-007).
    public TransactionLinkStatus CheckLinks(FinancialAccount account, Category category)
    {
        if (account.Id != AccountId || category.Id != CategoryId || account.UserId != UserId
            || category.UserId != UserId || category.Kind != Kind || OccurredOn < account.OpeningDate)
        {
            return TransactionLinkStatus.Invalid;
        }

        return account.IsActive && category.IsActive ? TransactionLinkStatus.Valid : TransactionLinkStatus.Inactive;
    }
}
