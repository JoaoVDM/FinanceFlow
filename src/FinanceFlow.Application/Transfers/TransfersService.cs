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

    public Task<TransferListResult> ListAsync(TransferQuery query, CancellationToken cancellationToken)
    {
        var userId = currentUser.UserId;
        if (userId == Guid.Empty)
        {
            return Task.FromResult(new TransferListResult(Error: TransferFailure.Unauthenticated));
        }

        var today = calendar.DateAt(clock.GetUtcNow());
        if (query.Page < 1 || query.PageSize is < 1 or > 100
            || (long)(query.Page - 1) * query.PageSize > int.MaxValue
            || query.AccountId == Guid.Empty || query.From > today || query.To > today || query.From > query.To
            || query.Sort is not ("occurredOn" or "amount" or "id") || query.Direction is not ("asc" or "desc"))
        {
            return Task.FromResult(new TransferListResult(Error: TransferFailure.InvalidInput));
        }

        return store.ListAsync(userId, query, cancellationToken);
    }
}
