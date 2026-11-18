using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

// Frozen target: never call mutable runtime configurations from a historical model.
internal static class TransactionsDelivery5AModel
{
    internal static void Build(ModelBuilder model)
    {
        CategoriesDeliveryBModel.Build(model);
        model.Entity("FinanceFlow.Domain.Transactions.FinancialTransaction", transaction =>
        {
            transaction.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            transaction.Property<Guid>("UserId").HasColumnType("uniqueidentifier");
            transaction.Property<Guid>("AccountId").HasColumnType("uniqueidentifier");
            transaction.Property<Guid>("CategoryId").HasColumnType("uniqueidentifier");
            transaction.Property<string>("Kind").IsRequired().HasMaxLength(7).IsUnicode(false)
                .UseCollation("Latin1_General_100_BIN2").HasColumnType("varchar(7)");
            transaction.Property<decimal>("Amount").HasPrecision(18, 2).HasColumnType("decimal(18,2)");
            transaction.Property<DateOnly>("OccurredOn").HasColumnType("date");
            transaction.Property<string>("Description").HasMaxLength(500).HasColumnType("nvarchar(500)");
            transaction.Property<bool>("IsDeleted").HasColumnType("bit");
            transaction.Property<DateTimeOffset?>("DeletedAtUtc").HasColumnType("datetimeoffset");
            transaction.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("datetimeoffset");
            transaction.Property<DateTimeOffset?>("UpdatedAtUtc").HasColumnType("datetimeoffset");
            transaction.Property<byte[]>("RowVersion").IsRequired().IsRowVersion().HasColumnType("rowversion");
            transaction.HasKey("Id");
            transaction.HasIndex("AccountId", "UserId");
            transaction.HasIndex("CategoryId", "UserId", "Kind");
            transaction.HasIndex("UserId", "AccountId", "OccurredOn", "Id");
            transaction.HasIndex("UserId", "CategoryId", "OccurredOn");
            transaction.HasIndex("UserId", "OccurredOn", "Id");
            transaction.ToTable("FinancialTransactions", "dbo", table =>
            {
                table.HasCheckConstraint("CK_FinancialTransactions_Amount", "[Amount] > 0 AND [Amount] <= 999999999999.99");
                table.HasCheckConstraint("CK_FinancialTransactions_Kind", "([Kind] = 'income' AND DATALENGTH([Kind]) = 6) OR ([Kind] = 'expense' AND DATALENGTH([Kind]) = 7)");
                table.HasCheckConstraint("CK_FinancialTransactions_Deletion", "([IsDeleted] = 0 AND [DeletedAtUtc] IS NULL) OR ([IsDeleted] = 1 AND [DeletedAtUtc] IS NOT NULL)");
            });
            transaction.HasOne("FinanceFlow.Infrastructure.Identity.ApplicationUser", null).WithMany()
                .HasForeignKey("UserId").OnDelete(DeleteBehavior.NoAction).IsRequired();
            transaction.HasOne("FinanceFlow.Domain.Accounts.FinancialAccount", null).WithMany()
                .HasForeignKey("AccountId", "UserId").HasPrincipalKey("Id", "UserId").OnDelete(DeleteBehavior.NoAction).IsRequired();
            transaction.HasOne("FinanceFlow.Domain.Categories.Category", null).WithMany()
                .HasForeignKey("CategoryId", "UserId", "Kind").HasPrincipalKey("Id", "UserId", "Kind").OnDelete(DeleteBehavior.NoAction).IsRequired();
        });
    }
}
