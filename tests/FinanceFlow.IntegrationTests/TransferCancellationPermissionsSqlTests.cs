using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransferCancellationPermissionsSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Api_can_update_only_cancellation_columns_and_cannot_modify_financial_fields_or_delete()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null));
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        var allowed = new[] { "IsCanceled", "CanceledAtUtc", "UpdatedAtUtc" };
        var immutable = new[] { "Id", "UserId", "SourceAccountId", "DestinationAccountId", "Amount", "OccurredOn", "Description", "CreatedAtUtc" };
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Cancellation);

        foreach (var column in allowed.Concat(immutable).Append("RowVersion"))
        {
            await using var permission = connection.CreateCommand();
            permission.CommandText = "SELECT HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','UPDATE',@column,'COLUMN')";
            permission.Parameters.AddWithValue("@column", column);
            Assert.Equal(allowed.Contains(column) ? 1 : 0, Convert.ToInt32(await permission.ExecuteScalarAsync(Cancellation)));
        }
        foreach (var column in allowed.Concat(immutable))
        {
            await using var update = connection.CreateCommand();
            update.CommandText = $"UPDATE dbo.Transfers SET [{column}]=[{column}] WHERE Id=@id";
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
        await using (var permission = connection.CreateCommand())
        {
            permission.CommandText = "SELECT HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','UPDATE'), HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','DELETE'), HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','ALTER'), HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','CONTROL')";
            await using var reader = await permission.ExecuteReaderAsync(Cancellation);
            Assert.True(await reader.ReadAsync(Cancellation));
            Assert.Equal(new[] { 0, 0, 0, 0 }, Enumerable.Range(0, 4).Select(reader.GetInt32));
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM dbo.Transfers WHERE Id=@id";
            delete.Parameters.AddWithValue("@id", id);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => delete.ExecuteNonQueryAsync(Cancellation))).Number);
        }
        foreach (var sql in new[] { "ALTER TABLE dbo.Transfers ADD Forbidden6B int NULL", "CREATE TABLE dbo.Forbidden6B (Id int)",
            "UPDATE dbo.IdempotencyRecords SET ResponseBody=ResponseBody", "DELETE FROM dbo.IdempotencyRecords" })
        {
            await using var denied = connection.CreateCommand();
            denied.CommandText = sql;
            await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync(Cancellation));
        }
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, Cancellation);
    }

    [Theory]
    [InlineData("GRANT UPDATE ON OBJECT::dbo.Transfers TO FinanceFlowApi")]
    [InlineData("GRANT UPDATE ON OBJECT::dbo.Transfers (IsCanceled) TO FinanceFlowApi WITH GRANT OPTION")]
    [InlineData("GRANT CONTROL ON OBJECT::dbo.Transfers TO FinanceFlowApi")]
    public async Task Verifier_rejects_table_updates_delegation_and_control(string sql)
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        {
            InitialCatalog = "FinanceFlow6BPrivileges_" + Guid.NewGuid().ToString("N")
        };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString) { InitialCatalog = migration.InitialCatalog };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(application.ConnectionString, Cancellation);
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString) { InitialCatalog = migration.InitialCatalog };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var grant = connection.CreateCommand();
        grant.CommandText = sql;
        await grant.ExecuteNonQueryAsync(Cancellation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseTasks.VerifyApplicationPrivilegesAsync(application.ConnectionString, Cancellation));
    }
}
