using FinanceFlow.Domain.Transfers;
using Xunit;

namespace FinanceFlow.Domain.Tests;

public sealed class TransferCancellationTests
{
    [Theory]
    [InlineData(-3)]
    [InlineData(0)]
    [InlineData(5)]
    public void Cancellation_changes_only_state_and_utc_technical_timestamps(int offsetHours)
    {
        var createdAt = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var transfer = Transfer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            500.01m, new(2000, 1, 3), "Original description", new(2026, 11, 20), createdAt);
        var original = (transfer.Id, transfer.UserId, transfer.SourceAccountId, transfer.DestinationAccountId,
            transfer.Amount, transfer.OccurredOn, transfer.Description, transfer.CreatedAtUtc);
        var canceledAt = createdAt.AddMinutes(5).ToOffset(TimeSpan.FromHours(offsetHours));

        transfer.Cancel(canceledAt);

        Assert.True(transfer.IsCanceled);
        Assert.Equal(canceledAt.ToUniversalTime(), transfer.CanceledAtUtc);
        Assert.Equal(TimeSpan.Zero, transfer.CanceledAtUtc!.Value.Offset);
        Assert.Equal(transfer.CanceledAtUtc, transfer.UpdatedAtUtc);
        Assert.Equal(original, (transfer.Id, transfer.UserId, transfer.SourceAccountId, transfer.DestinationAccountId,
            transfer.Amount, transfer.OccurredOn, transfer.Description, transfer.CreatedAtUtc));
        Assert.Empty(transfer.RowVersion); // Only SQL Server produces the persisted version.
        Assert.All(typeof(Transfer).GetProperties(), property => Assert.False(property.SetMethod?.IsPublic));
    }

    [Fact]
    public void Repeated_cancellation_is_rejected_without_changing_the_original_cancellation()
    {
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var transfer = Transfer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1m, new(2000, 1, 1), null, new(2026, 11, 20), now);
        transfer.Cancel(now);

        Assert.Throws<InvalidOperationException>(() => transfer.Cancel(now.AddHours(1)));

        Assert.True(transfer.IsCanceled);
        Assert.Equal(now, transfer.CanceledAtUtc);
        Assert.Equal(now, transfer.UpdatedAtUtc);
    }
}
