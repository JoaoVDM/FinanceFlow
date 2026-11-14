using FinanceFlow.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinanceFlow.Infrastructure.Persistence.Accounts;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public const string UniqueIndex = "IX_IdempotencyRecords_UserId_Operation_Key";
    public void Configure(EntityTypeBuilder<IdempotencyRecord> record)
    {
        record.ToTable("IdempotencyRecords", "dbo", table =>
        {
            table.HasCheckConstraint("CK_IdempotencyRecords_Key", "DATALENGTH([Key]) BETWEEN 1 AND 128 AND [Key] NOT LIKE '%[^!-~]%'");
            table.HasCheckConstraint("CK_IdempotencyRecords_Status", "[StatusCode] = 201");
            table.HasCheckConstraint("CK_IdempotencyRecords_Body", "ISJSON([ResponseBody]) = 1");
        });
        record.HasKey(x => x.Id);
        record.Property(x => x.Id).ValueGeneratedNever();
        record.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        record.HasIndex(x => new { x.UserId, x.Operation, x.Key }).IsUnique().HasDatabaseName(UniqueIndex);
        record.Property(x => x.Operation).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        record.Property(x => x.Key).HasMaxLength(128).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        record.Property(x => x.RequestHash).HasColumnType("binary(32)").IsRequired();
        record.Property(x => x.ResponseBody).IsRequired();
        record.Property(x => x.Location).HasMaxLength(256).IsRequired();
        record.Property(x => x.ETag).HasMaxLength(32).IsUnicode(false).IsRequired();
        record.Property(x => x.ContentType).HasMaxLength(64).IsUnicode(false).IsRequired();
    }
}
