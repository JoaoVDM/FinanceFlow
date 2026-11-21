using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

// Frozen target: preserve the prior model and the transfer mapping as introduced in 6A.
internal static class TransfersDelivery6AModel
{
    internal static void Build(ModelBuilder model)
    {
        TransactionsDelivery5AModel.Build(model);
        model.Entity("FinanceFlow.Domain.Transfers.Transfer", transfer =>
        {
            transfer.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            transfer.Property<Guid>("UserId").HasColumnType("uniqueidentifier");
            transfer.Property<Guid>("SourceAccountId").HasColumnType("uniqueidentifier");
            transfer.Property<Guid>("DestinationAccountId").HasColumnType("uniqueidentifier");
            transfer.Property<decimal>("Amount").HasPrecision(18, 2).HasColumnType("decimal(18,2)");
            transfer.Property<DateOnly>("OccurredOn").HasColumnType("date");
            transfer.Property<string>("Description").HasMaxLength(500).HasColumnType("nvarchar(500)");
            transfer.Property<bool>("IsCanceled").HasColumnType("bit");
            transfer.Property<DateTimeOffset?>("CanceledAtUtc").HasColumnType("datetimeoffset");
            transfer.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("datetimeoffset");
            transfer.Property<DateTimeOffset?>("UpdatedAtUtc").HasColumnType("datetimeoffset");
            transfer.Property<byte[]>("RowVersion").IsRequired().IsRowVersion().HasColumnType("rowversion");
            transfer.HasKey("Id");
            transfer.HasIndex("SourceAccountId", "UserId");
            transfer.HasIndex("DestinationAccountId", "UserId");
            transfer.HasIndex("UserId", "SourceAccountId", "OccurredOn", "Id");
            transfer.HasIndex("UserId", "DestinationAccountId", "OccurredOn", "Id");
            transfer.ToTable("Transfers", "dbo", table =>
            {
                table.HasCheckConstraint("CK_Transfers_Amount", "[Amount] > 0 AND [Amount] <= 999999999999.99");
                table.HasCheckConstraint("CK_Transfers_DistinctAccounts", "[SourceAccountId] <> [DestinationAccountId]");
                table.HasCheckConstraint("CK_Transfers_Cancellation", "([IsCanceled] = 0 AND [CanceledAtUtc] IS NULL) OR ([IsCanceled] = 1 AND [CanceledAtUtc] IS NOT NULL)");
            });
            transfer.HasOne("FinanceFlow.Infrastructure.Identity.ApplicationUser", null).WithMany()
                .HasForeignKey("UserId").OnDelete(DeleteBehavior.NoAction).IsRequired();
            transfer.HasOne("FinanceFlow.Domain.Accounts.FinancialAccount", null).WithMany()
                .HasForeignKey("SourceAccountId", "UserId").HasPrincipalKey("Id", "UserId").OnDelete(DeleteBehavior.NoAction).IsRequired();
            transfer.HasOne("FinanceFlow.Domain.Accounts.FinancialAccount", null).WithMany()
                .HasForeignKey("DestinationAccountId", "UserId").HasPrincipalKey("Id", "UserId").OnDelete(DeleteBehavior.NoAction).IsRequired();
        });
    }
}
