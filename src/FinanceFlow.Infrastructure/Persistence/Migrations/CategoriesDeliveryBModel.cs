using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

// Frozen incremental target. Do not reuse the mutable runtime entity configuration here.
internal static class CategoriesDeliveryBModel
{
    internal static void Build(ModelBuilder model)
    {
        AccountsDeliveryAModel.Build(model);
        const string categoryType = "FinanceFlow.Domain.Categories.Category";
        const string userType = "FinanceFlow.Infrastructure.Identity.ApplicationUser";
        model.Entity(categoryType, category =>
        {
            category.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            category.Property<Guid>("UserId").HasColumnType("uniqueidentifier");
            category.Property<string>("Name").IsRequired().HasMaxLength(100).HasColumnType("nvarchar(100)");
            category.Property<string>("NormalizedName").IsRequired().HasMaxLength(100)
                .UseCollation("Latin1_General_100_BIN2").HasColumnType("nvarchar(100)");
            category.Property<string>("Kind").IsRequired().HasMaxLength(7).IsUnicode(false)
                .UseCollation("Latin1_General_100_BIN2").HasColumnType("varchar(7)");
            category.Property<bool>("IsActive").HasColumnType("bit");
            category.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("datetimeoffset");
            category.Property<DateTimeOffset?>("UpdatedAtUtc").HasColumnType("datetimeoffset");
            category.Property<byte[]>("RowVersion").IsRequired().IsRowVersion().HasColumnType("rowversion");
            category.HasKey("Id");
            category.HasAlternateKey("Id", "UserId", "Kind");
            category.HasIndex("UserId", "Kind", "NormalizedName")
                .IsUnique().HasDatabaseName("IX_Categories_UserId_Kind_NormalizedName");
            category.ToTable("Categories", "dbo", table =>
            {
                table.HasCheckConstraint("CK_Categories_Name", "LEN(LTRIM(RTRIM([Name]))) > 0");
                table.HasCheckConstraint("CK_Categories_NormalizedName", "LEN(LTRIM(RTRIM([NormalizedName]))) > 0");
                table.HasCheckConstraint("CK_Categories_Kind",
                    "([Kind] = 'income' AND DATALENGTH([Kind]) = 6) OR ([Kind] = 'expense' AND DATALENGTH([Kind]) = 7)");
            });
            category.HasOne(userType, null).WithMany().HasForeignKey("UserId")
                .OnDelete(DeleteBehavior.NoAction).IsRequired();
        });
    }
}
