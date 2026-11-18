using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FinanceFlowDbContext)), Migration("20261117000100_TransactionsDelivery5A")]
public sealed class TransactionsDelivery5A : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE [dbo].[FinancialTransactions] (
          [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_FinancialTransactions] PRIMARY KEY,
          [UserId] uniqueidentifier NOT NULL,
          [AccountId] uniqueidentifier NOT NULL,
          [CategoryId] uniqueidentifier NOT NULL,
          [Kind] varchar(7) COLLATE Latin1_General_100_BIN2 NOT NULL,
          [Amount] decimal(18,2) NOT NULL,
          [OccurredOn] date NOT NULL,
          [Description] nvarchar(500) NULL,
          [IsDeleted] bit NOT NULL,
          [DeletedAtUtc] datetimeoffset NULL,
          [CreatedAtUtc] datetimeoffset NOT NULL,
          [UpdatedAtUtc] datetimeoffset NULL,
          [RowVersion] rowversion NOT NULL,
          CONSTRAINT [FK_FinancialTransactions_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE NO ACTION,
          CONSTRAINT [FK_FinancialTransactions_FinancialAccounts_AccountId_UserId] FOREIGN KEY ([AccountId],[UserId]) REFERENCES [dbo].[FinancialAccounts] ([Id],[UserId]) ON DELETE NO ACTION,
          CONSTRAINT [FK_FinancialTransactions_Categories_CategoryId_UserId_Kind] FOREIGN KEY ([CategoryId],[UserId],[Kind]) REFERENCES [dbo].[Categories] ([Id],[UserId],[Kind]) ON DELETE NO ACTION,
          CONSTRAINT [CK_FinancialTransactions_Amount] CHECK ([Amount] > 0 AND [Amount] <= 999999999999.99),
          CONSTRAINT [CK_FinancialTransactions_Kind] CHECK (([Kind] = 'income' AND DATALENGTH([Kind]) = 6) OR ([Kind] = 'expense' AND DATALENGTH([Kind]) = 7)),
          CONSTRAINT [CK_FinancialTransactions_Deletion] CHECK (([IsDeleted] = 0 AND [DeletedAtUtc] IS NULL) OR ([IsDeleted] = 1 AND [DeletedAtUtc] IS NOT NULL)));
        CREATE INDEX [IX_FinancialTransactions_AccountId_UserId] ON [dbo].[FinancialTransactions] ([AccountId],[UserId]);
        CREATE INDEX [IX_FinancialTransactions_CategoryId_UserId_Kind] ON [dbo].[FinancialTransactions] ([CategoryId],[UserId],[Kind]);
        CREATE INDEX [IX_FinancialTransactions_UserId_AccountId_OccurredOn_Id] ON [dbo].[FinancialTransactions] ([UserId],[AccountId],[OccurredOn],[Id]);
        CREATE INDEX [IX_FinancialTransactions_UserId_CategoryId_OccurredOn] ON [dbo].[FinancialTransactions] ([UserId],[CategoryId],[OccurredOn]);
        CREATE INDEX [IX_FinancialTransactions_UserId_OccurredOn_Id] ON [dbo].[FinancialTransactions] ([UserId],[OccurredOn],[Id]);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("FinancialTransactions", "dbo");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => TransactionsDelivery5AModel.Build(modelBuilder);
}
