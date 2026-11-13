using FinanceFlow.Application.Users;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Identity;

public sealed class UserTokenState(FinanceFlowDbContext db) : IUserTokenState
{
    public Task<bool> IsCurrentAsync(Guid userId, long tokenVersion, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().AnyAsync(user => user.Id == userId && user.IsActive && user.TokenVersion == tokenVersion, cancellationToken);
}
