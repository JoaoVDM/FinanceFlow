using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Common;

namespace FinanceFlow.Domain.Transfers;

public enum TransferAccountStatus { Valid, Invalid, Inactive }

public sealed class Transfer
{
    private Transfer() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid SourceAccountId { get; private set; }
    public Guid DestinationAccountId { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly OccurredOn { get; private set; }
    public string? Description { get; private set; }
    public bool IsCanceled { get; private set; }
    public DateTimeOffset? CanceledAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Transfer Create(Guid id, Guid userId, Guid sourceAccountId, Guid destinationAccountId,
        decimal amount, DateOnly occurredOn, string? description, DateOnly today, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || userId == Guid.Empty || sourceAccountId == Guid.Empty || destinationAccountId == Guid.Empty
            || sourceAccountId == destinationAccountId)
        {
            throw new ArgumentException("Transfer, owner and distinct account identifiers are required.");
        }
        if (!MonetaryLimits.IsPositiveAmount(amount) || occurredOn > today || description is { Length: > 500 })
        {
            throw new ArgumentException("A positive exact-cent amount, valid date and description are required.");
        }

        return new Transfer
        {
            Id = id,
            UserId = userId,
            SourceAccountId = sourceAccountId,
            DestinationAccountId = destinationAccountId,
            Amount = amount,
            OccurredOn = occurredOn,
            Description = description,
            CreatedAtUtc = createdAt.ToUniversalTime()
        };
    }

    public void Cancel(DateTimeOffset canceledAt)
    {
        if (IsCanceled)
        {
            throw new InvalidOperationException("Canceled transfers cannot be canceled again.");
        }

        IsCanceled = true;
        CanceledAtUtc = canceledAt.ToUniversalTime();
        UpdatedAtUtc = CanceledAtUtc;
    }

    // Check inside the same transaction that retains the account reads until commit (ADR-007).
    public TransferAccountStatus CheckAccounts(FinancialAccount source, FinancialAccount destination)
    {
        if (source.Id != SourceAccountId || destination.Id != DestinationAccountId
            || source.UserId != UserId || destination.UserId != UserId
            || source.Currency != "BRL" || destination.Currency != "BRL"
            || OccurredOn < source.OpeningDate || OccurredOn < destination.OpeningDate)
        {
            return TransferAccountStatus.Invalid;
        }
        return source.IsActive && destination.IsActive ? TransferAccountStatus.Valid : TransferAccountStatus.Inactive;
    }
}
