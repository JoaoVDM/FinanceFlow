using FinanceFlow.Domain.Accounts;
using FinanceFlow.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinanceFlow.Infrastructure.Persistence.Accounts;

public sealed class FinancialAccountConfiguration : IEntityTypeConfiguration<FinancialAccount>
{
    public void Configure(EntityTypeBuilder<FinancialAccount> account)
    {
        account.ToTable("FinancialAccounts", "dbo", table =>
        {
            table.HasCheckConstraint("CK_FinancialAccounts_Name", "LEN(LTRIM(RTRIM([Name]))) > 0");
            table.HasCheckConstraint("CK_FinancialAccounts_Type", "[Type] IN ('checking','savings','cash','other')");
            table.HasCheckConstraint("CK_FinancialAccounts_Currency", "[Currency] = 'BRL'");
            table.HasCheckConstraint("CK_FinancialAccounts_OpeningBalance", "[OpeningBalance] BETWEEN -999999999999.99 AND 999999999999.99");
        });
        account.HasKey(x => x.Id);
        account.Property(x => x.Id).ValueGeneratedNever();
        account.HasAlternateKey(x => new { x.Id, x.UserId });
        account.HasIndex(x => new { x.UserId, x.IsActive });
        account.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        account.Property(x => x.Name).HasMaxLength(100).IsRequired();
        account.Property(x => x.Type).HasMaxLength(32).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        account.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        account.Property(x => x.OpeningBalance).HasPrecision(18, 2);
        account.Property(x => x.OpeningDate).HasColumnType("date");
        account.Property(x => x.RowVersion).IsRowVersion().IsRequired();
        account.Property(x => x.OpeningBalance).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        account.Property(x => x.OpeningDate).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        account.Property(x => x.Currency).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
