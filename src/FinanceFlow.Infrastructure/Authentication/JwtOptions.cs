using Microsoft.Extensions.Options;

namespace FinanceFlow.Infrastructure.Authentication;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string SigningKeyBase64 { get; init; } = string.Empty;
}

public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience))
            return ValidateOptionsResult.Fail("Jwt:Issuer and Jwt:Audience are required.");
        try
        {
            if (Convert.FromBase64String(options.SigningKeyBase64).Length < 32)
                return ValidateOptionsResult.Fail("Jwt signing key must contain at least 32 cryptographically random bytes, Base64 encoded.");
        }
        catch (FormatException)
        {
            return ValidateOptionsResult.Fail("Jwt signing key must be valid Base64.");
        }
        return ValidateOptionsResult.Success;
    }
}
