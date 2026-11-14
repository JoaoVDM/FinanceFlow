using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FinanceFlow.Application.Accounts;

namespace FinanceFlow.Api.Authentication;

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid UserId => accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user
        && Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : Guid.Empty;
}
