using System.Net;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransactionsPersistenceSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Incremental_migration_preserves_existing_identity_accounts_and_categories()
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        {
            InitialCatalog = "FinanceFlow5A_" + Guid.NewGuid().ToString("N")
        };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString) { InitialCatalog = migration.InitialCatalog };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await using (var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(migration.ConnectionString).Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261114000200_CategoriesDeliveryB", Cancellation);
        }
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        using var factory = new SqlApiFactory(fixture, application.ConnectionString);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client, -15.25m);
        using var notReady = await client.GetAsync("/health/ready", Cancellation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);

        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);

        using var profile = await client.GetAsync("/api/v1/users/me", Cancellation);
        using var account = await client.GetAsync(references.AccountLocation, Cancellation);
        using var category = await client.GetAsync(references.CategoryLocation, Cancellation);
        using var ready = await client.GetAsync("/health/ready", Cancellation);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        Assert.Equal(HttpStatusCode.OK, category.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(references.AccountVersion, account.Headers.ETag!.ToString());
        Assert.Equal(references.CategoryVersion, category.Headers.ETag!.ToString());
        using var created = await CreateAsync(client, references);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(-5.25m, await BalanceAsync(client, references.AccountId));
        await using var scope = factory.Services.CreateAsyncScope();
        var current = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.False(current.Database.HasPendingModelChanges());
        Assert.Equal(4, (await current.Database.GetAppliedMigrationsAsync(Cancellation)).Count());
    }

    [Fact]
    public async Task Restricted_account_has_only_select_insert_and_real_sql_types_and_checks()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, Cancellation);
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using (var permission = connection.CreateCommand())
        {
            permission.CommandText = "SELECT HAS_PERMS_BY_NAME('dbo.FinancialTransactions','OBJECT','SELECT'), HAS_PERMS_BY_NAME('dbo.FinancialTransactions','OBJECT','INSERT'), HAS_PERMS_BY_NAME('dbo.FinancialTransactions','OBJECT','UPDATE'), HAS_PERMS_BY_NAME('dbo.FinancialTransactions','OBJECT','DELETE'), HAS_PERMS_BY_NAME('dbo.FinancialTransactions','OBJECT','ALTER')";
            await using var reader = await permission.ExecuteReaderAsync(Cancellation);
            Assert.True(await reader.ReadAsync(Cancellation));
            Assert.Equal(new[] { 1, 1, 0, 0, 0 }, Enumerable.Range(0, 5).Select(reader.GetInt32));
        }
        var forbidden = new[]
        {
            (Sql: "UPDATE dbo.FinancialTransactions SET CreatedAtUtc=CreatedAtUtc WHERE Id=@id", Error: 230),
            (Sql: "DELETE FROM dbo.FinancialTransactions WHERE Id=@id", Error: 229)
        };
        foreach (var operation in forbidden)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = operation.Sql;
            command.Parameters.AddWithValue("@id", id);
            var denied = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Cancellation));
            Assert.Equal(operation.Error, denied.Number);
        }
        await using var metadata = connection.CreateCommand();
        metadata.CommandText = "SELECT name, TYPE_NAME(user_type_id), precision, scale FROM sys.columns WHERE object_id=OBJECT_ID('dbo.FinancialTransactions')";
        await using var columns = await metadata.ExecuteReaderAsync(Cancellation);
        var actual = new Dictionary<string, (string Type, byte Precision, byte Scale)>();
        while (await columns.ReadAsync(Cancellation)) actual[columns.GetString(0)] = (columns.GetString(1), columns.GetByte(2), columns.GetByte(3));
        Assert.Equal(("decimal", (byte)18, (byte)2), actual["Amount"]);
        Assert.Equal("date", actual["OccurredOn"].Type);
        Assert.Equal("timestamp", actual["RowVersion"].Type);
        Assert.Equal("datetimeoffset", actual["CreatedAtUtc"].Type);
    }

    [Fact]
    public async Task Database_rejects_invalid_amount_kind_owner_and_reference_relationships()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        using var otherOwner = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(otherOwner);
        var references = await ReferencesAsync(client);
        var foreignReferences = await ReferencesAsync(otherOwner);
        using var created = await CreateAsync(client, references);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(Cancellation);
        foreach (var change in new[] { "Amount=0", "Amount=1000000000000", "Kind='Income'", "Kind='expense'", "AccountId=NEWID()", "CategoryId=NEWID()", "UserId=NEWID()", "IsDeleted=1" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dbo.FinancialTransactions SET " + change + " WHERE Id=@id";
            command.Parameters.AddWithValue("@id", id);
            var rejected = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Cancellation));
            Assert.Equal(547, rejected.Number);
        }
        // Both principals exist: these denials specifically prove the composite owner FKs,
        // rather than relying only on a nonexistent UserId or a missing reference.
        foreach (var reference in new[] { (Column: "AccountId", Id: foreignReferences.AccountId), (Column: "CategoryId", Id: foreignReferences.CategoryId) })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE dbo.FinancialTransactions SET {reference.Column}=@foreignId WHERE Id=@id";
            command.Parameters.AddWithValue("@foreignId", reference.Id);
            command.Parameters.AddWithValue("@id", id);
            var rejected = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Cancellation));
            Assert.Equal(547, rejected.Number);
            Assert.Contains(reference.Column, rejected.Message);
        }
        Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
    }

    [Fact]
    public async Task Deleted_rows_are_excluded_from_reads_and_all_balances_without_an_api_delete_operation()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.FinancialTransactions SET IsDeleted=1, DeletedAtUtc=SYSUTCDATETIME() WHERE Id=@id";
        command.Parameters.AddWithValue("@id", (await ReadAsync(created)).GetProperty("id").GetGuid());
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Cancellation));
        using var individual = await client.GetAsync(created.Headers.Location, Cancellation);
        using var list = await client.GetAsync("/api/v1/transactions", Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, individual.StatusCode);
        Assert.Equal(0, (await ReadAsync(list)).GetProperty("totalCount").GetInt32());
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId));
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId, "2000-01-01"));
    }

    [Fact]
    public async Task Readiness_requires_transaction_table_read_access_but_liveness_does_not()
    {
        // Use a distinct database so the denial never affects another fixture test.
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString) { InitialCatalog = "FinanceFlow5AReadiness_" + Guid.NewGuid().ToString("N") };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString) { InitialCatalog = migration.InitialCatalog };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString) { InitialCatalog = migration.InitialCatalog };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var deny = connection.CreateCommand();
        deny.CommandText = "DENY SELECT ON OBJECT::dbo.FinancialTransactions TO FinanceFlowApi";
        await deny.ExecuteNonQueryAsync(Cancellation);
        using var factory = new SqlApiFactory(fixture, application.ConnectionString);
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready", Cancellation);
        using var live = await client.GetAsync("/health/live", Cancellation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("{\"status\":\"notReady\"}", await ready.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        await using var grant = connection.CreateCommand();
        grant.CommandText = "GRANT UPDATE ON OBJECT::dbo.FinancialTransactions TO FinanceFlowApi";
        await grant.ExecuteNonQueryAsync(Cancellation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseTasks.VerifyApplicationPrivilegesAsync(application.ConnectionString, Cancellation));
    }
}
