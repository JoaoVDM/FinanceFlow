using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FinanceFlow.Application.Users;
using Microsoft.AspNetCore.Mvc;

namespace FinanceFlow.Api.Controllers;

public abstract class AccountControllerBase : ControllerBase
{
    protected Guid CurrentUserId => Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

    protected IActionResult Failure(AccountError error)
    {
        var status = error switch
        {
            AccountError.InvalidCredentials => 401,
            AccountError.InvalidInput => 400,
            AccountError.NotFound => 404,
            _ => 409
        };
        if (status == 401) Response.Headers.WWWAuthenticate = "Bearer";
        return Problem(statusCode: status, title: status switch
        {
            401 => "Invalid credentials.", 400 => "Invalid request.",
            404 => "Resource not found.", _ => "The request conflicts with the current state."
        });
    }
}
