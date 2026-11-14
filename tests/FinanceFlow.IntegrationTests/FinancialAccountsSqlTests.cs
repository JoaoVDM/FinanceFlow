using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class FinancialAccountsSqlTests(IdentitySqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Created_account_has_safe_contract_and_real_rowversion()
    {
        using var factory = new SqlApiFactory(fixture); using var client = factory.CreateClient();
        var owner = await Authenticate(client);
        using var created = await Create(client, "same-name", "one");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = await Read(created);
        Assert.Equal("same-name", account.Name); Assert.Equal("BRL", account.Currency); Assert.True(account.IsActive);
        Assert.Equal(TimeSpan.Zero, account.CreatedAtUtc.Offset);
        Assert.Equal(8, Convert.FromBase64String(account.Version).Length);
        Assert.Equal('"' + account.Version + '"', created.Headers.ETag?.ToString());
        Assert.Equal("/api/v1/accounts/" + account.Id, created.Headers.Location?.OriginalString);
        var body = await created.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("userId", body); Assert.DoesNotContain("rowVersion", body); Assert.DoesNotContain("currentBalance", body);
        using var get = await client.GetAsync(created.Headers.Location, Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(created.Headers.ETag, get.Headers.ETag);
        Assert.Equal(body, await get.Content.ReadAsStringAsync(Ct));
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        var persisted = await db.FinancialAccounts.AsNoTracking().SingleAsync(x => x.Id == account.Id, Ct);
        Assert.Equal(owner, persisted.UserId);
        using var duplicateName = await Create(client, "same-name", "two");
        Assert.Equal(HttpStatusCode.Created, duplicateName.StatusCode);
    }

    [Fact]
    public async Task Replay_preserves_original_bytes_location_and_etag_after_later_database_change()
    {
        using var factory = new SqlApiFactory(fixture); using var client = factory.CreateClient();
        await Authenticate(client);
        using var first = await Create(client, "Original", "replay", "10.000");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var originalBody = await first.Content.ReadAsStringAsync(Ct); var account = await Read(first);
        await using (var connection = new SqlConnection(fixture.MigrationConnectionString))
        {
            await connection.OpenAsync(Ct); await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dbo.FinancialAccounts SET Name=N'Later', IsActive=0 WHERE Id=@id";
            command.Parameters.AddWithValue("@id", account.Id); await command.ExecuteNonQueryAsync(Ct);
        }
        // Same command with another property order and equivalent decimal representation.
        using var replay = await Send(client, "replay", "{\"openingDate\":\"2000-01-01\",\"openingBalance\":10.00,\"currency\":\"BRL\",\"type\":\"checking\",\"name\":\"Original\"}");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(first.Headers.Location, replay.Headers.Location); Assert.Equal(first.Headers.ETag, replay.Headers.ETag);
        using var current = await client.GetAsync(first.Headers.Location, Ct);
        Assert.Equal("Later", (await Read(current)).Name); Assert.NotEqual(first.Headers.ETag, current.Headers.ETag);
        using var conflict = await Create(client, "Different", "replay");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Namespaces_and_reads_are_isolated_between_two_real_users()
    {
        using var factory = new SqlApiFactory(fixture); using var a = factory.CreateClient(); using var b = factory.CreateClient();
        await Authenticate(a); await Authenticate(b);
        using var createdA = await Create(a, "A", "Shared"); using var createdB = await Create(b, "B", "Shared");
        Assert.Equal(HttpStatusCode.Created, createdA.StatusCode); Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);
        using var foreign = await b.GetAsync(createdA.Headers.Location, Ct);
        using var absent = await b.GetAsync("/api/v1/accounts/" + Guid.NewGuid(), Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode); Assert.Equal(absent.StatusCode, foreign.StatusCode);
        using var foreignProblem = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync(Ct));
        using var absentProblem = JsonDocument.Parse(await absent.Content.ReadAsStringAsync(Ct));
        Assert.Equal(foreignProblem.RootElement.GetProperty("title").GetString(), absentProblem.RootElement.GetProperty("title").GetString());
        var pageA = await a.GetFromJsonAsync<AccountPage>("/api/v1/accounts", Ct);
        var pageB = await b.GetFromJsonAsync<AccountPage>("/api/v1/accounts", Ct);
        Assert.Equal("A", Assert.Single(pageA!.Items).Name); Assert.Equal(1, pageA.TotalCount);
        Assert.Equal("B", Assert.Single(pageB!.Items).Name); Assert.Equal(1, pageB.TotalCount);
        using var caseKey = await Create(a, "Separate", "shared");
        Assert.Equal(HttpStatusCode.Created, caseKey.StatusCode);
    }

    [Fact]
    public async Task Paging_filters_and_inactive_default_are_applied_on_owner_query()
    {
        using var factory = new SqlApiFactory(fixture); using var client = factory.CreateClient(); await Authenticate(client);
        var ids = new List<Guid>();
        foreach (var name in new[] { "Z", "A", "A", "B" })
        { using var response = await Create(client, name, Guid.NewGuid().ToString("N")); ids.Add((await Read(response)).Id); }
        await using (var connection = new SqlConnection(fixture.MigrationConnectionString))
        {
            await connection.OpenAsync(Ct); await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dbo.FinancialAccounts SET IsActive=0, Type='cash' WHERE Id=@id";
            command.Parameters.AddWithValue("@id", ids[0]); await command.ExecuteNonQueryAsync(Ct);
        }
        var page = await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts?pageSize=2", Ct);
        Assert.Equal(4, page!.TotalCount); Assert.Equal(2, page.TotalPages);
        Assert.All(page.Items, x => Assert.Equal("A", x.Name));
        var again = await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts?pageSize=2", Ct);
        Assert.Equal(page.Items.Select(x => x.Id), again!.Items.Select(x => x.Id));
        var inactive = await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts?isActive=false&type=cash", Ct);
        Assert.Equal(ids[0], Assert.Single(inactive!.Items).Id); Assert.Equal(1, inactive.TotalCount);
        var empty = await client.GetFromJsonAsync<AccountPage>("/api/v1/accounts?page=99", Ct);
        Assert.Empty(empty!.Items); Assert.Equal(4, empty.TotalCount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("123.45")]
    [InlineData("-123.45")]
    [InlineData("999999999999.99")]
    [InlineData("-999999999999.99")]
    [InlineData("10.000")]
    public async Task Exact_monetary_boundaries_round_trip_in_sql(string value)
    {
        using var factory = new SqlApiFactory(fixture); using var client = factory.CreateClient(); await Authenticate(client);
        using var response = await Create(client, "Money", "boundary", value);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var get = await client.GetAsync(response.Headers.Location, Ct);
        Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), (await Read(get)).OpeningBalance);
    }

    [Theory]
    [InlineData("10.001")]
    [InlineData("1000000000000")]
    [InlineData("-1000000000000")]
    [InlineData("10.00000000000000000000000000000000000000001")]
    public async Task Invalid_original_amount_does_not_write_any_record(string value)
    {
        using var factory = new SqlApiFactory(fixture); using var client = factory.CreateClient(); var owner = await Authenticate(client);
        using var response = await Create(client, "Invalid", "invalid", value);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertCounts(factory, owner, 0);
    }

    [Fact]
    public async Task Invalid_contracts_and_keys_are_rejected_before_persistence()
    {
        using var factory = new SqlApiFactory(fixture); using var client = factory.CreateClient(); var owner = await Authenticate(client);
        foreach (var json in new[] { "{}", Payload().Replace("checking", "Checking"), Payload().Replace("BRL", "USD"),
            Payload().Replace("Account", ""), Payload().Replace("2000-01-01", "2999-01-01"),
            Payload().Replace("\"name\":", "\"userId\":\"" + Guid.NewGuid() + "\",\"name\":") })
        { using var response = await Send(client, "invalid", json); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
        foreach (var key in new string?[] { null, "", "has space", new string('a', 129) })
        { using var response = await Send(client, key, Payload()); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
        foreach (var query in new[] { "page=0", "pageSize=101", "isActive=invalid", "type=creditCard", "sort=other", "direction=sideways" })
        { using var response = await client.GetAsync("/api/v1/accounts?" + query, Ct); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }
        await AssertCounts(factory, owner, 0);
    }

    [Fact]
    public async Task Simultaneous_identical_keys_create_one_account_and_replay_identically()
    {
        var gate = new InitialLookupGate(); using var factory = new InstrumentedFactory(fixture, gate); using var client = factory.CreateClient();
        var owner = await Authenticate(client); gate.Arm();
        var responses = await Task.WhenAll(Create(client, "Concurrent", "concurrent"), Create(client, "Concurrent", "concurrent"));
        try
        {
            Assert.All(responses, x => Assert.Equal(HttpStatusCode.Created, x.StatusCode));
            Assert.Equal(await responses[0].Content.ReadAsStringAsync(Ct), await responses[1].Content.ReadAsStringAsync(Ct));
            Assert.Equal(responses[0].Headers.ETag, responses[1].Headers.ETag);
            Assert.Equal(responses[0].Headers.Location, responses[1].Headers.Location);
            await AssertCounts(factory, owner, 1);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Simultaneous_different_payloads_with_one_key_have_one_winner()
    {
        var gate = new InitialLookupGate(); using var factory = new InstrumentedFactory(fixture, gate); using var client = factory.CreateClient();
        var owner = await Authenticate(client); gate.Arm();
        var responses = await Task.WhenAll(Create(client, "First", "race"), Create(client, "Second", "race"));
        try { Assert.Equal(new[] { 201, 409 }, responses.Select(x => (int)x.StatusCode).Order().ToArray()); await AssertCounts(factory, owner, 1); }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Violation_of_another_unique_constraint_is_not_replayed()
    {
        using var factory = new SqlApiFactory(fixture); using var client = factory.CreateClient(); var owner = await Authenticate(client);
        using var first = await Create(client, "Existing", "existing"); var existing = await Read(first);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IFinancialAccountStore>();
        var collision = FinanceFlow.Domain.Accounts.FinancialAccount.Create(existing.Id, owner, "Collision", "cash", "BRL", 0m,
            existing.OpeningDate, new FinancialCalendar().DateAt(DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(collision, "new-key",
            AccountCreationFingerprint.Compute(collision), Ct));
        await AssertCounts(factory, owner, 1);
    }

    [Fact]
    public async Task Failure_after_real_account_insert_rolls_back_and_key_can_be_retried()
    {
        var fault = new RecordInsertFault(); using var factory = new InstrumentedFactory(fixture, fault); using var client = factory.CreateClient();
        var owner = await Authenticate(client);
        using var failed = await Create(client, "Rollback", "rollback");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(fault.AccountInsertExecuted); await AssertCounts(factory, owner, 0);
        Assert.DoesNotContain("injected-record-failure", await failed.Content.ReadAsStringAsync(Ct));
        using var retry = await Create(client, "Rollback", "rollback");
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode); await AssertCounts(factory, owner, 1);
    }

    [Fact]
    public async Task Incremental_migration_upgrades_identity_database_and_preserves_users()
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString) { InitialCatalog = "FinanceFlowUpgrade_" + Guid.NewGuid().ToString("N") };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString) { InitialCatalog = migration.InitialCatalog };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Ct);
        await using (var before = new FinanceFlowDbContext(new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(migration.ConnectionString).Options))
            await before.GetService<IMigrator>().MigrateAsync("20261113000100_InitialIdentity", Ct);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Ct);
        using var factory = new SqlApiFactory(fixture, application.ConnectionString); using var client = factory.CreateClient();
        var owner = await Authenticate(client);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Ct);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Ct);
        using var profile = await client.GetAsync("/api/v1/users/me", Ct); Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using var created = await Create(client, "Upgraded", "upgrade"); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertCounts(factory, owner, 1);
    }

    [Fact]
    public async Task Privilege_verifier_rejects_unexpected_financial_update_in_disposable_database()
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        { InitialCatalog = "FinanceFlowExtraAccountGrant_" + Guid.NewGuid().ToString("N") };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        { InitialCatalog = migration.InitialCatalog };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Ct);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, Ct);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Ct);
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString) { InitialCatalog = migration.InitialCatalog };
        await using var connection = new SqlConnection(admin.ConnectionString); await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "GRANT UPDATE ON OBJECT::dbo.FinancialAccounts TO FinanceFlowApi";
        await command.ExecuteNonQueryAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseTasks.VerifyApplicationPrivilegesAsync(application.ConnectionString, Ct));
    }

    [Fact]
    public async Task Sql_schema_constraints_and_restricted_permissions_are_effective()
    {
        using var factory = new SqlApiFactory(fixture); using var client = factory.CreateClient(); var owner = await Authenticate(client);
        using var response = await Create(client, "Schema", "schema"); var id = (await Read(response)).Id;
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, Ct);
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString); await connection.OpenAsync(Ct);
        foreach (var table in new[] { "FinancialAccounts", "IdempotencyRecords" })
        {
            await using var permissions = connection.CreateCommand();
            permissions.CommandText = "SELECT HAS_PERMS_BY_NAME(@object,'OBJECT','SELECT'), HAS_PERMS_BY_NAME(@object,'OBJECT','INSERT'), HAS_PERMS_BY_NAME(@object,'OBJECT','UPDATE'), HAS_PERMS_BY_NAME(@object,'OBJECT','DELETE')";
            permissions.Parameters.AddWithValue("@object", "dbo." + table);
            await using var reader = await permissions.ExecuteReaderAsync(Ct); Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal(1, reader.GetInt32(0)); Assert.Equal(1, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2)); Assert.Equal(0, reader.GetInt32(3));
        }
        await using (var denied = connection.CreateCommand())
        {
            denied.CommandText = "UPDATE dbo.FinancialAccounts SET OpeningBalance=1 WHERE Id=@id"; denied.Parameters.AddWithValue("@id", id);
            await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync(Ct));
        }
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20261114000100_AccountsDeliveryA", await db.Database.GetAppliedMigrationsAsync(Ct));
        await using var admin = new SqlConnection(fixture.MigrationConnectionString); await admin.OpenAsync(Ct);
        foreach (var change in new[] { "Currency='USD'", "Type='Checking'", "OpeningBalance=1000000000000", "Name=N' '" })
        {
            await using var invalid = admin.CreateCommand(); invalid.CommandText = "UPDATE dbo.FinancialAccounts SET " + change + " WHERE Id=@id";
            invalid.Parameters.AddWithValue("@id", id);
            var error = await Assert.ThrowsAsync<SqlException>(() => invalid.ExecuteNonQueryAsync(Ct)); Assert.Equal(547, error.Number);
        }
        await using (var foreignOwner = admin.CreateCommand())
        {
            foreignOwner.CommandText = "UPDATE dbo.FinancialAccounts SET UserId=@missing WHERE Id=@id";
            foreignOwner.Parameters.AddWithValue("@missing", Guid.NewGuid()); foreignOwner.Parameters.AddWithValue("@id", id);
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => foreignOwner.ExecuteNonQueryAsync(Ct))).Number);
        }
        await using (var deleteOwner = admin.CreateCommand())
        {
            deleteOwner.CommandText = "DELETE FROM dbo.AspNetUsers WHERE Id=@owner";
            deleteOwner.Parameters.AddWithValue("@owner", owner);
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => deleteOwner.ExecuteNonQueryAsync(Ct))).Number);
        }
        await using (var invalidKey = admin.CreateCommand())
        {
            invalidKey.CommandText = "INSERT INTO dbo.IdempotencyRecords SELECT @id,UserId,Operation,'bad key',RequestHash,StatusCode,ResponseBody,Location,ETag,ContentType,CreatedAtUtc FROM dbo.IdempotencyRecords WHERE UserId=@owner";
            invalidKey.Parameters.AddWithValue("@id", Guid.NewGuid()); invalidKey.Parameters.AddWithValue("@owner", owner);
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => invalidKey.ExecuteNonQueryAsync(Ct))).Number);
        }
        await using var metadata = admin.CreateCommand();
        metadata.CommandText = "SELECT TYPE_NAME(user_type_id), precision, scale FROM sys.columns WHERE object_id=OBJECT_ID('dbo.FinancialAccounts') AND name='OpeningBalance'";
        await using var columns = await metadata.ExecuteReaderAsync(Ct); Assert.True(await columns.ReadAsync(Ct));
        Assert.Equal("decimal", columns.GetString(0)); Assert.Equal(18, columns.GetByte(1)); Assert.Equal(2, columns.GetByte(2));
    }

    private static async Task<Guid> Authenticate(HttpClient client)
    {
        var (_, _, token) = await IdentitySqlTests.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Subject);
    }
    private static string Payload(string name = "Account", string value = "0") =>
        "{\"name\":" + JsonSerializer.Serialize(name) + ",\"type\":\"checking\",\"currency\":\"BRL\",\"openingBalance\":" + value + ",\"openingDate\":\"2000-01-01\"}";
    private static Task<HttpResponseMessage> Create(HttpClient client, string name, string key, string value = "0") => Send(client, key, Payload(name, value));
    private static async Task<HttpResponseMessage> Send(HttpClient client, string? key, string payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request, Ct);
    }
    private static async Task<FinancialAccountView> Read(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<FinancialAccountView>(Ct))!;
    private static async Task AssertCounts(ApiFactory factory, Guid owner, int expected)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Equal(expected, await db.FinancialAccounts.CountAsync(x => x.UserId == owner, Ct));
        Assert.Equal(expected, await db.IdempotencyRecords.CountAsync(x => x.UserId == owner, Ct));
    }
    private sealed class InstrumentedFactory(IdentitySqlFixture fixture, DbCommandInterceptor interceptor) : ApiFactory()
    {
        public override string ConnectionString => fixture.ApplicationConnectionString;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(interceptor)));
        }
    }
    private sealed class InitialLookupGate : DbCommandInterceptor
    {
        private int remaining;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => remaining = 2;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [dbo].[IdempotencyRecords]", StringComparison.Ordinal) && Volatile.Read(ref remaining) > 0)
            {
                if (Interlocked.Decrement(ref remaining) == 0) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
    private sealed class RecordInsertFault : DbCommandInterceptor
    {
        private int fail = 1;
        public bool AccountInsertExecuted { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [dbo].[FinancialAccounts]", StringComparison.Ordinal)) AccountInsertExecuted = true;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [dbo].[IdempotencyRecords]", StringComparison.Ordinal) && Interlocked.Exchange(ref fail, 0) == 1)
                throw new InvalidOperationException("injected-record-failure");
            return ValueTask.FromResult(result);
        }
    }
}
