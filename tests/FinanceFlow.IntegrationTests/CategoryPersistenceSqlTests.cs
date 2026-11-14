using System.Net;
using System.Net.Http.Json;
using FinanceFlow.Application.Categories;
using FinanceFlow.Domain.Categories;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.CategoryTestRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class CategoryPersistenceSqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Incremental_migration_preserves_identity_accounts_tokens_and_prior_snapshot()
    {
        var database = await CreateDisposableDatabaseAsync("20261114000100_AccountsDeliveryA");
        using var factory = new SqlApiFactory(fixture, database.Application);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        using var createAccount = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts")
        {
            Content = JsonContent.Create(new { name = "Before categories", type = "cash", currency = "BRL", openingBalance = -10.25m, openingDate = "2000-01-01" })
        };
        createAccount.Headers.Add("Idempotency-Key", "before-categories");
        using var account = await client.SendAsync(createAccount, TestCancellationToken);
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        var originalAccountBody = await account.Content.ReadAsStringAsync(TestCancellationToken);
        using var notReady = await client.GetAsync("/health/ready", TestCancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);

        await DatabaseTasks.MigrateAsync(database.Migration, TestCancellationToken);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, database.Migration, database.Application, TestCancellationToken);

        using var profile = await client.GetAsync("/api/v1/users/me", TestCancellationToken);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using var preserved = await client.GetAsync(account.Headers.Location, TestCancellationToken);
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        Assert.Equal(originalAccountBody, await preserved.Content.ReadAsStringAsync(TestCancellationToken));
        Assert.Equal(account.Headers.ETag, preserved.Headers.ETag);
        using var category = await CreateAsync(client, "After migration");
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        using var ready = await client.GetAsync("/health/ready", TestCancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(new[] { "20261113000100_InitialIdentity", "20261114000100_AccountsDeliveryA", "20261114000200_CategoriesDeliveryB" },
            await db.Database.GetAppliedMigrationsAsync(TestCancellationToken));
    }

    [Fact]
    public async Task Category_permissions_and_actual_column_types_are_restricted()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        using var created = await CreateAsync(client, "Permissions");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var category = await ReadAsync(created);
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, TestCancellationToken);
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using (var permissions = connection.CreateCommand())
        {
            permissions.CommandText = """
                SELECT HAS_PERMS_BY_NAME('dbo.Categories','OBJECT','SELECT'),
                       HAS_PERMS_BY_NAME('dbo.Categories','OBJECT','INSERT'),
                       HAS_PERMS_BY_NAME('dbo.Categories','OBJECT','UPDATE'),
                       HAS_PERMS_BY_NAME('dbo.Categories','OBJECT','DELETE'),
                       HAS_PERMS_BY_NAME('dbo.Categories','OBJECT','ALTER')
                """;
            await using var reader = await permissions.ExecuteReaderAsync(TestCancellationToken);
            Assert.True(await reader.ReadAsync(TestCancellationToken));
            Assert.Equal(new[] { 1, 1, 0, 0, 0 }, Enumerable.Range(0, 5).Select(reader.GetInt32));
        }

        foreach (var sql in new[] { "UPDATE dbo.Categories SET Name=N'Not allowed' WHERE Id=@id", "DELETE FROM dbo.Categories WHERE Id=@id" })
        {
            await using var denied = connection.CreateCommand();
            denied.CommandText = sql;
            denied.Parameters.AddWithValue("@id", category.Id);

            var exception = await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync(TestCancellationToken));

            Assert.Equal(229, exception.Number);
        }

        await using var metadata = connection.CreateCommand();
        metadata.CommandText = """
            SELECT name, TYPE_NAME(user_type_id), max_length, collation_name
            FROM sys.columns WHERE object_id=OBJECT_ID('dbo.Categories') ORDER BY name
            """;
        await using var columns = await metadata.ExecuteReaderAsync(TestCancellationToken);
        var actual = new Dictionary<string, (string Type, short Length, string? Collation)>();
        while (await columns.ReadAsync(TestCancellationToken))
        {
            actual[columns.GetString(0)] = (columns.GetString(1), columns.GetInt16(2), columns.IsDBNull(3) ? null : columns.GetString(3));
        }

        Assert.Equal(("nvarchar", (short)200, "Latin1_General_100_BIN2"), actual["NormalizedName"]);
        Assert.Equal((short)200, actual["Name"].Length);
        Assert.Equal(("varchar", (short)7, "Latin1_General_100_BIN2"), actual["Kind"]);
        Assert.Equal("timestamp", actual["RowVersion"].Type);
        Assert.Equal((short)8, actual["RowVersion"].Length);
        Assert.Equal("datetimeoffset", actual["CreatedAtUtc"].Type);
        Assert.Equal("datetimeoffset", actual["UpdatedAtUtc"].Type);
    }

    [Fact]
    public async Task Constraints_reject_invalid_names_kind_fk_and_cascading_owner_deletion()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        using var response = await CreateAsync(client, "Constraints");
        var category = await ReadAsync(response);
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        foreach (var change in new[] { "Name=N' '", "NormalizedName=N''", "Kind='Income'", "Kind='income '", "Kind='other'", "UserId=NEWID()" })
        {
            await using var invalid = connection.CreateCommand();
            invalid.CommandText = "UPDATE dbo.Categories SET " + change + " WHERE Id=@id";
            invalid.Parameters.AddWithValue("@id", category.Id);

            var exception = await Assert.ThrowsAsync<SqlException>(() => invalid.ExecuteNonQueryAsync(TestCancellationToken));

            Assert.Equal(547, exception.Number);
        }

        await using var deleteOwner = connection.CreateCommand();
        deleteOwner.CommandText = "DELETE FROM dbo.AspNetUsers WHERE Id=@owner";
        deleteOwner.Parameters.AddWithValue("@owner", owner);
        var foreignKeyFailure = await Assert.ThrowsAsync<SqlException>(() => deleteOwner.ExecuteNonQueryAsync(TestCancellationToken));
        Assert.Equal(547, foreignKeyFailure.Number);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.True(await db.Categories.AnyAsync(record => record.Id == category.Id && record.UserId == owner, TestCancellationToken));
    }

    [Fact]
    public async Task Primary_key_and_foreign_key_failures_are_not_name_conflicts()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        using var created = await CreateAsync(client, "Existing");
        var existing = await ReadAsync(created);
        var candidates = new[]
        {
            Category.Create(existing.Id, owner, "Different name", "income", DateTimeOffset.UtcNow),
            Category.Create(Guid.NewGuid(), Guid.NewGuid(), "Missing owner", "expense", DateTimeOffset.UtcNow)
        };

        foreach (var candidate in candidates)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ICategoryStore>();

            await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(candidate, TestCancellationToken));
        }

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Equal(1, await db.Categories.CountAsync(category => category.UserId == owner, TestCancellationToken));
    }

    [Theory]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    [InlineData("ALTER")]
    public async Task Privilege_verifier_rejects_extra_category_grants_in_disposable_database(string permission)
    {
        var database = await CreateDisposableDatabaseAsync();
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            InitialCatalog = new SqlConnectionStringBuilder(database.Migration).InitialCatalog
        };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var grant = connection.CreateCommand();
        grant.CommandText = "GRANT " + permission + " ON OBJECT::dbo.Categories TO FinanceFlowApi";
        await grant.ExecuteNonQueryAsync(TestCancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseTasks.VerifyApplicationPrivilegesAsync(database.Application, TestCancellationToken));
    }

    [Fact]
    public async Task Readiness_requires_category_read_access_while_liveness_remains_healthy()
    {
        var database = await CreateDisposableDatabaseAsync();
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            InitialCatalog = new SqlConnectionStringBuilder(database.Migration).InitialCatalog
        };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var deny = connection.CreateCommand();
        deny.CommandText = "DENY SELECT ON OBJECT::dbo.Categories TO FinanceFlowApi";
        await deny.ExecuteNonQueryAsync(TestCancellationToken);
        using var factory = new SqlApiFactory(fixture, database.Application);
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready", TestCancellationToken);
        using var live = await client.GetAsync("/health/live", TestCancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("{\"status\":\"notReady\"}", await ready.Content.ReadAsStringAsync(TestCancellationToken));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    private async Task<(string Migration, string Application)> CreateDisposableDatabaseAsync(string? targetMigration = null)
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        {
            InitialCatalog = "FinanceFlowCategories_" + Guid.NewGuid().ToString("N")
        };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            InitialCatalog = migration.InitialCatalog
        };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, TestCancellationToken);
        if (targetMigration is null)
        {
            await DatabaseTasks.MigrateAsync(migration.ConnectionString, TestCancellationToken);
        }
        else
        {
            await using var db = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>()
                .UseSqlServer(migration.ConnectionString).Options);
            await db.GetService<IMigrator>().MigrateAsync(targetMigration, TestCancellationToken);
        }

        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, TestCancellationToken);
        return (migration.ConnectionString, application.ConnectionString);
    }
}
