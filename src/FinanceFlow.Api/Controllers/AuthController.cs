using FinanceFlow.Api.Contracts.Users;
using FinanceFlow.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FinanceFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/auth"), RequestSizeLimit(16_384)]
public sealed class AuthController(IUserAccounts accounts) : AccountControllerBase
{
    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("register")]
    [ProducesResponseType<UserProfile>(201), ProducesResponseType<ProblemDetails>(400), ProducesResponseType<ProblemDetails>(409), ProducesResponseType<ProblemDetails>(429)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.RegisterAsync(new(request.DisplayName, request.Email, request.Password), cancellationToken);
        return result.Error is { } error ? Failure(error) : Created("/api/v1/users/me", result.Profile);
    }

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    [ProducesResponseType(200), ProducesResponseType<ProblemDetails>(400), ProducesResponseType<ProblemDetails>(401), ProducesResponseType<ProblemDetails>(429)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.LoginAsync(new(request.Email, request.Password), cancellationToken);
        if (result.Error is { } error) return Failure(error);
        var token = result.AccessToken!;
        return Ok(new { accessToken = token.Token, tokenType = token.TokenType, expiresIn = token.ExpiresIn });
    }

    [HttpPost("logout")]
    [ProducesResponseType(204), ProducesResponseType<ProblemDetails>(401)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var result = await accounts.LogoutAsync(CurrentUserId, cancellationToken);
        return result.Error is { } error ? Failure(error) : NoContent();
    }
}
