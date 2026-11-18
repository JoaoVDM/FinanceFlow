using FinanceFlow.Application.Transactions;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/accounts/{id:guid}/balance")]
public sealed class AccountBalancesController(FinancialTransactionsService transactions) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AccountBalance>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Get(Guid id, [FromQuery] DateOnly? asOf, CancellationToken cancellationToken)
    {
        if (Request.Query.Any(parameter => !parameter.Key.Equals("asOf", StringComparison.OrdinalIgnoreCase)
            || parameter.Value.Count != 1 || string.IsNullOrEmpty(parameter.Value[0])
            || !DateOnly.TryParseExact(parameter.Value[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
        {
            return Problem(statusCode: 400);
        }

        var result = await transactions.BalanceAsync(id, asOf, cancellationToken);
        return result.Error is { } error ? Problem(statusCode: error switch
        {
            TransactionFailure.InvalidInput => 400,
            TransactionFailure.Unauthenticated => 401,
            TransactionFailure.NotFound => 404,
            _ => 503
        }) : Ok(result.Balance);
    }
}
