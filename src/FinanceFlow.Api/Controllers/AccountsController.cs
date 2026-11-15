using FinanceFlow.Api.Contracts.Accounts;
using FinanceFlow.Api.Concurrency;
using FinanceFlow.Application.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace FinanceFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/accounts"), RequestSizeLimit(16_384)]
public sealed class AccountsController(FinancialAccountsService accounts) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<FinancialAccountView>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Create(CreateAccountRequest request, CancellationToken cancellationToken)
    {
        var keys = Request.Headers["Idempotency-Key"];
        if (keys.Count != 1)
        {
            return Failure(AccountFailure.InvalidInput);
        }

        var result = await accounts.CreateAsync(
            new(request.Name, request.Type, request.Currency, request.OpeningBalance!.Value, request.OpeningDate!.Value),
            keys[0],
            cancellationToken);
        if (result.Error is { } error)
        {
            return Failure(error);
        }

        var response = result.Response!;
        Response.Headers.Location = response.Location;
        Response.Headers.ETag = response.ETag;
        Response.Headers.CacheControl = "no-store";
        return new ContentResult
        {
            StatusCode = response.StatusCode,
            ContentType = response.ContentType,
            Content = response.Body
        };
    }

    [HttpGet]
    [ProducesResponseType<AccountPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    public async Task<IActionResult> List([FromQuery] AccountListRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.ListAsync(
            new(request.Page, request.PageSize, request.IsActive, request.Type, request.Sort, request.Direction),
            cancellationToken);
        if (result.Error is { } error)
        {
            return Failure(error);
        }

        Response.Headers.CacheControl = "no-store";
        return Ok(result.Page);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<FinancialAccountView>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await accounts.GetAsync(id, cancellationToken);
        if (result.Error is { } error)
        {
            return Failure(error);
        }

        Response.Headers.ETag = '"' + result.Account!.Version + '"';
        Response.Headers.CacheControl = "no-store";
        // Match the frozen Web JSON representation used by the creation snapshot.
        return new ContentResult
        {
            StatusCode = 200,
            ContentType = "application/json; charset=utf-8",
            Content = JsonSerializer.Serialize(result.Account, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> Update(Guid id, UpdateAccountRequest request, CancellationToken cancellationToken)
    {
        var headerError = IfMatchHeader.Read(Request.Headers, out var version);
        if (headerError is { } status)
        {
            return Problem(statusCode: status);
        }

        var result = await accounts.UpdateAsync(id, new(request.Name, request.Type), version!, cancellationToken);
        return ChangeResponse(result);
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [ProducesResponseType<ProblemDetails>(429)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> SetActivity(Guid id, SetAccountActivityRequest request, CancellationToken cancellationToken)
    {
        var headerError = IfMatchHeader.Read(Request.Headers, out var version);
        if (headerError is { } status)
        {
            return Problem(statusCode: status);
        }

        var result = await accounts.SetActivityAsync(id, request.IsActive!.Value, version!, cancellationToken);
        return ChangeResponse(result);
    }

    private IActionResult ChangeResponse(AccountChangeResult result)
    {
        if (result.Error is { } error)
        {
            return Failure(error);
        }

        Response.Headers.ETag = '"' + result.Version + '"';
        return NoContent();
    }

    private IActionResult Failure(AccountFailure error)
    {
        var status = error switch
        {
            AccountFailure.InvalidInput => 400,
            AccountFailure.Unauthenticated => 401,
            AccountFailure.NotFound => 404,
            AccountFailure.IdempotencyConflict => 409,
            AccountFailure.VersionConflict => 412,
            _ => 503
        };
        if (status == 401)
        {
            Response.Headers.WWWAuthenticate = "Bearer";
        }

        return Problem(statusCode: status, title: status switch
        {
            400 => "Invalid account request.",
            401 => "Authentication required.",
            404 => "Resource not found.",
            409 => "The idempotency key conflicts with the original command.",
            412 => "The resource version has changed.",
            _ => "Service unavailable."
        });
    }
}
