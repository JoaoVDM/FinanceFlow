using System.Text.Json;
using System.Globalization;
using FinanceFlow.Api.Contracts.Accounts;
using FinanceFlow.Api.Contracts.Transactions;
using FinanceFlow.Api.Concurrency;
using FinanceFlow.Application.Transactions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;

namespace FinanceFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/transactions"), RequestSizeLimit(16_384)]
public sealed class TransactionsController(FinancialTransactionsService transactions) : ControllerBase
{
    private static readonly HashSet<string> ListParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "pageSize", "accountId", "categoryId", "kind", "from", "to", "minAmount", "maxAmount", "sort", "direction"
    };
    private static readonly JsonSerializerOptions MoneyJson = new() { Converters = { new ExactMoneyJsonConverter() } };

    [HttpPost]
    [ProducesResponseType<FinancialTransactionView>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Create(CreateTransactionRequest request, CancellationToken cancellationToken)
    {
        var keys = Request.Headers["Idempotency-Key"];
        if (keys.Count != 1)
        {
            return Failure(TransactionFailure.InvalidInput);
        }

        var result = await transactions.CreateAsync(new(request.AccountId!.Value, request.CategoryId!.Value, request.Kind,
            request.Amount!.Value, request.OccurredOn!.Value, request.Description), keys[0], cancellationToken);
        if (result.Error is { } error)
        {
            return Failure(error);
        }

        var response = result.Response!;
        Response.Headers.Location = response.Location;
        Response.Headers.ETag = response.ETag;
        return new ContentResult { StatusCode = response.StatusCode, ContentType = response.ContentType, Content = response.Body };
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<FinancialTransactionView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await transactions.GetAsync(id, cancellationToken);
        if (result.Error is { } error)
        {
            return Failure(error);
        }

        Response.Headers.ETag = '"' + result.Transaction!.Version + '"';
        return Ok(result.Transaction);
    }

    [HttpGet]
    [ProducesResponseType<TransactionPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> List([FromQuery] TransactionListRequest request, CancellationToken cancellationToken)
    {
        if (Request.Query.Any(parameter => !ListParameters.Contains(parameter.Key)
            || parameter.Value.Count != 1 || string.IsNullOrEmpty(parameter.Value[0])))
        {
            return Failure(TransactionFailure.InvalidInput);
        }

        foreach (var parameter in new[] { "from", "to" })
        {
            if (Request.Query.TryGetValue(parameter, out var value)
                && !DateOnly.TryParseExact(value[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return Failure(TransactionFailure.InvalidInput);
            }
        }

        decimal? min;
        decimal? max;
        try
        {
            min = request.MinAmount is null ? null : JsonSerializer.Deserialize<decimal?>(request.MinAmount, MoneyJson);
            max = request.MaxAmount is null ? null : JsonSerializer.Deserialize<decimal?>(request.MaxAmount, MoneyJson);
            if ((request.MinAmount is not null && min is null) || (request.MaxAmount is not null && max is null))
            {
                return Failure(TransactionFailure.InvalidInput);
            }
        }
        catch (JsonException)
        {
            return Failure(TransactionFailure.InvalidInput);
        }

        var result = await transactions.ListAsync(new(request.Page, request.PageSize, request.AccountId, request.CategoryId,
            request.Kind, request.From, request.To, min, max, request.Sort, request.Direction), cancellationToken);
        return result.Error is { } error ? Failure(error) : Ok(result.Page);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Update(Guid id, UpdateTransactionRequest request, CancellationToken cancellationToken)
    {
        var headerError = IfMatchHeader.Read(Request.Headers, out var version);
        if (headerError is { } status)
        {
            return Problem(statusCode: status);
        }
        var result = await transactions.UpdateAsync(id, new(request.AccountId!.Value, request.CategoryId!.Value,
            request.Kind, request.Amount!.Value, request.OccurredOn!.Value, request.Description), version!, cancellationToken);
        return ChangeResponse(result);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        // This financial DELETE has no request DTO; do not silently accept attempted field updates.
        if (HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true || Request.ContentLength > 0)
        {
            return Problem(statusCode: 400);
        }
        var headerError = IfMatchHeader.Read(Request.Headers, out var version);
        if (headerError is { } status)
        {
            return Problem(statusCode: status);
        }
        var result = await transactions.DeleteAsync(id, version!, cancellationToken);
        return ChangeResponse(result);
    }

    private IActionResult ChangeResponse(TransactionChangeResult result)
    {
        if (result.Error is { } error)
        {
            return Failure(error);
        }
        Response.Headers.ETag = '"' + result.Version + '"';
        return NoContent();
    }

    private IActionResult Failure(TransactionFailure error) => Problem(statusCode: error switch
    {
        TransactionFailure.InvalidInput => 400,
        TransactionFailure.Unauthenticated => 401,
        TransactionFailure.NotFound => 404,
        TransactionFailure.InactiveReference or TransactionFailure.IdempotencyConflict => 409,
        TransactionFailure.VersionConflict => 412,
        _ => 503
    });
}
