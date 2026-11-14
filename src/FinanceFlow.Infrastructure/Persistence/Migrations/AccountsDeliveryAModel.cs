using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

// Frozen target of AccountsDeliveryA. Never replace this with runtime configurations.
internal static class AccountsDeliveryAModel
{
    internal static void Build(ModelBuilder model)
    {
        InitialIdentityModel.Build(model);
        const string accountType = "FinanceFlow.Domain.Accounts.FinancialAccount";
        const string recordType = "FinanceFlow.Infrastructure.Persistence.Accounts.IdempotencyRecord";
        const string userType = "FinanceFlow.Infrastructure.Identity.ApplicationUser";
        model.Entity(accountType, account =>
        {
            account.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            account.Property<Guid>("UserId").HasColumnType("uniqueidentifier");
            account.Property<string>("Name").IsRequired().HasMaxLength(100).HasColumnType("nvarchar(100)");
            account.Property<string>("Type").IsRequired().HasMaxLength(32).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").HasColumnType("varchar(32)");
            account.Property<string>("Currency").IsRequired().HasMaxLength(3).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").HasColumnType("varchar(3)");
            account.Property<decimal>("OpeningBalance").HasPrecision(18, 2).HasColumnType("decimal(18,2)");
            account.Property<DateOnly>("OpeningDate").HasColumnType("date");
            account.Property<bool>("IsActive").HasColumnType("bit");
            account.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("datetimeoffset");
            account.Property<DateTimeOffset?>("UpdatedAtUtc").HasColumnType("datetimeoffset");
            account.Property<byte[]>("RowVersion").IsRequired().IsRowVersion().HasColumnType("rowversion");
            account.HasKey("Id");
            account.HasAlternateKey("Id", "UserId");
            account.HasIndex("UserId", "IsActive");
            account.ToTable("FinancialAccounts", "dbo", table =>
            {
                table.HasCheckConstraint("CK_FinancialAccounts_Name", "LEN(LTRIM(RTRIM([Name]))) > 0");
                table.HasCheckConstraint("CK_FinancialAccounts_Type", "[Type] IN ('checking','savings','cash','other')");
                table.HasCheckConstraint("CK_FinancialAccounts_Currency", "[Currency] = 'BRL'");
                table.HasCheckConstraint("CK_FinancialAccounts_OpeningBalance", "[OpeningBalance] BETWEEN -999999999999.99 AND 999999999999.99");
            });
            account.HasOne(userType, null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.NoAction).IsRequired();
        });
        model.Entity(recordType, record =>
        {
            record.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uniqueidentifier");
            record.Property<Guid>("UserId").HasColumnType("uniqueidentifier");
            record.Property<string>("Operation").IsRequired().HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").HasColumnType("varchar(64)");
            record.Property<string>("Key").IsRequired().HasMaxLength(128).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").HasColumnType("varchar(128)");
            record.Property<byte[]>("RequestHash").IsRequired().HasColumnType("binary(32)");
            record.Property<int>("StatusCode").HasColumnType("int");
            record.Property<string>("ResponseBody").IsRequired().HasColumnType("nvarchar(max)");
            record.Property<string>("Location").IsRequired().HasMaxLength(256).HasColumnType("nvarchar(256)");
            record.Property<string>("ETag").IsRequired().HasMaxLength(32).IsUnicode(false).HasColumnType("varchar(32)");
            record.Property<string>("ContentType").IsRequired().HasMaxLength(64).IsUnicode(false).HasColumnType("varchar(64)");
            record.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("datetimeoffset");
            record.HasKey("Id");
            record.HasIndex("UserId", "Operation", "Key").IsUnique().HasDatabaseName("IX_IdempotencyRecords_UserId_Operation_Key");
            record.ToTable("IdempotencyRecords", "dbo", table =>
            {
                table.HasCheckConstraint("CK_IdempotencyRecords_Key", "DATALENGTH([Key]) BETWEEN 1 AND 128 AND [Key] NOT LIKE '%[^!-~]%'");
                table.HasCheckConstraint("CK_IdempotencyRecords_Status", "[StatusCode] = 201");
                table.HasCheckConstraint("CK_IdempotencyRecords_Body", "ISJSON([ResponseBody]) = 1");
            });
            record.HasOne(userType, null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.NoAction).IsRequired();
        });
    }
}
