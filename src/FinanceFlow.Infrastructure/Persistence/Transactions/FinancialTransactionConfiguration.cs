using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Categories;
using FinanceFlow.Domain.Transactions;
using FinanceFlow.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinanceFlow.Infrastructure.Persistence.Transactions;

public sealed class FinancialTransactionConfiguration : IEntityTypeConfiguration<FinancialTransaction>
{
    public void Configure(EntityTypeBuilder<FinancialTransaction> transaction)
    {
        transaction.ToTable("FinancialTransactions", "dbo", table =>
        {
            table.HasCheckConstraint("CK_FinancialTransactions_Amount", "[Amount] > 0 AND [Amount] <= 999999999999.99");
            table.HasCheckConstraint("CK_FinancialTransactions_Kind", "([Kind] = 'income' AND DATALENGTH([Kind]) = 6) OR ([Kind] = 'expense' AND DATALENGTH([Kind]) = 7)");
            table.HasCheckConstraint("CK_FinancialTransactions_Deletion", "([IsDeleted] = 0 AND [DeletedAtUtc] IS NULL) OR ([IsDeleted] = 1 AND [DeletedAtUtc] IS NOT NULL)");
        });
        transaction.HasKey(value => value.Id);
        transaction.Property(value => value.Id).ValueGeneratedNever();
        transaction.HasOne<ApplicationUser>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        transaction.HasOne<FinancialAccount>().WithMany().HasForeignKey(value => new { value.AccountId, value.UserId })
            .HasPrincipalKey(value => new { value.Id, value.UserId }).OnDelete(DeleteBehavior.NoAction);
        transaction.HasOne<Category>().WithMany().HasForeignKey(value => new { value.CategoryId, value.UserId, value.Kind })
            .HasPrincipalKey(value => new { value.Id, value.UserId, value.Kind }).OnDelete(DeleteBehavior.NoAction);
        transaction.Property(value => value.Kind).HasMaxLength(7).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        transaction.Property(value => value.Amount).HasPrecision(18, 2);
        transaction.Property(value => value.OccurredOn).HasColumnType("date");
        transaction.Property(value => value.Description).HasMaxLength(500);
        transaction.Property(value => value.CreatedAtUtc).HasColumnType("datetimeoffset");
        transaction.Property(value => value.UpdatedAtUtc).HasColumnType("datetimeoffset");
        transaction.Property(value => value.DeletedAtUtc).HasColumnType("datetimeoffset");
        transaction.Property(value => value.RowVersion).IsRowVersion().IsRequired();
        transaction.HasIndex(value => new { value.UserId, value.AccountId, value.OccurredOn, value.Id });
        transaction.HasIndex(value => new { value.UserId, value.CategoryId, value.OccurredOn });
        transaction.HasIndex(value => new { value.UserId, value.OccurredOn, value.Id });
    }
}
