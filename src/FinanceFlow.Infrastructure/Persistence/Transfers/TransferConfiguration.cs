using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Transfers;
using FinanceFlow.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinanceFlow.Infrastructure.Persistence.Transfers;

public sealed class TransferConfiguration : IEntityTypeConfiguration<Transfer>
{
    public void Configure(EntityTypeBuilder<Transfer> transfer)
    {
        transfer.ToTable("Transfers", "dbo", table =>
        {
            table.HasCheckConstraint("CK_Transfers_Amount", "[Amount] > 0 AND [Amount] <= 999999999999.99");
            table.HasCheckConstraint("CK_Transfers_DistinctAccounts", "[SourceAccountId] <> [DestinationAccountId]");
            table.HasCheckConstraint("CK_Transfers_Cancellation", "([IsCanceled] = 0 AND [CanceledAtUtc] IS NULL) OR ([IsCanceled] = 1 AND [CanceledAtUtc] IS NOT NULL)");
        });
        transfer.HasKey(value => value.Id);
        transfer.Property(value => value.Id).ValueGeneratedNever();
        transfer.HasOne<ApplicationUser>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        transfer.HasOne<FinancialAccount>().WithMany().HasForeignKey(value => new { value.SourceAccountId, value.UserId })
            .HasPrincipalKey(value => new { value.Id, value.UserId }).OnDelete(DeleteBehavior.NoAction);
        transfer.HasOne<FinancialAccount>().WithMany().HasForeignKey(value => new { value.DestinationAccountId, value.UserId })
            .HasPrincipalKey(value => new { value.Id, value.UserId }).OnDelete(DeleteBehavior.NoAction);
        transfer.Property(value => value.Amount).HasPrecision(18, 2);
        transfer.Property(value => value.OccurredOn).HasColumnType("date");
        transfer.Property(value => value.Description).HasMaxLength(500);
        transfer.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset");
        transfer.Property(value => value.UpdatedAtUtc).HasColumnType("datetimeoffset");
        transfer.Property(value => value.CanceledAtUtc).HasColumnType("datetimeoffset");
        transfer.Property(value => value.RowVersion).IsRowVersion().IsRequired();
        transfer.Property(value => value.UserId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        transfer.Property(value => value.CreatedAtUtc).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        transfer.HasIndex(value => new { value.UserId, value.SourceAccountId, value.OccurredOn, value.Id });
        transfer.HasIndex(value => new { value.UserId, value.DestinationAccountId, value.OccurredOn, value.Id });
    }
}
