using FinanceFlow.Application.Users;
using Microsoft.AspNetCore.Identity;

namespace FinanceFlow.Infrastructure.Identity;

public sealed class PasswordLengthValidator : IPasswordValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password) =>
        Task.FromResult(AccountInputRules.IsPasswordValid(password) ? IdentityResult.Success :
            IdentityResult.Failed(new IdentityError { Code = "PasswordLength", Description = "Password must contain 12 to 128 characters." }));
}
