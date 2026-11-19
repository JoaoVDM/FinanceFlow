using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Categories;
using FinanceFlow.Domain.Common;

namespace FinanceFlow.Domain.Transactions;

public enum TransactionLinkStatus { Valid, Invalid, Inactive }

public sealed record TransactionDetails(Guid AccountId, Guid CategoryId, string Kind, decimal Amount,
    DateOnly OccurredOn, string? Description);

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

        ValidateDetails(new(accountId, categoryId, kind, amount, occurredOn, description), today);

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

    public static void ValidateDetails(TransactionDetails details, DateOnly today)
    {
        if (details.AccountId == Guid.Empty || details.CategoryId == Guid.Empty)
        {
            throw new ArgumentException("Account and category identifiers are required.");
        }
        if (!IsKindValid(details.Kind) || !MonetaryLimits.IsPositiveAmount(details.Amount))
        {
            throw new ArgumentException("A valid kind and positive amount with exact cents are required.");
        }

        if (details.OccurredOn > today || details.Description is { Length: > 500 })
        {
            throw new ArgumentException("Invalid financial date or description length.");
        }

    }

    public TransactionLinkStatus UpdateDetails(TransactionDetails details, FinancialAccount originalAccount,
        FinancialAccount targetAccount, Category targetCategory, DateOnly today, DateTimeOffset updatedAt)
    {
        if (IsDeleted)
        {
            throw new InvalidOperationException("Deleted transactions cannot be edited.");
        }
        ValidateDetails(details, today);
        if (originalAccount.Id != AccountId || originalAccount.UserId != UserId
            || targetAccount.Id != details.AccountId || targetAccount.UserId != UserId
            || targetCategory.Id != details.CategoryId || targetCategory.UserId != UserId
            || targetCategory.Kind != details.Kind || details.OccurredOn < targetAccount.OpeningDate)
        {
            return TransactionLinkStatus.Invalid;
        }

        var financialChange = AccountId != details.AccountId || CategoryId != details.CategoryId
            || Kind != details.Kind || Amount != details.Amount || OccurredOn != details.OccurredOn;
        // An inactive source account permits only descriptive correction, even when moving to an active account.
        if (financialChange && (!originalAccount.IsActive || !targetAccount.IsActive))
        {
            return TransactionLinkStatus.Inactive;
        }
        // A historical category may stay inactive; only reassociation requires an active category.
        if (CategoryId != details.CategoryId && !targetCategory.IsActive)
        {
            return TransactionLinkStatus.Inactive;
        }

        AccountId = details.AccountId;
        CategoryId = details.CategoryId;
        Kind = details.Kind;
        Amount = details.Amount;
        OccurredOn = details.OccurredOn;
        Description = details.Description;
        UpdatedAtUtc = updatedAt.ToUniversalTime();
        return TransactionLinkStatus.Valid;
    }

    public void Delete(DateTimeOffset deletedAt)
    {
        if (IsDeleted)
        {
            throw new InvalidOperationException("Deleted transactions cannot be deleted again.");
        }
        IsDeleted = true;
        DeletedAtUtc = deletedAt.ToUniversalTime();
        UpdatedAtUtc = DeletedAtUtc;
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
