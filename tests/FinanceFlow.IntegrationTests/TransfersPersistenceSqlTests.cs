using System.Net;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransfersPersistenceSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Incremental_migration_preserves_5b_users_accounts_transactions_versions_and_creation_snapshots()
    {
        var database = await DatabaseAsync("20261117000100_TransactionsDelivery5A");
        using var factory = new SqlApiFactory(fixture, database.Application);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client, 12.34m);
        using var transaction = await IncomeExpenseAsync(client, account.Id, "income", 0.01m, "before-6a");
        var originalBody = await transaction.Content.ReadAsStringAsync(Cancellation);
        using var before = await client.GetAsync("/health/ready", Cancellation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);

        await DatabaseTasks.MigrateAsync(database.Migration, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, database.Migration, database.Application, Cancellation);

        using var accountGet = await client.GetAsync(account.Location, Cancellation);
        using var transactionGet = await client.GetAsync(transaction.Headers.Location, Cancellation);
        using var profile = await client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(account.Version, accountGet.Headers.ETag!.ToString());
        Assert.Equal(transaction.Headers.ETag, transactionGet.Headers.ETag);
        Assert.Equal(12.35m, await BalanceAsync(client, account.Id));
        await using var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(database.Application).Options);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync(Cancellation)).Count());
        var snapshot = await db.IdempotencyRecords.AsNoTracking().SingleAsync(value => value.Key == "before-6a" && value.Operation == "transactions.create.v1", Cancellation);
        Assert.Equal(originalBody, snapshot.ResponseBody);
        using var ready = await client.GetAsync("/health/ready", Cancellation);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Restricted_api_preserves_financial_fields_and_real_transfer_columns_use_exact_types()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 1m, new(2000, 1, 1), null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using (var permissions = connection.CreateCommand())
        {
            permissions.CommandText = """
                SELECT HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','SELECT'), HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','INSERT'),
                  HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','UPDATE'), HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','DELETE'),
                  HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','ALTER'), HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','UPDATE','UserId','COLUMN'),
                  HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','UPDATE','CreatedAtUtc','COLUMN'), HAS_PERMS_BY_NAME('dbo.Transfers','OBJECT','UPDATE','RowVersion','COLUMN')
                """;
            await using var reader = await permissions.ExecuteReaderAsync(Cancellation);
            Assert.True(await reader.ReadAsync(Cancellation));
            Assert.Equal(new[] { 1, 1, 0, 0, 0, 0, 0, 0 }, Enumerable.Range(0, 8).Select(reader.GetInt32));
        }
        foreach (var sql in new[] { "UPDATE dbo.Transfers SET Amount=Amount WHERE Id=@id", "UPDATE dbo.Transfers SET UserId=UserId WHERE Id=@id", "DELETE FROM dbo.Transfers WHERE Id=@id" })
        {
            await using var denied = connection.CreateCommand();
            denied.CommandText = sql;
            denied.Parameters.AddWithValue("@id", id);
            Assert.Equal(sql.StartsWith("UPDATE", StringComparison.Ordinal) ? 230 : 229,
                (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync(Cancellation))).Number);
        }
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT name, TYPE_NAME(user_type_id), precision, scale FROM sys.columns WHERE object_id=OBJECT_ID('dbo.Transfers')";
            await using var reader = await schema.ExecuteReaderAsync(Cancellation);
            var types = new Dictionary<string, string>();
            while (await reader.ReadAsync(Cancellation))
            {
                types[reader.GetString(0)] = reader.GetString(1);
                if (reader.GetString(0) == "Amount")
                {
                    Assert.Equal((byte)18, reader.GetByte(2));
                    Assert.Equal((byte)2, reader.GetByte(3));
                }
            }
            Assert.Equal("date", types["OccurredOn"]);
            Assert.Equal("datetimeoffset", types["CreatedAtUtc"]);
            Assert.Equal("timestamp", types["RowVersion"]);
            Assert.DoesNotContain("Kind", types.Keys);
            Assert.DoesNotContain("CategoryId", types.Keys);
        }
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, Cancellation);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("maximum")]
    [InlineData("foreign-source")]
    [InlineData("foreign-destination")]
    [InlineData("cancellation")]
    public async Task Sql_constraints_prevent_invalid_values_accounts_ownership_and_cancellation_pairs(string invalid)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        using var stranger = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        var foreign = await AccountAsync(stranger);
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO dbo.Transfers (Id,UserId,SourceAccountId,DestinationAccountId,Amount,OccurredOn,IsCanceled,CreatedAtUtc)
            VALUES (@id,@owner,@source,@destination,@amount,'2000-01-01',@canceled,'2000-01-01T12:00:00+00:00')
            """;
        insert.Parameters.AddWithValue("@id", Guid.NewGuid());
        insert.Parameters.AddWithValue("@owner", owner);
        insert.Parameters.AddWithValue("@source", invalid == "foreign-source" ? foreign.Id : source.Id);
        insert.Parameters.AddWithValue("@destination", invalid == "same" ? source.Id : invalid == "foreign-destination" ? foreign.Id : destination.Id);
        insert.Parameters.AddWithValue("@amount", invalid switch { "zero" => 0m, "negative" => -1m, "maximum" => 1000000000000m, _ => 1m });
        insert.Parameters.AddWithValue("@canceled", invalid == "cancellation");
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => insert.ExecuteNonQueryAsync(Cancellation))).Number);
    }

    [Fact]
    public async Task Canceled_fixture_rows_remove_both_effects_and_readiness_requires_select_on_transfers()
    {
        var database = await DatabaseAsync();
        using var factory = new SqlApiFactory(fixture, database.Application);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client, 100m);
        var destination = await AccountAsync(client, -25m);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(90m, await BalanceAsync(client, source.Id));
        Assert.Equal(-15m, await BalanceAsync(client, destination.Id));
        // Fixture-only state preparation. There is no cancellation endpoint or UPDATE grant in 6A.
        await using var connection = new SqlConnection(database.Migration);
        await connection.OpenAsync(Cancellation);
        await using var cancel = connection.CreateCommand();
        cancel.CommandText = "UPDATE dbo.Transfers SET IsCanceled=1,CanceledAtUtc='2000-01-02T12:00:00+00:00' WHERE Id=@id";
        cancel.Parameters.AddWithValue("@id", (await ReadAsync(created)).GetProperty("id").GetGuid());
        Assert.Equal(1, await cancel.ExecuteNonQueryAsync(Cancellation));
        Assert.Equal(100m, await BalanceAsync(client, source.Id));
        Assert.Equal(-25m, await BalanceAsync(client, destination.Id));
        // Changing another principal's permissions requires the administrative fixture identity,
        // not the migrator's schema/DML permissions. Both connections use this disposable database.
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            InitialCatalog = new SqlConnectionStringBuilder(database.Migration).InitialCatalog
        };
        await using var adminConnection = new SqlConnection(admin.ConnectionString);
        await adminConnection.OpenAsync(Cancellation);
        await using var deny = adminConnection.CreateCommand();
        deny.CommandText = "DENY SELECT ON OBJECT::dbo.Transfers TO FinanceFlowApi";
        await deny.ExecuteNonQueryAsync(Cancellation);
        using var ready = await client.GetAsync("/health/ready", Cancellation);
        using var live = await client.GetAsync("/health/live", Cancellation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Theory]
    [InlineData("GRANT UPDATE ON OBJECT::dbo.Transfers (Amount) TO FinanceFlowApi")]
    [InlineData("GRANT DELETE ON OBJECT::dbo.Transfers TO FinanceFlowApi")]
    [InlineData("GRANT SELECT ON OBJECT::dbo.Transfers TO FinanceFlowApi WITH GRANT OPTION")]
    public async Task Verifier_rejects_extra_permissions_and_delegation(string sql)
    {
        var database = await DatabaseAsync();
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            InitialCatalog = new SqlConnectionStringBuilder(database.Migration).InitialCatalog
        };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Cancellation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseTasks.VerifyApplicationPrivilegesAsync(database.Application, Cancellation));
    }

    private async Task<(string Migration, string Application)> DatabaseAsync(string? target = null)
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString) { InitialCatalog = "FinanceFlow6A_" + Guid.NewGuid().ToString("N") };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString) { InitialCatalog = migration.InitialCatalog };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        await using var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(migration.ConnectionString).Options);
        await db.GetService<IMigrator>().MigrateAsync(target, Cancellation);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
        return (migration.ConnectionString, application.ConnectionString);
    }
}
