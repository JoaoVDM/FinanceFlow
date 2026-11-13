using System.Security.Cryptography;
using FinanceFlow.Infrastructure.Authentication;
using FinanceFlow.Infrastructure.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class IdentityConfigurationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    public void Signing_key_requires_32_decoded_bytes(int length)
    {
        var result = new JwtOptionsValidator().Validate(null, new JwtOptions
        {
            Issuer = "tests", Audience = "tests", SigningKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length))
        });
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("Jwt:SigningKeyBase64", "invalid-base64")]
    [InlineData("Jwt:Issuer", "")]
    [InlineData("Jwt:Audience", "")]
    [InlineData("Database:ConnectionString", "")]
    public void Required_security_configuration_is_validated_at_startup(string key, string value)
    {
        using var root = new ApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public void Runtime_configuration_rejects_sa()
    {
        var result = new DatabaseOptionsValidator().Validate(null, new DatabaseOptions
        {
            ConnectionString = "Server=localhost;Database=FinanceFlow;User ID=sa;Password=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        });
        Assert.True(result.Failed);
    }
}
