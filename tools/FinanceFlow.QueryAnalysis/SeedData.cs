using System.Data;
using System.Text.Json;
using FinanceFlow.Domain.Accounts;
using FinanceFlow.Domain.Categories;
using FinanceFlow.Infrastructure.Identity;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.QueryAnalysis;

internal static class SeedData
{
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-4000-8000-{value:x12}");
    internal static Guid Owner(int user) => Id(1 + user);
    internal static Guid Account(int user, int account) => Id(100 + user * 10 + account);
    private static Guid CategoryId(int user, int category) => Id(200 + user * 20 + category);

    internal static async Task WriteAsync(string connectionString, int size)
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        await using (var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(connectionString).Options))
        {
            for (var user = 0; user < 2; user++)
            {
                db.Users.Add(new ApplicationUser { Id = Owner(user), DisplayName = "Synthetic owner " + user,
                    Email = $"perf{user}@example.invalid", NormalizedEmail = $"PERF{user}@EXAMPLE.INVALID", CreatedAtUtc = now });
                for (var account = 0; account < 4; account++)
                {
                    var entity = FinancialAccount.Create(Account(user, account), Owner(user), "Account " + account,
                        account % 2 == 0 ? "checking" : "savings", "BRL", account * 100m, new(2026, 1, 1), Program.To, now);
                    if (account == 3) entity.SetActivity(false, now);
                    db.FinancialAccounts.Add(entity);
                }
                for (var category = 0; category < 12; category++)
                {
                    var entity = Category.Create(CategoryId(user, category), Owner(user), "Category " + category,
                        category % 2 == 0 ? "income" : "expense", now);
                    if (category == 11) entity.SetActivity(false, now);
                    db.Categories.Add(entity);
                }
            }
            await db.SaveChangesAsync();
        }
        // Synthetic setup only, on this procedure's own disposable database. CHECKs/FKs are enforced.
        var transactions = new DataTable();
        foreach (var column in new[] { "Id", "UserId", "AccountId", "CategoryId" }) transactions.Columns.Add(column, typeof(Guid));
        transactions.Columns.Add("Kind", typeof(string));
        transactions.Columns.Add("Amount", typeof(decimal));
        transactions.Columns.Add("OccurredOn", typeof(DateTime));
        transactions.Columns.Add("IsDeleted", typeof(bool));
        transactions.Columns.Add("CreatedAtUtc", typeof(DateTimeOffset));
        transactions.Columns.Add("DeletedAtUtc", typeof(DateTimeOffset));
        for (var row = 0; row < size; row++)
        {
            var user = row % 2;
            var category = (row / 8) % 12;
            var deleted = row % 10 == 0;
            transactions.Rows.Add(Id(1000 + row), Owner(user), Account(user, (row / 2) % 4), CategoryId(user, category),
                category % 2 == 0 ? "income" : "expense", (row % 9999 + 1) / 100m,
                new DateOnly(2026, 10, 1).AddDays(row % 57).ToDateTime(TimeOnly.MinValue), deleted, now, deleted ? now : DBNull.Value);
        }
        var transfers = new DataTable();
        foreach (var column in new[] { "Id", "UserId", "SourceAccountId", "DestinationAccountId" }) transfers.Columns.Add(column, typeof(Guid));
        transfers.Columns.Add("Amount", typeof(decimal));
        transfers.Columns.Add("OccurredOn", typeof(DateTime));
        transfers.Columns.Add("IsCanceled", typeof(bool));
        transfers.Columns.Add("CreatedAtUtc", typeof(DateTimeOffset));
        transfers.Columns.Add("CanceledAtUtc", typeof(DateTimeOffset));
        for (var row = 0; row < size / 4; row++)
        {
            var user = row % 2;
            var source = (row / 2) % 4;
            var canceled = row % 8 == 0;
            transfers.Rows.Add(Id(100000 + row), Owner(user), Account(user, source), Account(user, (source + 1) % 4),
                (row % 2999 + 1) / 100m, new DateOnly(2026, 10, 1).AddDays(row % 57).ToDateTime(TimeOnly.MinValue), canceled, now, canceled ? now : DBNull.Value);
        }
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var pair in new[] { ("FinancialTransactions", transactions), ("Transfers", transfers) })
        {
            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, null)
            {
                DestinationTableName = "dbo." + pair.Item1, BulkCopyTimeout = 120, BatchSize = 1000
            };
            foreach (DataColumn column in pair.Item2.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(pair.Item2);
        }
        await using var counts = connection.CreateCommand();
        counts.CommandText = """
            SELECT 'transactions' AS [resource], [UserId], [AccountId], [Kind], [IsDeleted] AS [excluded], COUNT(*) AS [rows]
            FROM dbo.FinancialTransactions GROUP BY [UserId],[AccountId],[Kind],[IsDeleted]
            UNION ALL
            SELECT 'transfers', [UserId], [SourceAccountId], 'transfer', [IsCanceled], COUNT(*)
            FROM dbo.Transfers GROUP BY [UserId],[SourceAccountId],[IsCanceled]
            """;
        await using var reader = await counts.ExecuteReaderAsync();
        var distribution = new List<object>();
        while (await reader.ReadAsync()) distribution.Add(new { resource = reader.GetString(0), owner = reader.GetGuid(1), account = reader.GetGuid(2),
            kind = reader.GetString(3), excluded = reader.GetBoolean(4), rows = reader.GetInt32(5) });
        Distribution = JsonSerializer.Serialize(new { transactionRows = transactions.Rows.Count, transferRows = transfers.Rows.Count,
            users = 2, accounts = 8, categories = 24, period = "2026-10-01..2026-11-26", distribution }, Program.Json);
    }

    internal static string Distribution { get; private set; } = "";
}
