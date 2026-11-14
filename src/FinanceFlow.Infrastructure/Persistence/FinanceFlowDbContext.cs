using FinanceFlow.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using FinanceFlow.Domain.Accounts;
using FinanceFlow.Infrastructure.Persistence.Accounts;

namespace FinanceFlow.Infrastructure.Persistence;

public sealed class FinanceFlowDbContext(DbContextOptions<FinanceFlowDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<FinancialAccount> FinancialAccounts => Set<FinancialAccount>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new FinancialAccountConfiguration());
        builder.ApplyConfiguration(new IdempotencyRecordConfiguration());
        builder.Entity<IdentityUserLogin<Guid>>().Property(x => x.LoginProvider).HasMaxLength(128);
        builder.Entity<IdentityUserLogin<Guid>>().Property(x => x.ProviderKey).HasMaxLength(128);
        builder.Entity<IdentityUserToken<Guid>>().Property(x => x.LoginProvider).HasMaxLength(128);
        builder.Entity<IdentityUserToken<Guid>>().Property(x => x.Name).HasMaxLength(128);
        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            user.Property(x => x.Email).HasMaxLength(256).IsRequired();
            user.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
            user.HasIndex(x => x.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique();
            user.Property(x => x.TokenVersion).IsConcurrencyToken();
            user.ToTable("AspNetUsers", table =>
            {
                table.HasCheckConstraint("CK_AspNetUsers_DisplayName", "LEN(LTRIM(RTRIM([DisplayName]))) > 0");
                table.HasCheckConstraint("CK_AspNetUsers_TokenVersion", "[TokenVersion] >= 0");
            });
        });
    }
}
