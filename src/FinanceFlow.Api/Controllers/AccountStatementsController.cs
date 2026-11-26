using System.Globalization;
using FinanceFlow.Api.Contracts.Statements;
using FinanceFlow.Application.Statements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/accounts/{id:guid}/statement")]
public sealed class AccountStatementsController(AccountStatementsService statements) : ControllerBase
{
    private static readonly HashSet<string> Parameters = new(StringComparer.OrdinalIgnoreCase) { "from", "to", "page", "pageSize" };

    [HttpGet]
    [ProducesResponseType<AccountStatement>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Get(Guid id, [FromQuery] StatementRequest request, CancellationToken cancellationToken)
    {
        if (Request.Query.Any(parameter => !Parameters.Contains(parameter.Key)
            || parameter.Value.Count != 1 || string.IsNullOrEmpty(parameter.Value[0])))
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

        var result = await statements.GetAsync(id, new(request.From!.Value, request.To!.Value, request.Page, request.PageSize), cancellationToken);
        return result.Error is { } error ? Problem(statusCode: error switch
        {
            StatementFailure.InvalidInput => 400,
            StatementFailure.Unauthenticated => 401,
            StatementFailure.NotFound => 404,
            _ => 503
        }) : Ok(result.Statement);
    }
}
