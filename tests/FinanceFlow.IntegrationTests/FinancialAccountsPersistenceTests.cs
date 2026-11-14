using System.Net;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed partial class FinancialAccountsSqlTests
{
    [Fact]
    public async Task Incremental_migration_upgrades_identity_database_and_preserves_users()
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        {
            InitialCatalog = "FinanceFlowUpgrade_" + Guid.NewGuid().ToString("N")
        };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            InitialCatalog = migration.InitialCatalog
        };
        await DatabaseTasks.ProvisionAsync(
            fixture.AdminConnectionString,
            migration.ConnectionString,
            application.ConnectionString,
            TestCancellationToken);
        await using (var before = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(migration.ConnectionString).Options))
        {
            await before.GetService<IMigrator>().MigrateAsync("20261113000100_InitialIdentity", TestCancellationToken);
        }

        await DatabaseTasks.ProvisionAsync(
            fixture.AdminConnectionString,
            migration.ConnectionString,
            application.ConnectionString,
            TestCancellationToken);
        using var factory = new SqlApiFactory(fixture, application.ConnectionString);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, TestCancellationToken);
        await DatabaseTasks.ProvisionAsync(
            fixture.AdminConnectionString,
            migration.ConnectionString,
            application.ConnectionString,
            TestCancellationToken);
        using var profile = await client.GetAsync("/api/v1/users/me", TestCancellationToken);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using var created = await CreateAccountAsync(client, "Upgraded", "upgrade");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertRecordCountsAsync(factory, owner, 1);
    }

    [Fact]
    public async Task Privilege_verifier_rejects_unexpected_financial_update_in_disposable_database()
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        {
            InitialCatalog = "FinanceFlowExtraAccountGrant_" + Guid.NewGuid().ToString("N")
        };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            InitialCatalog = migration.InitialCatalog
        };
        await DatabaseTasks.ProvisionAsync(
            fixture.AdminConnectionString,
            migration.ConnectionString,
            application.ConnectionString,
            TestCancellationToken);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, TestCancellationToken);
        await DatabaseTasks.ProvisionAsync(
            fixture.AdminConnectionString,
            migration.ConnectionString,
            application.ConnectionString,
            TestCancellationToken);
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            InitialCatalog = migration.InitialCatalog
        };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "GRANT UPDATE ON OBJECT::dbo.FinancialAccounts TO FinanceFlowApi";
        await command.ExecuteNonQueryAsync(TestCancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseTasks.VerifyApplicationPrivilegesAsync(
            application.ConnectionString,
            TestCancellationToken));
    }

    [Fact]
    public async Task Sql_schema_constraints_and_restricted_permissions_are_effective()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        using var response = await CreateAccountAsync(client, "Schema", "schema");
        var id = (await ReadAccountAsync(response)).Id;
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, TestCancellationToken);
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        foreach (var table in new[]
        {
            "FinancialAccounts",
            "IdempotencyRecords"
        })
        {
            await using var permissions = connection.CreateCommand();
            permissions.CommandText = "SELECT HAS_PERMS_BY_NAME(@object,'OBJECT','SELECT'), HAS_PERMS_BY_NAME(@object,'OBJECT','INSERT'), HAS_PERMS_BY_NAME(@object,'OBJECT','UPDATE'), HAS_PERMS_BY_NAME(@object,'OBJECT','DELETE')";
            permissions.Parameters.AddWithValue("@object", "dbo." + table);
            await using var reader = await permissions.ExecuteReaderAsync(TestCancellationToken);
            Assert.True(await reader.ReadAsync(TestCancellationToken));
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(0, reader.GetInt32(3));
        }

        await using (var denied = connection.CreateCommand())
        {
            denied.CommandText = "UPDATE dbo.FinancialAccounts SET OpeningBalance=1 WHERE Id=@id";
            denied.Parameters.AddWithValue("@id", id);
            await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync(TestCancellationToken));
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261114000100_AccountsDeliveryA", await db.Database.GetAppliedMigrationsAsync(TestCancellationToken));
        await using var admin = new SqlConnection(fixture.MigrationConnectionString);
        await admin.OpenAsync(TestCancellationToken);
        foreach (var change in new[]
        {
            "Currency='USD'",
            "Type='Checking'",
            "OpeningBalance=1000000000000",
            "Name=N' '"
        })
        {
            await using var invalid = admin.CreateCommand();
            invalid.CommandText = "UPDATE dbo.FinancialAccounts SET " + change + " WHERE Id=@id";
            invalid.Parameters.AddWithValue("@id", id);
            var error = await Assert.ThrowsAsync<SqlException>(() => invalid.ExecuteNonQueryAsync(TestCancellationToken));
            Assert.Equal(547, error.Number);
        }

        await using (var foreignOwner = admin.CreateCommand())
        {
            foreignOwner.CommandText = "UPDATE dbo.FinancialAccounts SET UserId=@missing WHERE Id=@id";
            foreignOwner.Parameters.AddWithValue("@missing", Guid.NewGuid());
            foreignOwner.Parameters.AddWithValue("@id", id);
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => foreignOwner.ExecuteNonQueryAsync(TestCancellationToken))).Number);
        }

        await using (var deleteOwner = admin.CreateCommand())
        {
            deleteOwner.CommandText = "DELETE FROM dbo.AspNetUsers WHERE Id=@owner";
            deleteOwner.Parameters.AddWithValue("@owner", owner);
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => deleteOwner.ExecuteNonQueryAsync(TestCancellationToken))).Number);
        }

        await using (var invalidKey = admin.CreateCommand())
        {
            invalidKey.CommandText = "INSERT INTO dbo.IdempotencyRecords SELECT @id,UserId,Operation,'bad key',RequestHash,StatusCode,ResponseBody,Location,ETag,ContentType,CreatedAtUtc FROM dbo.IdempotencyRecords WHERE UserId=@owner";
            invalidKey.Parameters.AddWithValue("@id", Guid.NewGuid());
            invalidKey.Parameters.AddWithValue("@owner", owner);
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => invalidKey.ExecuteNonQueryAsync(TestCancellationToken))).Number);
        }

        await using var metadata = admin.CreateCommand();
        metadata.CommandText = "SELECT TYPE_NAME(user_type_id), precision, scale FROM sys.columns WHERE object_id=OBJECT_ID('dbo.FinancialAccounts') AND name='OpeningBalance'";
        await using var columns = await metadata.ExecuteReaderAsync(TestCancellationToken);
        Assert.True(await columns.ReadAsync(TestCancellationToken));
        Assert.Equal("decimal", columns.GetString(0));
        Assert.Equal(18, columns.GetByte(1));
        Assert.Equal(2, columns.GetByte(2));
    }
}
