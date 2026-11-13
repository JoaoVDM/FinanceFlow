using FinanceFlow.Api.Contracts.Users;
using FinanceFlow.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FinanceFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/users/me"), RequestSizeLimit(16_384)]
public sealed class UsersController(IUserAccounts accounts) : AccountControllerBase
{
    [HttpGet]
    [ProducesResponseType<UserProfile>(200), ProducesResponseType<ProblemDetails>(401)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var result = await accounts.GetProfileAsync(CurrentUserId, cancellationToken);
        return result.Error is { } error ? Failure(error) : Ok(result.Profile);
    }

    [HttpPatch]
    [ProducesResponseType(204), ProducesResponseType<ProblemDetails>(400), ProducesResponseType<ProblemDetails>(401)]
    public async Task<IActionResult> Update(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.UpdateProfileAsync(CurrentUserId, request.DisplayName, cancellationToken);
        return result.Error is { } error ? Failure(error) : NoContent();
    }

    [HttpPost("change-password"), EnableRateLimiting("password")]
    [ProducesResponseType(204), ProducesResponseType<ProblemDetails>(400), ProducesResponseType<ProblemDetails>(401), ProducesResponseType<ProblemDetails>(429)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.ChangePasswordAsync(CurrentUserId, request.CurrentPassword, request.NewPassword, cancellationToken);
        return result.Error is { } error ? Failure(error) : NoContent();
    }

    [HttpDelete, EnableRateLimiting("password")]
    [ProducesResponseType(204), ProducesResponseType<ProblemDetails>(400), ProducesResponseType<ProblemDetails>(401), ProducesResponseType<ProblemDetails>(429)]
    public async Task<IActionResult> Deactivate([FromBody] DeactivateAccountRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.DeactivateAsync(CurrentUserId, request.CurrentPassword, cancellationToken);
        return result.Error is { } error ? Failure(error) : NoContent();
    }
}
