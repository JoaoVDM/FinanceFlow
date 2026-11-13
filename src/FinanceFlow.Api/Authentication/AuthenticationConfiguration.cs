using System.Data.Common;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FinanceFlow.Application.Users;
using FinanceFlow.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FinanceFlow.Api.Authentication;

internal static class AuthenticationConfiguration
{
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddAuthorization();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, settings) =>
            {
                var jwt = settings.Value;
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKeyBase64)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateIssuer = true, ValidIssuer = jwt.Issuer,
                    ValidateAudience = true, ValidAudience = jwt.Audience,
                    RequireExpirationTime = true, ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = JwtRegisteredClaimNames.Sub
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal!;
                        if (!Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)
                            || !long.TryParse(principal.FindFirstValue("tokenVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                            || version < 0 || string.IsNullOrWhiteSpace(principal.FindFirstValue(JwtRegisteredClaimNames.Jti))
                            || !long.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Nbf), CultureInfo.InvariantCulture, out _)
                            || !long.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Iat), CultureInfo.InvariantCulture, out _))
                        { context.Fail("Invalid token."); return; }
                        try
                        {
                            var state = context.HttpContext.RequestServices.GetRequiredService<IUserTokenState>();
                            if (!await state.IsCurrentAsync(userId, version, context.HttpContext.RequestAborted))
                                context.Fail("Invalid token.");
                        }
                        catch (Exception exception) when (exception is DbException or TimeoutException or InvalidOperationException)
                        {
                            // Database unavailability never bypasses authorization.
                            context.Fail("Authentication unavailable.");
                        }
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        await Errors.ApiProblemDetails.WriteAsync(context.HttpContext, 401);
                    },
                    OnForbidden = context => Errors.ApiProblemDetails.WriteAsync(context.HttpContext, 403)
                };
            });
        return services;
    }
}
