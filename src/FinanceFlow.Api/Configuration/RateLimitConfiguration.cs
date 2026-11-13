using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using FinanceFlow.Api.Errors;
using Microsoft.AspNetCore.RateLimiting;

namespace FinanceFlow.Api.Configuration;

internal static class RateLimitConfiguration
{
    public static IServiceCollection AddApiRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                return subject is null ? RateLimitPartition.GetNoLimiter("anonymous")
                    : Window(subject, 120, TimeSpan.FromMinutes(1));
            });
            options.AddPolicy("register", context => Window(Ip(context), 3, TimeSpan.FromMinutes(1)));
            options.AddPolicy("login", context => Window(Ip(context), 5, TimeSpan.FromMinutes(1)));
            options.AddPolicy("password", context => Window(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? Ip(context), 5, TimeSpan.FromMinutes(15)));
            options.OnRejected = async (context, cancellationToken) =>
            {
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                    ? Math.Max(1, (int)Math.Ceiling(value.TotalSeconds)) : 60;
                context.HttpContext.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
                await ApiProblemDetails.WriteAsync(context.HttpContext, 429);
            };
        });
        return services;
    }

    private static string Ip(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    private static RateLimitPartition<string> Window(string key, int limit, TimeSpan duration) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit, Window = duration, QueueLimit = 0, AutoReplenishment = true
        });
}
