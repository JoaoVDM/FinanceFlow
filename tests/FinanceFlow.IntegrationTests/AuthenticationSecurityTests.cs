using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class AuthenticationSecurityTests
{
    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xml")]
    public async Task Exception_remains_sanitized_even_with_incompatible_accept_header(string accept)
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        using var response = await client.GetAsync("/api/v1/__tests/failure", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("sensitive-test-detail", body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

    [Fact]
    public async Task Missing_token_returns_401_with_challenge_and_problem_details()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/users/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Valid_signature_with_unavailable_database_fails_closed()
    {
        using var factory = new ApiFactory(); // Real SqlClient connection to an unavailable endpoint.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(factory, Guid.NewGuid(), 0));
        using var response = await client.GetAsync("/api/v1/users/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("login", 5)]
    [InlineData("register", 3)]
    public async Task Authentication_rate_limits_return_retry_after(string route, int limit)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        for (var attempt = 0; attempt < limit; attempt++)
        {
            using var invalid = await client.PostAsJsonAsync($"/api/v1/auth/{route}", new { }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var response = await client.PostAsJsonAsync($"/api/v1/auth/{route}", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Database_failure_affects_readiness_but_not_liveness()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("{\"status\":\"notReady\"}", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Plain_http_does_not_accept_credentials()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("http://localhost/api/v1/auth/login", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    internal static string CreateToken(ApiFactory factory, Guid userId, long version, string? variant = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>();
        if (variant != "missing-id") claims.Add(new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")));
        if (variant != "missing-issued-at") claims.Add(new(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
        if (variant != "missing-subject") claims.Add(new(JwtRegisteredClaimNames.Sub, userId.ToString()));
        if (variant != "missing-version") claims.Add(new("tokenVersion", version.ToString(), ClaimValueTypes.Integer64));
        var key = variant == "signature" ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(factory.TestKey);
        var token = new JwtSecurityToken(variant == "issuer" ? "wrong" : "FinanceFlow.Tests",
            variant == "audience" ? "wrong" : "FinanceFlow.Tests.Client", claims,
            variant == "missing-not-before" ? null : variant == "expired" ? now.AddMinutes(-30) : variant == "future" ? now.AddMinutes(5) : now.AddSeconds(-1),
            variant == "missing-expiration" ? null : variant == "expired" ? now.AddMinutes(-15) : now.AddMinutes(15),
            new SigningCredentials(new SymmetricSecurityKey(key), variant == "algorithm" ? SecurityAlgorithms.HmacSha384 : SecurityAlgorithms.HmacSha256));
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        if (variant != "tampered") return encoded;
        var parts = encoded.Split('.');
        parts[2] = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];
        return string.Join('.', parts);
    }
}
