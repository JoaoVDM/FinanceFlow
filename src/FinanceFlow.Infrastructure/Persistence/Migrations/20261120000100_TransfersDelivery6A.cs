using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FinanceFlowDbContext)), Migration("20261120000100_TransfersDelivery6A")]
public sealed class TransfersDelivery6A : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE [dbo].[Transfers] (
          [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_Transfers] PRIMARY KEY,
          [UserId] uniqueidentifier NOT NULL,
          [SourceAccountId] uniqueidentifier NOT NULL,
          [DestinationAccountId] uniqueidentifier NOT NULL,
          [Amount] decimal(18,2) NOT NULL,
          [OccurredOn] date NOT NULL,
          [Description] nvarchar(500) NULL,
          [IsCanceled] bit NOT NULL,
          [CanceledAtUtc] datetimeoffset NULL,
          [CreatedAtUtc] datetimeoffset NOT NULL,
          [UpdatedAtUtc] datetimeoffset NULL,
          [RowVersion] rowversion NOT NULL,
          CONSTRAINT [FK_Transfers_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE NO ACTION,
          CONSTRAINT [FK_Transfers_FinancialAccounts_SourceAccountId_UserId] FOREIGN KEY ([SourceAccountId],[UserId]) REFERENCES [dbo].[FinancialAccounts] ([Id],[UserId]) ON DELETE NO ACTION,
          CONSTRAINT [FK_Transfers_FinancialAccounts_DestinationAccountId_UserId] FOREIGN KEY ([DestinationAccountId],[UserId]) REFERENCES [dbo].[FinancialAccounts] ([Id],[UserId]) ON DELETE NO ACTION,
          CONSTRAINT [CK_Transfers_Amount] CHECK ([Amount] > 0 AND [Amount] <= 999999999999.99),
          CONSTRAINT [CK_Transfers_DistinctAccounts] CHECK ([SourceAccountId] <> [DestinationAccountId]),
          CONSTRAINT [CK_Transfers_Cancellation] CHECK (([IsCanceled] = 0 AND [CanceledAtUtc] IS NULL) OR ([IsCanceled] = 1 AND [CanceledAtUtc] IS NOT NULL)));
        CREATE INDEX [IX_Transfers_SourceAccountId_UserId] ON [dbo].[Transfers] ([SourceAccountId],[UserId]);
        CREATE INDEX [IX_Transfers_DestinationAccountId_UserId] ON [dbo].[Transfers] ([DestinationAccountId],[UserId]);
        CREATE INDEX [IX_Transfers_UserId_SourceAccountId_OccurredOn_Id] ON [dbo].[Transfers] ([UserId],[SourceAccountId],[OccurredOn],[Id]);
        CREATE INDEX [IX_Transfers_UserId_DestinationAccountId_OccurredOn_Id] ON [dbo].[Transfers] ([UserId],[DestinationAccountId],[OccurredOn],[Id]);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Transfers", "dbo");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => TransfersDelivery6AModel.Build(modelBuilder);
}
