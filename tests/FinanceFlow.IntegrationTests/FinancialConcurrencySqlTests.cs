using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using static FinanceFlow.IntegrationTests.FinancialChangeRequests;
using static FinanceFlow.IntegrationTests.FinancialMutationSqlTests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class FinancialConcurrencySqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("accounts", "PUT")]
    [InlineData("accounts", "PATCH")]
    [InlineData("categories", "PUT")]
    [InlineData("categories", "PATCH")]
    public async Task Two_independent_requests_read_the_same_version_and_only_one_update_commits(string resource, string method)
    {
        var observer = new UpdateObserver(resource);
        using var factory = new FinancialChangeApiFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var created = await CreateResourceAsync(client, resource);
        var version = created.Headers.ETag!.ToString();
        observer.ArmReadBarrier();

        var responses = await Task.WhenAll(
            ChangeAsync(client, created.Headers.Location!, method, Command(resource, method, "First", false), version),
            ChangeAsync(client, created.Headers.Location!, method, Command(resource, method, "Second", true), version));
        try
        {
            Assert.Equal(new[] { 204, 412 }, responses.Select(response => (int)response.StatusCode).Order());
            var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.NoContent);
            await AssertSuccessAsync(responses[winner]);
            await AssertProblemAsync(responses[1 - winner], 412, "precondition_failed");
            using var current = await client.GetAsync(created.Headers.Location, TestCancellationToken);
            Assert.Equal(responses[winner].Headers.ETag, current.Headers.ETag);
            var body = await BodyAsync(current);
            if (method == "PUT")
            {
                Assert.Equal(winner == 0 ? "First" : "Second", body.GetProperty("name").GetString());
            }
            else
            {
                Assert.Equal(winner == 1, body.GetProperty("isActive").GetBoolean());
            }

            Assert.Equal(2, observer.Versions.Count);
            Assert.All(observer.Versions, expected => Assert.Equal(Convert.FromBase64String(version.Trim('"')), expected));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("categories")]
    public async Task Stale_client_version_is_the_real_sql_predicate_even_after_loading_a_newer_row(string resource)
    {
        var observer = new UpdateObserver(resource);
        using var factory = new FinancialChangeApiFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var created = await CreateResourceAsync(client, resource);
        using var first = await ChangeAsync(client, created.Headers.Location!, "PUT", Command(resource, "PUT", "Changed"), created.Headers.ETag!.ToString());
        await AssertSuccessAsync(first);

        using var stale = await ChangeAsync(client, created.Headers.Location!, "PUT", Command(resource, "PUT", "Changed"), created.Headers.ETag!.ToString());

        await AssertProblemAsync(stale, 412, "precondition_failed");
        Assert.Equal(2, observer.Versions.Count);
        Assert.Equal(Convert.FromBase64String(created.Headers.ETag.Tag.Trim('"')), observer.Versions.Last());
        using var current = await client.GetAsync(created.Headers.Location, TestCancellationToken);
        Assert.Equal(first.Headers.ETag, current.Headers.ETag);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("categories")]
    public async Task Column_updates_are_allowed_but_immutable_fields_delete_and_ddl_are_denied(string resource)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        using var created = await CreateResourceAsync(client, resource);
        var id = (await BodyAsync(created)).GetProperty("id").GetGuid();
        var table = resource == "accounts" ? "FinancialAccounts" : "Categories";
        var allowed = resource == "accounts" ? new[] { "Name", "Type", "IsActive", "UpdatedAtUtc" }
            : new[] { "Name", "NormalizedName", "IsActive", "UpdatedAtUtc" };
        var forbidden = resource == "accounts" ? new[] { "Id", "UserId", "Currency", "OpeningBalance", "OpeningDate", "CreatedAtUtc" }
            : new[] { "Id", "UserId", "Kind", "CreatedAtUtc" };
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        foreach (var column in allowed.Concat(forbidden).Append("RowVersion"))
        {
            await using var permission = connection.CreateCommand();
            permission.CommandText = "SELECT HAS_PERMS_BY_NAME(@object,'OBJECT','UPDATE',@column,'COLUMN')";
            permission.Parameters.AddWithValue("@object", "dbo." + table);
            permission.Parameters.AddWithValue("@column", column);

            Assert.Equal(allowed.Contains(column) ? 1 : 0, Convert.ToInt32(await permission.ExecuteScalarAsync(TestCancellationToken)));
        }

        foreach (var column in allowed.Concat(forbidden))
        {
            await using var update = connection.CreateCommand();
            update.CommandText = $"UPDATE dbo.{table} SET [{column}]=[{column}] WHERE Id=@id";
            update.Parameters.AddWithValue("@id", id);
            if (allowed.Contains(column))
            {
                Assert.Equal(1, await update.ExecuteNonQueryAsync(TestCancellationToken));
            }
            else
            {
                var denied = await Assert.ThrowsAsync<SqlException>(() => update.ExecuteNonQueryAsync(TestCancellationToken));
                // UPDATE is granted only on editable columns; immutable-column denial is SQL error 230.
                Assert.Equal(230, denied.Number);
            }
        }

        foreach (var sql in new[] { $"DELETE FROM dbo.{table} WHERE Id=@id", $"ALTER TABLE dbo.{table} ADD ForbiddenColumn int NULL",
            "CREATE TABLE dbo.ForbiddenTable (Id int)", "UPDATE dbo.IdempotencyRecords SET ResponseBody=ResponseBody" })
        {
            await using var denied = connection.CreateCommand();
            denied.CommandText = sql;
            denied.Parameters.AddWithValue("@id", id);

            await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync(TestCancellationToken));
        }

        await DatabaseTasks.VerifyApplicationPrivilegesAsync(fixture.ApplicationConnectionString, TestCancellationToken);
    }

    [Theory]
    [InlineData("FinancialAccounts", "OpeningBalance", false)]
    [InlineData("Categories", "Kind", false)]
    [InlineData("Categories", "Name", true)]
    public async Task Verifier_rejects_extra_columns_and_delegation_in_disposable_database(string table, string column, bool delegation)
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        {
            InitialCatalog = "FinanceFlowColumnPrivileges_" + Guid.NewGuid().ToString("N")
        };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString) { InitialCatalog = migration.InitialCatalog };
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, TestCancellationToken);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, TestCancellationToken);
        await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, TestCancellationToken);
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString) { InitialCatalog = migration.InitialCatalog };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestCancellationToken);
        await using var grant = connection.CreateCommand();
        grant.CommandText = $"GRANT UPDATE ON OBJECT::dbo.{table} ({column}) TO FinanceFlowApi" + (delegation ? " WITH GRANT OPTION" : "");
        await grant.ExecuteNonQueryAsync(TestCancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseTasks.VerifyApplicationPrivilegesAsync(application.ConnectionString, TestCancellationToken));
    }

    private sealed class UpdateObserver(string resource) : DbCommandInterceptor
    {
        private readonly string table = resource == "accounts" ? "FinancialAccounts" : "Categories";
        private readonly TaskCompletionSource bothReadsCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pendingReads;
        public ConcurrentQueue<byte[]> Versions { get; } = new();

        public void ArmReadBarrier() => pendingReads = 2;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"FROM [dbo].[{table}]", StringComparison.Ordinal) && Volatile.Read(ref pendingReads) > 0)
            {
                if (Interlocked.Decrement(ref pendingReads) == 0)
                {
                    bothReadsCompleted.TrySetResult();
                }

                await bothReadsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"UPDATE [dbo].[{table}]", StringComparison.Ordinal))
            {
                var predicate = Regex.Match(command.CommandText, @"WHERE[\s\S]*?\[RowVersion\]\s*=\s*(?<parameter>@\w+)");
                Assert.True(predicate.Success, "The SQL UPDATE must include the rowversion predicate.");
                var parameter = command.Parameters.Cast<DbParameter>().Single(value =>
                    value.ParameterName.TrimStart('@') == predicate.Groups["parameter"].Value.TrimStart('@'));
                Versions.Enqueue(Assert.IsType<byte[]>(parameter.Value).ToArray());
            }

            return ValueTask.FromResult(result);
        }
    }
}
