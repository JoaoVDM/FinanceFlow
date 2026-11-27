using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Categories;
using FinanceFlow.Application.Transactions;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Application.Statements;
using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Categories;
using FinanceFlow.Domain.Transactions;
using FinanceFlow.Domain.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using FinanceFlow.Infrastructure.Persistence.Accounts;
using FinanceFlow.Infrastructure.Persistence.Categories;
using FinanceFlow.Infrastructure.Persistence.Transactions;
using FinanceFlow.Infrastructure.Persistence.Transfers;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.QueryAnalysis;

internal static class OfflineSql
{
    internal static void Write(string output)
    {
        using var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>()
            .UseSqlServer("Server=localhost;Database=OfflineOnly;Integrated Security=True;TrustServerCertificate=True").Options);
        var owner = SeedData.Owner(0);
        var account = SeedData.Account(0, 0);
        var from = Program.From;
        var to = Program.To;
        // Representative translation only. Actual store commands/parameters are captured in measured mode.
        var transactions = db.FinancialTransactions.AsNoTracking().Where(t => t.UserId == owner && !t.IsDeleted && t.OccurredOn >= from && t.OccurredOn <= to);
        Save("accounts", db.FinancialAccounts.AsNoTracking().Where(a => a.UserId == owner).OrderBy(a => a.Name).ThenBy(a => a.Id).Skip(0).Take(20)
            .Select(Projection<FinancialAccountStore, FinancialAccount, FinancialAccountView>()));
        Save("categories", db.Categories.AsNoTracking().Where(c => c.UserId == owner).OrderBy(c => c.Name).ThenBy(c => c.Id).Skip(0).Take(20)
            .Select(Projection<CategoryStore, Category, CategoryView>()));
        Save("transactions", transactions.OrderByDescending(t => t.OccurredOn).ThenBy(t => t.Id).Skip(0).Take(20)
            .Select(Projection<FinancialTransactionStore, FinancialTransaction, FinancialTransactionView>()));
        Save("transfers", db.Transfers.AsNoTracking().Where(t => t.UserId == owner && !t.IsCanceled && (t.SourceAccountId == account || t.DestinationAccountId == account)
                && t.OccurredOn >= from && t.OccurredOn <= to).OrderByDescending(t => t.OccurredOn).ThenBy(t => t.Id).Skip(0).Take(20)
            .Select(Projection<TransferStore, Transfer, TransferView>()));
        Save("balance", db.FinancialAccounts.AsNoTracking().Where(a => a.UserId == owner && a.Id == account).Select(a => new
        {
            a.OpeningBalance, a.OpeningDate,
            Net = db.FinancialTransactions.Where(t => t.UserId == owner && t.AccountId == account && !t.IsDeleted && t.OccurredOn <= to)
                .Sum(t => (decimal?)(t.Kind == "income" ? t.Amount : -t.Amount)) ?? 0m,
            TransferNet = db.Transfers.Where(t => t.UserId == owner && !t.IsCanceled && t.OccurredOn <= to && (t.SourceAccountId == account || t.DestinationAccountId == account))
                .Sum(t => (decimal?)(t.DestinationAccountId == account ? t.Amount : -t.Amount)) ?? 0m
        }));
        Save("summary", transactions.GroupBy(t => 1).Select(g => new { Income = g.Sum(t => t.Kind == "income" ? t.Amount : 0m), Expense = g.Sum(t => t.Kind == "expense" ? t.Amount : 0m) }));
        Save("by-category", (from t in transactions join c in db.Categories.Where(c => c.UserId == owner)
                on new { Id = t.CategoryId, t.UserId, t.Kind } equals new { c.Id, c.UserId, c.Kind }
            group t by new { c.Id, c.Name, c.Kind } into g select new { g.Key.Id, g.Key.Name, g.Key.Kind, Total = g.Sum(t => t.Amount) })
            .OrderBy(g => g.Kind).ThenByDescending(g => g.Total).ThenBy(g => g.Id));
        var indexes = db.Model.GetEntityTypes().SelectMany(entity => entity.GetIndexes().Select(index => new
        {
            table = entity.GetTableName(), name = index.GetDatabaseName(), index.IsUnique,
            columns = index.Properties.Select(property => property.Name).ToArray()
        }));
        File.WriteAllText(Path.Combine(output, "model-indexes.json"), JsonSerializer.Serialize(indexes, Program.Json));
        var keys = db.Model.GetEntityTypes().SelectMany(entity => entity.GetKeys().Select(key => new
        {
            table = entity.GetTableName(), name = key.GetName(), primary = key.IsPrimaryKey(), columns = key.Properties.Select(p => p.Name).ToArray()
        }));
        File.WriteAllText(Path.Combine(output, "model-keys.json"), JsonSerializer.Serialize(keys, Program.Json));
        var opening = new DateOnly(2026, 1, 1);
        var movements = db.Database.SqlQuery<StatementMovement>($"""
            SELECT [Id] AS [OperationId], CAST('transaction' AS varchar(11)) AS [OperationType],
                CAST([Kind] AS varchar(11)) AS [Nature], [OccurredOn],
                CASE WHEN [Kind]='income' THEN [Amount] ELSE -[Amount] END AS [Amount],
                [Description], [CategoryId], CAST(NULL AS uniqueidentifier) AS [CounterpartyAccountId]
            FROM [dbo].[FinancialTransactions] WHERE [UserId]={owner} AND [AccountId]={account} AND [IsDeleted]=0 AND [OccurredOn]>={opening} AND [OccurredOn]<={to}
            UNION ALL
            SELECT [Id], CAST('transfer' AS varchar(11)),
                CAST(CASE WHEN [DestinationAccountId]={account} THEN 'transferIn' ELSE 'transferOut' END AS varchar(11)),
                [OccurredOn], CASE WHEN [DestinationAccountId]={account} THEN [Amount] ELSE -[Amount] END,
                [Description], CAST(NULL AS uniqueidentifier),
                CASE WHEN [DestinationAccountId]={account} THEN [SourceAccountId] ELSE [DestinationAccountId] END
            FROM [dbo].[Transfers] WHERE [UserId]={owner} AND [IsCanceled]=0
                AND ([SourceAccountId]={account} OR [DestinationAccountId]={account}) AND [OccurredOn]>={opening} AND [OccurredOn]<={to}
            """);
        Save("statement", movements.Where(m => m.OccurredOn >= from).OrderByDescending(m => m.OccurredOn)
            .ThenBy(m => m.OperationId).ThenBy(m => m.OperationType).Skip(0).Take(20));
        void Save<T>(string name, IQueryable<T> query) => File.WriteAllText(Path.Combine(output, name + ".sql"), query.ToQueryString());
    }

    private static Expression<Func<TEntity, TView>> Projection<TStore, TEntity, TView>() =>
        (Expression<Func<TEntity, TView>>)(typeof(TStore).GetField("Projection", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("Store projection changed; update the diagnostic."));
}
