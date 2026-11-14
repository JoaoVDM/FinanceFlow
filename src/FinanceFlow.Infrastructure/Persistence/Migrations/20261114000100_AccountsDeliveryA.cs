using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FinanceFlowDbContext)), Migration("20261114000100_AccountsDeliveryA")]
public sealed class AccountsDeliveryA : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE [dbo].[FinancialAccounts] (
          [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_FinancialAccounts] PRIMARY KEY,
          [UserId] uniqueidentifier NOT NULL,
          [Name] nvarchar(100) NOT NULL,
          [Type] varchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
          [Currency] varchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
          [OpeningBalance] decimal(18,2) NOT NULL, [OpeningDate] date NOT NULL,
          [IsActive] bit NOT NULL, [CreatedAtUtc] datetimeoffset NOT NULL,
          [UpdatedAtUtc] datetimeoffset NULL, [RowVersion] rowversion NOT NULL,
          CONSTRAINT [AK_FinancialAccounts_Id_UserId] UNIQUE ([Id], [UserId]),
          CONSTRAINT [FK_FinancialAccounts_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE NO ACTION,
          CONSTRAINT [CK_FinancialAccounts_Name] CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
          CONSTRAINT [CK_FinancialAccounts_Type] CHECK ([Type] IN ('checking','savings','cash','other')),
          CONSTRAINT [CK_FinancialAccounts_Currency] CHECK ([Currency] = 'BRL'),
          CONSTRAINT [CK_FinancialAccounts_OpeningBalance] CHECK ([OpeningBalance] BETWEEN -999999999999.99 AND 999999999999.99));
        CREATE INDEX [IX_FinancialAccounts_UserId_IsActive] ON [dbo].[FinancialAccounts] ([UserId], [IsActive]);
        CREATE TABLE [dbo].[IdempotencyRecords] (
          [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_IdempotencyRecords] PRIMARY KEY,
          [UserId] uniqueidentifier NOT NULL,
          [Operation] varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
          [Key] varchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
          [RequestHash] binary(32) NOT NULL, [StatusCode] int NOT NULL,
          [ResponseBody] nvarchar(max) NOT NULL, [Location] nvarchar(256) NOT NULL,
          [ETag] varchar(32) NOT NULL, [ContentType] varchar(64) NOT NULL, [CreatedAtUtc] datetimeoffset NOT NULL,
          CONSTRAINT [FK_IdempotencyRecords_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE NO ACTION,
          CONSTRAINT [CK_IdempotencyRecords_Key] CHECK (DATALENGTH([Key]) BETWEEN 1 AND 128 AND [Key] NOT LIKE '%[^!-~]%'),
          CONSTRAINT [CK_IdempotencyRecords_Status] CHECK ([StatusCode] = 201),
          CONSTRAINT [CK_IdempotencyRecords_Body] CHECK (ISJSON([ResponseBody]) = 1));
        CREATE UNIQUE INDEX [IX_IdempotencyRecords_UserId_Operation_Key] ON [dbo].[IdempotencyRecords] ([UserId], [Operation], [Key]);
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("IdempotencyRecords", "dbo");
        migrationBuilder.DropTable("FinancialAccounts", "dbo");
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AccountsDeliveryAModel.Build(modelBuilder);
}
