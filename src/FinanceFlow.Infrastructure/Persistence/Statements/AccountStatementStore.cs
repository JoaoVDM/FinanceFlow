using FinanceFlow.Application.Statements;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Statements;

public sealed class AccountStatementStore(FinanceFlowDbContext db) : IAccountStatementStore
{
    public async Task<StatementResult> GetAsync(Guid userId, Guid accountId, StatementQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await SnapshotReadSession.RunAsync(db, () => ReadAsync(userId, accountId, query, cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (SnapshotReadFailure.IsUnavailable(exception))
        {
            return new(Error: StatementFailure.Unavailable);
        }
    }

    private async Task<StatementResult> ReadAsync(Guid userId, Guid accountId, StatementQuery query, CancellationToken cancellationToken)
    {
        var account = await db.FinancialAccounts.AsNoTracking()
            .Where(value => value.UserId == userId && value.Id == accountId)
            .Select(value => new { value.Currency, value.OpeningDate, value.OpeningBalance })
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return new(Error: StatementFailure.NotFound);
        }

        // Fixed parameterized UNION ALL: derived movements are never added to the EF model or persisted.
        // SUM remains SQL decimal(38,2); combine separate aggregates in checked C# decimal arithmetic
        // to avoid SQL's scale reduction when adding several decimal(38,2) expressions.
        var movements = db.Database.SqlQuery<StatementMovement>($"""
            SELECT [Id] AS [OperationId], CAST('transaction' AS varchar(11)) AS [OperationType],
                CAST([Kind] AS varchar(11)) AS [Nature], [OccurredOn],
                CASE WHEN [Kind] = 'income' THEN [Amount] ELSE -[Amount] END AS [Amount],
                [Description], [CategoryId], CAST(NULL AS uniqueidentifier) AS [CounterpartyAccountId]
            FROM [dbo].[FinancialTransactions]
            WHERE [UserId] = {userId} AND [AccountId] = {accountId} AND [IsDeleted] = 0
                AND [OccurredOn] >= {account.OpeningDate} AND [OccurredOn] <= {query.To}
            UNION ALL
            SELECT [Id], CAST('transfer' AS varchar(11)),
                CAST(CASE WHEN [DestinationAccountId] = {accountId} THEN 'transferIn' ELSE 'transferOut' END AS varchar(11)),
                [OccurredOn], CASE WHEN [DestinationAccountId] = {accountId} THEN [Amount] ELSE -[Amount] END,
                [Description], CAST(NULL AS uniqueidentifier),
                CASE WHEN [DestinationAccountId] = {accountId} THEN [SourceAccountId] ELSE [DestinationAccountId] END
            FROM [dbo].[Transfers]
            WHERE [UserId] = {userId} AND [IsCanceled] = 0
                AND ([SourceAccountId] = {accountId} OR [DestinationAccountId] = {accountId})
                AND [OccurredOn] >= {account.OpeningDate} AND [OccurredOn] <= {query.To}
            """);

        var totals = await movements.GroupBy(value => 1).Select(group => new
        {
            Prior = group.Sum(value => value.OccurredOn < query.From ? value.Amount : 0m),
            Income = group.Sum(value => value.OccurredOn >= query.From && value.Nature == "income" ? value.Amount : 0m),
            Expense = group.Sum(value => value.OccurredOn >= query.From && value.Nature == "expense" ? -value.Amount : 0m),
            TransferIn = group.Sum(value => value.OccurredOn >= query.From && value.Nature == "transferIn" ? value.Amount : 0m),
            TransferOut = group.Sum(value => value.OccurredOn >= query.From && value.Nature == "transferOut" ? -value.Amount : 0m),
            Count = group.Count(value => value.OccurredOn >= query.From)
        }).SingleOrDefaultAsync(cancellationToken);

        var items = await movements.Where(value => value.OccurredOn >= query.From)
            .OrderByDescending(value => value.OccurredOn).ThenBy(value => value.OperationId).ThenBy(value => value.OperationType)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
        var income = totals?.Income ?? 0m;
        var expense = totals?.Expense ?? 0m;
        var transferIn = totals?.TransferIn ?? 0m;
        var transferOut = totals?.TransferOut ?? 0m;
        var opening = account.OpeningDate <= query.From ? checked(account.OpeningBalance + (totals?.Prior ?? 0m)) : 0m;
        var contribution = account.OpeningDate > query.From && account.OpeningDate <= query.To ? account.OpeningBalance : 0m;
        var closing = checked(opening + contribution + income - expense + transferIn - transferOut);
        var count = totals?.Count ?? 0;
        var statement = new AccountStatement(accountId, account.Currency, query.From, query.To,
            account.OpeningDate, account.OpeningBalance, opening, contribution, income, expense,
            checked(income - expense), transferIn, transferOut, closing, items, query.Page, query.PageSize,
            count, (int)Math.Ceiling((decimal)count / query.PageSize));
        return new(Statement: statement);
    }
}
