using FinanceFlow.Domain.Transfers;

namespace FinanceFlow.Application.Transfers;

public interface ITransferStore
{
    Task<TransferCreationResult> CreateAsync(Transfer transfer, string key, byte[] hash, CancellationToken cancellationToken);
    Task<TransferView?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);
}

public sealed record CreateTransferCommand(Guid SourceAccountId, Guid DestinationAccountId, decimal Amount,
    DateOnly OccurredOn, string? Description = null);

public sealed record TransferView(Guid Id, Guid SourceAccountId, Guid DestinationAccountId, decimal Amount,
    DateOnly OccurredOn, string? Description, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc, string Version);

public sealed record StoredTransferCreation(int StatusCode, string Body, string Location, string ETag, string ContentType);
public enum TransferFailure { InvalidInput, Unauthenticated, NotFound, InactiveAccount, IdempotencyConflict, Unavailable }
public sealed record TransferCreationResult(StoredTransferCreation? Response = null, TransferFailure? Error = null);
public sealed record TransferReadResult(TransferView? Transfer = null, TransferFailure? Error = null);
