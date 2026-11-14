using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FinanceFlowDbContext)), Migration("20261114000200_CategoriesDeliveryB")]
public sealed class CategoriesDeliveryB : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE [dbo].[Categories] (
          [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_Categories] PRIMARY KEY,
          [UserId] uniqueidentifier NOT NULL,
          [Name] nvarchar(100) NOT NULL,
          [NormalizedName] nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
          [Kind] varchar(7) COLLATE Latin1_General_100_BIN2 NOT NULL,
          [IsActive] bit NOT NULL,
          [CreatedAtUtc] datetimeoffset NOT NULL,
          [UpdatedAtUtc] datetimeoffset NULL,
          [RowVersion] rowversion NOT NULL,
          CONSTRAINT [AK_Categories_Id_UserId_Kind] UNIQUE ([Id], [UserId], [Kind]),
          CONSTRAINT [FK_Categories_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE NO ACTION,
          CONSTRAINT [CK_Categories_Name] CHECK (LEN(LTRIM(RTRIM([Name]))) > 0),
          CONSTRAINT [CK_Categories_NormalizedName] CHECK (LEN(LTRIM(RTRIM([NormalizedName]))) > 0),
          CONSTRAINT [CK_Categories_Kind] CHECK (([Kind] = 'income' AND DATALENGTH([Kind]) = 6) OR ([Kind] = 'expense' AND DATALENGTH([Kind]) = 7)));
        CREATE UNIQUE INDEX [IX_Categories_UserId_Kind_NormalizedName]
          ON [dbo].[Categories] ([UserId], [Kind], [NormalizedName]);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Categories", "dbo");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => CategoriesDeliveryBModel.Build(modelBuilder);
}
