using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FinanceFlow.Infrastructure.Identity;

// Perform the framework's password verification even when an email is unknown.
public sealed class UnknownCredentialVerifier
{
    private readonly PasswordHasher<ApplicationUser> hasher;
    private readonly string hash;
    private readonly ApplicationUser user = new();

    public UnknownCredentialVerifier(IOptions<PasswordHasherOptions> options)
    {
        hasher = new PasswordHasher<ApplicationUser>(options);
        hash = hasher.HashPassword(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public void Verify(string password) => hasher.VerifyHashedPassword(user, hash, password);
}
