using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransactionMutationPermissionsSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Api_has_only_needed_column_updates_and_cannot_change_identity_creation_delete_or_schema()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        var allowed = new[] { "AccountId", "CategoryId", "Kind", "Amount", "OccurredOn", "Description", "UpdatedAtUtc", "IsDeleted", "DeletedAtUtc" };
        var immutable = new[] { "Id", "UserId", "CreatedAtUtc" };
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Cancellation);
        foreach (var column in allowed.Concat(immutable).Append("RowVersion"))
        {
            await using var permission = connection.CreateCommand();
            permission.CommandText = "SELECT HAS_PERMS_BY_NAME('dbo.FinancialTransactions','OBJECT','UPDATE',@column,'COLUMN')";
            permission.Parameters.AddWithValue("@column", column);
            Assert.Equal(allowed.Contains(column) ? 1 : 0, Convert.ToInt32(await permission.ExecuteScalarAsync(Cancellation)));
        }
        foreach (var column in allowed.Concat(immutable))
        {
            await using var update = connection.CreateCommand();
            update.CommandText = $"UPDATE dbo.FinancialTransactions SET [{column}]=[{column}] WHERE Id=@id";
            update.Parameters.AddWithValue("@id", id);
            if (allowed.Contains(column))
            {
                Assert.Equal(1, await update.ExecuteNonQueryAsync(Cancellation));
            }
            else
            {
                var denied = await Assert.ThrowsAsync<SqlException>(() => update.ExecuteNonQueryAsync(Cancellation));
                Assert.Equal(230, denied.Number);
            }
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM dbo.FinancialTransactions WHERE Id=@id";
            delete.Parameters.AddWithValue("@id", id);
            var denied = await Assert.ThrowsAsync<SqlException>(() => delete.ExecuteNonQueryAsync(Cancellation));
            Assert.Equal(229, denied.Number);
        }
        foreach (var sql in new[] { "ALTER TABLE dbo.FinancialTransactions ADD ForbiddenColumn int NULL", "CREATE TABLE dbo.Forbidden5B (Id int)", "UPDATE dbo.IdempotencyRecords SET ResponseBody=ResponseBody" })
        {
            await using var denied = connection.CreateCommand();
            denied.CommandText = sql;
            await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync(Cancellation));
        }
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, Cancellation);
    }

    [Theory]
    [InlineData("UserId", false)]
    [InlineData("CreatedAtUtc", false)]
    [InlineData("Amount", true)]
    public async Task Verifier_rejects_extra_immutable_column_grants_and_delegation(string column, bool delegation)
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        {
            InitialCatalog = "FinanceFlow5BPrivileges_" + Guid.NewGuid().ToString("N")
        };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString) { InitialCatalog = migration.InitialCatalog };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString) { InitialCatalog = migration.InitialCatalog };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var grant = connection.CreateCommand();
        grant.CommandText = $"GRANT UPDATE ON OBJECT::dbo.FinancialTransactions ({column}) TO FinanceFlowApi" + (delegation ? " WITH GRANT OPTION" : "");
        await grant.ExecuteNonQueryAsync(Cancellation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseTasks.VerifyApplicationPrivilegesAsync(application.ConnectionString, Cancellation));
    }
}
