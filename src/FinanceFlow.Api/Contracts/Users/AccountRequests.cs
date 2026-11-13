using System.ComponentModel.DataAnnotations;

namespace FinanceFlow.Api.Contracts.Users;

public sealed class RegisterRequest
{
    [Required, StringLength(100)]
    public string DisplayName { get; init; } = string.Empty;
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init; } = string.Empty;
    [Required, StringLength(128, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;
}

public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; init; } = string.Empty;
    [Required, StringLength(128, MinimumLength = 12)]
    public string Password { get; init; } = string.Empty;
}

public sealed class UpdateProfileRequest
{
    [Required, StringLength(100)]
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class ChangePasswordRequest
{
    [Required, StringLength(128, MinimumLength = 12)]
    public string CurrentPassword { get; init; } = string.Empty;
    [Required, StringLength(128, MinimumLength = 12)]
    public string NewPassword { get; init; } = string.Empty;
}

public sealed class DeactivateAccountRequest
{
    [Required, StringLength(128, MinimumLength = 12)]
    public string CurrentPassword { get; init; } = string.Empty;
}
