using FinanceFlow.Application.Users;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Categories;
using FinanceFlow.Application.Transactions;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Infrastructure.Persistence.Transfers;
using FinanceFlow.Infrastructure.Persistence.Transactions;
using FinanceFlow.Infrastructure.Persistence.Categories;
using FinanceFlow.Infrastructure.Persistence.Accounts;
using FinanceFlow.Infrastructure.Authentication;
using FinanceFlow.Infrastructure.Configuration;
using FinanceFlow.Infrastructure.Identity;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FinanceFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<FinancialCalendar>();
        services.AddScoped<FinancialAccountsService>();
        services.AddScoped<IFinancialAccountStore, FinancialAccountStore>();
        services.AddScoped<CategoriesService>();
        services.AddScoped<ICategoryStore, CategoryStore>();
        services.AddScoped<FinancialTransactionsService>();
        services.AddScoped<IFinancialTransactionStore, FinancialTransactionStore>();
        services.AddScoped<TransfersService>();
        services.AddScoped<ITransferStore, TransferStore>();
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName)).ValidateOnStart();
        services.AddDbContext<FinanceFlowDbContext>((provider, options) =>
            options.UseSqlServer(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString,
                sql => sql.CommandTimeout(15)));
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Password.RequiredUniqueChars = 1;
            options.Password.RequireUppercase = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireDigit = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            // Keep the supported Identity v1 relational schema; no passkey feature in this stage.
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;
            options.Stores.MaxLengthForKeys = 128;
        }).AddEntityFrameworkStores<FinanceFlowDbContext>().AddPasswordValidator<PasswordLengthValidator>();
        services.AddScoped<IUserAccounts, UserAccounts>();
        services.AddScoped<IUserTokenState, UserTokenState>();
        services.AddScoped<AccessTokenIssuer>();
        services.AddSingleton<UnknownCredentialVerifier>();
        return services;
    }
}
