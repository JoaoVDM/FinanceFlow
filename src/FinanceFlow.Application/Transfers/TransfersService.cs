using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Domain.Transfers;

namespace FinanceFlow.Application.Transfers;

public sealed class TransfersService(ITransferStore store, ICurrentUser currentUser, TimeProvider clock, FinancialCalendar calendar)
{
    public async Task<TransferCreationResult> CreateAsync(CreateTransferCommand command, string? key, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return new(Error: TransferFailure.Unauthenticated);
        }
        if (!IdempotencyKey.IsValid(key))
        {
            return new(Error: TransferFailure.InvalidInput);
        }

        Transfer transfer;
        try
        {
            var now = clock.GetUtcNow();
            transfer = Transfer.Create(Guid.NewGuid(), userId, command.SourceAccountId, command.DestinationAccountId,
                command.Amount, command.OccurredOn, command.Description, calendar.DateAt(now), now);
        }
        catch (ArgumentException)
        {
            return new(Error: TransferFailure.InvalidInput);
        }
        return await store.CreateAsync(transfer, key!, TransferCreationFingerprint.Compute(transfer), cancellationToken);
    }

    public Task<TransferCancellationResult> CancelAsync(Guid id, byte[] expectedVersion, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return Task.FromResult(new TransferCancellationResult(Error: TransferFailure.Unauthenticated));
        }
        if (expectedVersion is not { Length: 8 })
        {
            return Task.FromResult(new TransferCancellationResult(Error: TransferFailure.InvalidInput));
        }

        return store.CancelAsync(userId, id, expectedVersion.ToArray(), clock.GetUtcNow(), cancellationToken);
    }

    public async Task<TransferReadResult> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return new(Error: TransferFailure.Unauthenticated);
        }
        var transfer = await store.GetAsync(userId, id, cancellationToken);
        return transfer is null ? new(Error: TransferFailure.NotFound) : new(Transfer: transfer);
    }
}
