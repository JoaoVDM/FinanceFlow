using System.Globalization;
using FinanceFlow.Api.Contracts.Reports;
using FinanceFlow.Application.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/reports")]
public sealed class ReportsController(ReportsService reports) : ControllerBase
{
    private static readonly HashSet<string> Parameters = new(StringComparer.OrdinalIgnoreCase) { "from", "to", "accountId" };

    [HttpGet("summary")]
    [ProducesResponseType<FinancialSummary>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    [ProducesResponseType<ProblemDetails>(500)]
    public async Task<IActionResult> Summary([FromQuery] SummaryRequest request, CancellationToken cancellationToken)
    {
        if (Request.Query.Any(parameter => !Parameters.Contains(parameter.Key)
            || parameter.Value.Count != 1 || string.IsNullOrWhiteSpace(parameter.Value[0])))
        {
            return Problem(statusCode: 400);
        }
        foreach (var name in new[] { "from", "to" })
        {
            if (!Request.Query.TryGetValue(name, out var value)
                || !DateOnly.TryParseExact(value[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return Problem(statusCode: 400);
            }
        }

        var result = await reports.GetSummaryAsync(new(request.From!.Value, request.To!.Value, request.AccountId), cancellationToken);
        return result.Error is { } error ? Problem(statusCode: error switch
        {
            SummaryFailure.InvalidInput => 400,
            SummaryFailure.Unauthenticated => 401,
            SummaryFailure.NotFound => 404,
            _ => 503
        }) : Ok(result.Summary);
    }
}
