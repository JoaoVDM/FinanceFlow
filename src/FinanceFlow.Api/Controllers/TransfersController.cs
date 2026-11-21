using FinanceFlow.Api.Contracts.Transfers;
using FinanceFlow.Api.Concurrency;
using FinanceFlow.Application.Transfers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;

namespace FinanceFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/transfers"), RequestSizeLimit(16_384)]
public sealed class TransfersController(TransfersService transfers) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<TransferView>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Create(CreateTransferRequest request, CancellationToken cancellationToken)
    {
        var keys = Request.Headers["Idempotency-Key"];
        if (keys.Count != 1)
        {
            return Failure(TransferFailure.InvalidInput);
        }
        var result = await transfers.CreateAsync(new(request.SourceAccountId!.Value, request.DestinationAccountId!.Value,
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
    [ProducesResponseType<TransferView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await transfers.GetAsync(id, cancellationToken);
        if (result.Error is { } error)
        {
            return Failure(error);
        }
        Response.Headers.ETag = '"' + result.Transfer!.Version + '"';
        return Ok(result.Transfer);
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
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        if (HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true || Request.ContentLength > 0)
        {
            return Problem(statusCode: 400);
        }
        var headerError = IfMatchHeader.Read(Request.Headers, out var version);
        if (headerError is { } status)
        {
            return Problem(statusCode: status);
        }

        var result = await transfers.CancelAsync(id, version!, cancellationToken);
        if (result.Error is { } error)
        {
            return Failure(error);
        }
        Response.Headers.ETag = '"' + result.Version + '"';
        return NoContent();
    }

    private IActionResult Failure(TransferFailure error) => Problem(statusCode: error switch
    {
        TransferFailure.InvalidInput => 400,
        TransferFailure.Unauthenticated => 401,
        TransferFailure.NotFound => 404,
        TransferFailure.VersionConflict => 412,
        TransferFailure.InactiveAccount or TransferFailure.IdempotencyConflict => 409,
        _ => 503
    });
}
