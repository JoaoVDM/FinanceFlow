using FinanceFlow.Domain.Categories;
using FinanceFlow.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinanceFlow.Infrastructure.Persistence.Categories;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public const string UniqueNameIndex = "IX_Categories_UserId_Kind_NormalizedName";

    public void Configure(EntityTypeBuilder<Category> category)
    {
        category.ToTable("Categories", "dbo", table =>
        {
            table.HasCheckConstraint("CK_Categories_Name", "LEN(LTRIM(RTRIM([Name]))) > 0");
            table.HasCheckConstraint("CK_Categories_NormalizedName", "LEN(LTRIM(RTRIM([NormalizedName]))) > 0");
            // SQL comparisons otherwise pad trailing spaces, even under BIN2.
            table.HasCheckConstraint("CK_Categories_Kind",
                "([Kind] = 'income' AND DATALENGTH([Kind]) = 6) OR ([Kind] = 'expense' AND DATALENGTH([Kind]) = 7)");
        });
        category.HasKey(category => category.Id);
        category.Property(category => category.Id).ValueGeneratedNever();
        category.HasAlternateKey(category => new { category.Id, category.UserId, category.Kind });
        category.HasIndex(category => new { category.UserId, category.Kind, category.NormalizedName })
            .HasDatabaseName(UniqueNameIndex).IsUnique();
        category.HasOne<ApplicationUser>().WithMany()
            .HasForeignKey(category => category.UserId).OnDelete(DeleteBehavior.NoAction);
        category.Property(category => category.Name).HasMaxLength(100).IsRequired();
        category.Property(category => category.NormalizedName).HasMaxLength(100)
            .UseCollation("Latin1_General_100_BIN2").IsRequired();
        category.Property(category => category.Kind).HasMaxLength(7).IsUnicode(false)
            .UseCollation("Latin1_General_100_BIN2").IsRequired();
        category.Property(category => category.CreatedAtUtc).HasColumnType("datetimeoffset");
        category.Property(category => category.UpdatedAtUtc).HasColumnType("datetimeoffset");
        category.Property(category => category.RowVersion).IsRowVersion().IsRequired();
        category.Property(category => category.Kind).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
