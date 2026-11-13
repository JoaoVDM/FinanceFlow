namespace FinanceFlow.Application.Users;

public interface IUserAccounts
{
    Task<AccountResult> RegisterAsync(RegisterAccount input, CancellationToken cancellationToken);
    Task<AccountResult> LoginAsync(LoginAccount input, CancellationToken cancellationToken);
    Task<AccountResult> GetProfileAsync(Guid userId, CancellationToken cancellationToken);
    Task<AccountResult> UpdateProfileAsync(Guid userId, string displayName, CancellationToken cancellationToken);
    Task<AccountResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken);
    Task<AccountResult> LogoutAsync(Guid userId, CancellationToken cancellationToken);
    Task<AccountResult> DeactivateAsync(Guid userId, string currentPassword, CancellationToken cancellationToken);
}

public interface IUserTokenState
{
    Task<bool> IsCurrentAsync(Guid userId, long tokenVersion, CancellationToken cancellationToken);
}

public sealed record RegisterAccount(string DisplayName, string Email, string Password);
public sealed record LoginAccount(string Email, string Password);
public sealed record UserProfile(Guid Id, string DisplayName, string Email);
public sealed record AccessToken(string Token, string TokenType, int ExpiresIn);
public enum AccountError { InvalidInput, InvalidCredentials, Conflict, NotFound, ConcurrentChange }
public sealed record AccountResult(UserProfile? Profile = null, AccessToken? AccessToken = null, AccountError? Error = null);
