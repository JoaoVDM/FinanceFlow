namespace FinanceFlow.Application.Reports;

public interface IReportStore
{
    Task<SummaryResult> GetSummaryAsync(Guid userId, SummaryQuery query, CancellationToken cancellationToken);
    Task<CategoryReportResult> GetByCategoryAsync(Guid userId, CategoryReportQuery query, CancellationToken cancellationToken);
}

public sealed record SummaryQuery(DateOnly From, DateOnly To, Guid? AccountId = null);

public sealed record FinancialSummary(string Currency, DateOnly From, DateOnly To,
    decimal Income, decimal Expense, decimal Result);

public enum SummaryFailure { InvalidInput, Unauthenticated, NotFound, Unavailable }
public sealed record SummaryResult(FinancialSummary? Summary = null, SummaryFailure? Error = null);

public sealed record CategoryReportQuery(DateOnly From, DateOnly To, Guid? AccountId = null, string? Kind = null);
public sealed record CategoryReportGroup(Guid CategoryId, string Name, string Kind, decimal Total);
public enum CategoryReportFailure { InvalidInput, Unauthenticated, NotFound, Unavailable }
public sealed record CategoryReportResult(IReadOnlyList<CategoryReportGroup>? Groups = null, CategoryReportFailure? Error = null);
