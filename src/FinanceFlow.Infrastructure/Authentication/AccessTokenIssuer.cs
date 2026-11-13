using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FinanceFlow.Application.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FinanceFlow.Infrastructure.Authentication;

public sealed class AccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    public AccessToken Issue(Guid userId, long tokenVersion)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience,
        [
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new Claim("tokenVersion", tokenVersion.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        ], now.UtcDateTime, now.AddMinutes(15).UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(settings.SigningKeyBase64)), SecurityAlgorithms.HmacSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", 900);
    }
}
