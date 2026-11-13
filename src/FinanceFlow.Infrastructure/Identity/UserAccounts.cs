using System.Data;
using FinanceFlow.Application.Users;
using FinanceFlow.Infrastructure.Authentication;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Identity;

public sealed class UserAccounts(
    FinanceFlowDbContext db, UserManager<ApplicationUser> users,
    AccessTokenIssuer tokenIssuer, UnknownCredentialVerifier unknownCredentials,
    TimeProvider timeProvider) : IUserAccounts
{
    public async Task<AccountResult> RegisterAsync(RegisterAccount input, CancellationToken cancellationToken)
    {
        if (!AccountInputRules.IsDisplayNameValid(input.DisplayName) || !AccountInputRules.IsEmailValid(input.Email)
            || !AccountInputRules.IsPasswordValid(input.Password)) return new(Error: AccountError.InvalidInput);
        var email = input.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null) return new(Error: AccountError.Conflict);
        var id = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = id, UserName = id.ToString("N"), Email = email, DisplayName = input.DisplayName.Trim(),
            IsActive = true, TokenVersion = 0, CreatedAtUtc = timeProvider.GetUtcNow(), LockoutEnabled = true
        };
        try
        {
            var result = await users.CreateAsync(user, input.Password);
            if (!result.Succeeded)
                return new(Error: result.Errors.Any(x => x.Code is "DuplicateEmail" or "DuplicateUserName")
                    ? AccountError.Conflict : AccountError.InvalidInput);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Concurrent registration is decided by the unique database index.
            db.ChangeTracker.Clear();
            if (await users.FindByEmailAsync(email) is not null) return new(Error: AccountError.Conflict);
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Profile: Profile(user));
    }

    public async Task<AccountResult> LoginAsync(LoginAccount input, CancellationToken cancellationToken)
    {
        if (!AccountInputRules.IsEmailValid(input.Email) || !AccountInputRules.IsPasswordValid(input.Password))
            return new(Error: AccountError.InvalidInput);
        var normalizedEmail = users.NormalizeEmail(input.Email.Trim());
        // Email is immutable in this API. Resolve the id, then lock by primary key in the same
        // order as password/logout/deactivation, avoiding opposite index-lock ordering.
        var userId = await db.Users.AsNoTracking().Where(x => x.NormalizedEmail == normalizedEmail)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (userId is null)
        {
            unknownCredentials.Verify(input.Password);
            return new(Error: AccountError.InvalidCredentials);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var user = await LockUserAsync(userId.Value, cancellationToken);
        if (user is null)
        {
            unknownCredentials.Verify(input.Password);
            return new(Error: AccountError.InvalidCredentials);
        }
        if (!user.IsActive || await users.IsLockedOutAsync(user)) return new(Error: AccountError.InvalidCredentials);
        if (!await users.CheckPasswordAsync(user, input.Password))
        {
            var failed = await users.AccessFailedAsync(user);
            if (!failed.Succeeded) return new(Error: AccountError.InvalidCredentials);
            await transaction.CommitAsync(cancellationToken);
            return new(Error: AccountError.InvalidCredentials);
        }
        var reset = await users.ResetAccessFailedCountAsync(user);
        if (!reset.Succeeded) return new(Error: AccountError.InvalidCredentials);
        // Capture the version of the credential that was verified, while holding the row lock.
        var token = tokenIssuer.Issue(user.Id, user.TokenVersion);
        await transaction.CommitAsync(cancellationToken);
        return new(AccessToken: token);
    }

    public async Task<AccountResult> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profile = await db.Users.AsNoTracking().Where(x => x.Id == userId && x.IsActive)
            .Select(x => new UserProfile(x.Id, x.DisplayName, x.Email!)).SingleOrDefaultAsync(cancellationToken);
        return profile is null ? new(Error: AccountError.NotFound) : new(Profile: profile);
    }

    public async Task<AccountResult> UpdateProfileAsync(Guid userId, string displayName, CancellationToken cancellationToken)
    {
        if (!AccountInputRules.IsDisplayNameValid(displayName)) return new(Error: AccountError.InvalidInput);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if (user is null || !user.IsActive) return new(Error: AccountError.NotFound);
        user.DisplayName = displayName.Trim();
        user.UpdatedAtUtc = timeProvider.GetUtcNow();
        if (!(await users.UpdateAsync(user)).Succeeded) return new(Error: AccountError.ConcurrentChange);
        await transaction.CommitAsync(cancellationToken);
        return new();
    }

    public async Task<AccountResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        if (!AccountInputRules.IsPasswordValid(currentPassword) || !AccountInputRules.IsPasswordValid(newPassword))
            return new(Error: AccountError.InvalidInput);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if (user is null || !user.IsActive) return new(Error: AccountError.NotFound);
        if (!(await users.ChangePasswordAsync(user, currentPassword, newPassword)).Succeeded)
            return new(Error: AccountError.InvalidInput);
        Revoke(user);
        if (!(await users.UpdateAsync(user)).Succeeded) return new(Error: AccountError.ConcurrentChange);
        await transaction.CommitAsync(cancellationToken);
        return new();
    }

    public async Task<AccountResult> LogoutAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if (user is null || !user.IsActive) return new(Error: AccountError.NotFound);
        Revoke(user);
        if (!(await users.UpdateAsync(user)).Succeeded) return new(Error: AccountError.ConcurrentChange);
        await transaction.CommitAsync(cancellationToken);
        return new();
    }

    public async Task<AccountResult> DeactivateAsync(Guid userId, string currentPassword, CancellationToken cancellationToken)
    {
        if (!AccountInputRules.IsPasswordValid(currentPassword)) return new(Error: AccountError.InvalidInput);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await LockUserAsync(userId, cancellationToken);
        if (user is null || !user.IsActive) return new(Error: AccountError.NotFound);
        if (!await users.CheckPasswordAsync(user, currentPassword)) return new(Error: AccountError.InvalidInput);
        user.IsActive = false;
        user.DeactivatedAtUtc = timeProvider.GetUtcNow();
        Revoke(user);
        if (!(await users.UpdateAsync(user)).Succeeded) return new(Error: AccountError.ConcurrentChange);
        await transaction.CommitAsync(cancellationToken);
        return new();
    }

    private Task<ApplicationUser?> LockUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.FromSqlInterpolated($"SELECT * FROM [dbo].[AspNetUsers] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {userId}")
            .SingleOrDefaultAsync(cancellationToken);

    private void Revoke(ApplicationUser user)
    {
        user.TokenVersion = checked(user.TokenVersion + 1);
        user.UpdatedAtUtc = timeProvider.GetUtcNow();
    }

    private static UserProfile Profile(ApplicationUser user) => new(user.Id, user.DisplayName, user.Email!);
}
