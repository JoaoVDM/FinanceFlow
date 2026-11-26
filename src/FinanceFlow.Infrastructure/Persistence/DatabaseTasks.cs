using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FinanceFlow.Infrastructure.Persistence;

public static class DatabaseTasks
{
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnectionStringBuilder(connectionString);
        if (!Regex.IsMatch(connection.InitialCatalog, "^FinanceFlow[A-Za-z0-9_]*$")
            || string.IsNullOrWhiteSpace(connection.UserID) || connection.UserID.Equals("sa", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use a dedicated migration account and a FinanceFlow database.");
        var options = new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(connectionString).Options;
        await using var db = new FinanceFlowDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);
    }

    public static async Task ProvisionAsync(string adminConnectionString, string migrationConnectionString,
        string applicationConnectionString, CancellationToken cancellationToken = default)
    {
        var admin = new SqlConnectionStringBuilder(adminConnectionString);
        var migration = new SqlConnectionStringBuilder(migrationConnectionString);
        var application = new SqlConnectionStringBuilder(applicationConnectionString);
        if (!Regex.IsMatch(migration.InitialCatalog, "^FinanceFlow[A-Za-z0-9_]*$")
            || migration.InitialCatalog != application.InitialCatalog || migration.UserID == application.UserID
            || !admin.DataSource.Equals(migration.DataSource, StringComparison.OrdinalIgnoreCase)
            || !admin.DataSource.Equals(application.DataSource, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Provisioning requires a FinanceFlow database and separate accounts on the same server.");
        foreach (var identity in new[] { migration, application })
            if (!Regex.IsMatch(identity.UserID, "^[A-Za-z][A-Za-z0-9_]{0,63}$") || identity.UserID.Equals("sa", StringComparison.OrdinalIgnoreCase)
                || identity.Password.Length < 20 || identity.IntegratedSecurity)
                throw new InvalidOperationException("Dedicated SQL accounts with externally supplied strong passwords are required.");

        admin.InitialCatalog = "master";
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "IF DB_ID(@database) IS NULL BEGIN DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@database); EXEC(@sql); END";
            create.Parameters.AddWithValue("@database", migration.InitialCatalog);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        // Administrative, explicitly invoked provisioning only. API startup and the restricted
        // application login never change database options; READ_COMMITTED_SNAPSHOT stays unchanged.
        await using (var snapshot = connection.CreateCommand())
        {
            snapshot.CommandText = """
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name=@database AND snapshot_isolation_state<>1)
                BEGIN
                  DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET ALLOW_SNAPSHOT_ISOLATION ON';
                  EXEC(@sql);
                END
                """;
            snapshot.Parameters.AddWithValue("@database", migration.InitialCatalog);
            await snapshot.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var identity in new[] { migration, application })
        {
            await connection.ChangeDatabaseAsync("master", cancellationToken);
            await using var login = connection.CreateCommand();
            login.CommandText = """
                IF SUSER_ID(@username) IS NULL
                BEGIN
                  DECLARE @sql nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@username)
                    + N' WITH PASSWORD = N''' + REPLACE(@password, '''', '''''') + N''', CHECK_POLICY = ON';
                  EXEC(@sql);
                END
                """;
            login.Parameters.AddWithValue("@username", identity.UserID);
            login.Parameters.AddWithValue("@password", identity.Password);
            await login.ExecuteNonQueryAsync(cancellationToken);
            await connection.ChangeDatabaseAsync(migration.InitialCatalog, cancellationToken);
            await using var user = connection.CreateCommand();
            user.CommandText = """
                IF DATABASE_PRINCIPAL_ID(@username) IS NULL
                BEGIN
                  DECLARE @sql nvarchar(max) = N'CREATE USER ' + QUOTENAME(@username) + N' FOR LOGIN ' + QUOTENAME(@username);
                  EXEC(@sql);
                END
                ELSE IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @username AND sid = SUSER_SID(@username))
                  THROW 50001, 'Existing database user is mapped to a different login.', 1;
                DECLARE @grant nvarchar(max);
                IF @migrator = 1
                BEGIN
                  SET @grant = N'GRANT SELECT, INSERT, UPDATE, DELETE, ALTER, REFERENCES ON SCHEMA::dbo TO ' + QUOTENAME(@username)
                    + N'; GRANT CREATE TABLE TO ' + QUOTENAME(@username);
                  EXEC(@grant);
                END
                ELSE
                BEGIN
                  IF OBJECT_ID(N'dbo.AspNetUsers') IS NOT NULL
                  BEGIN
                    SET @grant = N'GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.AspNetUsers TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                  END
                  IF OBJECT_ID(N'dbo.__EFMigrationsHistory') IS NOT NULL
                  BEGIN
                    SET @grant = N'GRANT SELECT ON OBJECT::dbo.__EFMigrationsHistory TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                  END
                  IF OBJECT_ID(N'dbo.FinancialAccounts') IS NOT NULL
                  BEGIN
                    SET @grant = N'GRANT SELECT, INSERT ON OBJECT::dbo.FinancialAccounts TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                    SET @grant = N'GRANT UPDATE ON OBJECT::dbo.FinancialAccounts (Name, Type, IsActive, UpdatedAtUtc) TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                  END
                  IF OBJECT_ID(N'dbo.IdempotencyRecords') IS NOT NULL
                  BEGIN
                    SET @grant = N'GRANT SELECT, INSERT ON OBJECT::dbo.IdempotencyRecords TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                  END
                  IF OBJECT_ID(N'dbo.Categories') IS NOT NULL
                  BEGIN
                    SET @grant = N'GRANT SELECT, INSERT ON OBJECT::dbo.Categories TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                    SET @grant = N'GRANT UPDATE ON OBJECT::dbo.Categories (Name, NormalizedName, IsActive, UpdatedAtUtc) TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                  END
                  IF OBJECT_ID(N'dbo.FinancialTransactions') IS NOT NULL
                  BEGIN
                    SET @grant = N'GRANT SELECT, INSERT ON OBJECT::dbo.FinancialTransactions TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                    SET @grant = N'GRANT UPDATE ON OBJECT::dbo.FinancialTransactions (AccountId, CategoryId, Kind, Amount, OccurredOn, Description, UpdatedAtUtc, IsDeleted, DeletedAtUtc) TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                  END
                  IF OBJECT_ID(N'dbo.Transfers') IS NOT NULL
                  BEGIN
                    SET @grant = N'GRANT SELECT, INSERT ON OBJECT::dbo.Transfers TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                    SET @grant = N'GRANT UPDATE ON OBJECT::dbo.Transfers (IsCanceled, CanceledAtUtc, UpdatedAtUtc) TO ' + QUOTENAME(@username);
                    EXEC(@grant);
                  END
                END
                """;
            user.Parameters.AddWithValue("@username", identity.UserID);
            user.Parameters.AddWithValue("@migrator", identity.UserID == migration.UserID);
            await user.ExecuteNonQueryAsync(cancellationToken);
        }
        // Existing credentials/privileges are never reset silently. Reject an overprivileged API account.
        await VerifyApplicationPrivilegesAsync(applicationConnectionString, cancellationToken);
    }

    public static async Task VerifyApplicationPrivilegesAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var check = connection.CreateCommand();
        check.CommandText = """
            SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1 OR IS_MEMBER('db_owner') = 1
              OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1
              OR HAS_PERMS_BY_NAME('dbo', 'SCHEMA', 'ALTER') = 1
              OR EXISTS (SELECT 1 FROM sys.server_role_members WHERE member_principal_id = SUSER_ID())
              OR EXISTS (SELECT 1 FROM sys.database_role_members WHERE member_principal_id = DATABASE_PRINCIPAL_ID())
              OR EXISTS (SELECT 1 FROM sys.schemas WHERE principal_id = DATABASE_PRINCIPAL_ID())
              OR EXISTS (
                SELECT 1 FROM sys.server_permissions
                WHERE grantee_principal_id = SUSER_ID()
                  AND (state = 'W' OR (state = 'G' AND permission_name <> 'CONNECT SQL')))
              OR EXISTS (
                SELECT 1 FROM sys.database_permissions
                WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID()
                  AND (state = 'W' OR (state = 'G' AND NOT (
                    (class = 0 AND permission_name = 'CONNECT')
                    OR (class = 1 AND minor_id = 0
                      AND major_id = COALESCE(OBJECT_ID('dbo.AspNetUsers'), -1)
                      AND permission_name IN ('SELECT', 'INSERT', 'UPDATE'))
                    OR (class = 1 AND minor_id = 0
                      AND major_id IN (COALESCE(OBJECT_ID('dbo.FinancialAccounts'), -1), COALESCE(OBJECT_ID('dbo.IdempotencyRecords'), -1), COALESCE(OBJECT_ID('dbo.Categories'), -1), COALESCE(OBJECT_ID('dbo.FinancialTransactions'), -1), COALESCE(OBJECT_ID('dbo.Transfers'), -1))
                      AND permission_name IN ('SELECT', 'INSERT'))
                    OR (class = 1 AND minor_id = 0
                      AND major_id = COALESCE(OBJECT_ID('dbo.__EFMigrationsHistory'), -1)
                      AND permission_name = 'SELECT')
                    OR (class = 1 AND minor_id > 0 AND permission_name = 'UPDATE'
                      AND (
                        (major_id = COALESCE(OBJECT_ID('dbo.FinancialAccounts'), -1)
                          AND COL_NAME(major_id, minor_id) IN ('Name', 'Type', 'IsActive', 'UpdatedAtUtc'))
                        OR (major_id = COALESCE(OBJECT_ID('dbo.Categories'), -1)
                          AND COL_NAME(major_id, minor_id) IN ('Name', 'NormalizedName', 'IsActive', 'UpdatedAtUtc'))
                        OR (major_id = COALESCE(OBJECT_ID('dbo.FinancialTransactions'), -1)
                          AND COL_NAME(major_id, minor_id) IN ('AccountId', 'CategoryId', 'Kind', 'Amount', 'OccurredOn', 'Description', 'UpdatedAtUtc', 'IsDeleted', 'DeletedAtUtc'))
                        OR (major_id = COALESCE(OBJECT_ID('dbo.Transfers'), -1)
                          AND COL_NAME(major_id, minor_id) IN ('IsCanceled', 'CanceledAtUtc', 'UpdatedAtUtc'))))
                  ))))
              THEN 1 ELSE 0 END
            """;
        if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) != 0)
            throw new InvalidOperationException("The application SQL account has excessive privileges.");
    }

    public static async Task<int> RunCommandAsync(string mode, IConfiguration configuration)
    {
        try
        {
            var migration = configuration["Migration:ConnectionString"] ?? throw new InvalidOperationException();
            if (mode == "--provision-database")
                await ProvisionAsync(configuration["Provisioning:AdminConnectionString"] ?? throw new InvalidOperationException(),
                    migration, configuration["Database:ConnectionString"] ?? throw new InvalidOperationException());
            else await MigrateAsync(migration);
            Console.WriteLine(mode == "--provision-database" ? "Database provisioned; application privileges checked." : "Migrations applied.");
            return 0;
        }
        catch
        {
            // Never print connection strings, passwords or raw SQL exceptions.
            Console.Error.WriteLine("Database operation failed. Check configuration, connectivity and permissions.");
            return 1;
        }
    }
}
