using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace FinanceFlow.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public string ConnectionString { get; init; } = string.Empty;
}

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        try
        {
            var connection = new SqlConnectionStringBuilder(options.ConnectionString);
            if (string.IsNullOrWhiteSpace(connection.DataSource) || string.IsNullOrWhiteSpace(connection.InitialCatalog)
                || string.IsNullOrWhiteSpace(connection.UserID) || string.IsNullOrEmpty(connection.Password)
                || connection.UserID.Equals("sa", StringComparison.OrdinalIgnoreCase) || connection.IntegratedSecurity
                || connection.PersistSecurityInfo)
                return ValidateOptionsResult.Fail("Database:ConnectionString must specify a dedicated SQL application account and database, with Persist Security Info disabled.");
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException)
        {
            return ValidateOptionsResult.Fail("Database:ConnectionString is invalid.");
        }
    }
}
