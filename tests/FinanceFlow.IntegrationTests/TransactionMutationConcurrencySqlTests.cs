using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransactionMutationRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransactionMutationConcurrencySqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("PUT", "PUT")]
    [InlineData("PUT", "DELETE")]
    [InlineData("DELETE", "DELETE")]
    public async Task Independent_mutations_from_one_version_have_one_winner_and_real_sql_preconditions(string firstMethod, string secondMethod)
    {
        var observer = new MutationObserver();
        using var factory = new MutationFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        var body = Payload(await ReadAsync(created));
        var version = created.Headers.ETag!.ToString();
        observer.ArmInitialReads();

        var responses = await Task.WhenAll(
            MutateAsync(client, created.Headers.Location!, firstMethod, body with { Amount = 20m }, version),
            MutateAsync(client, created.Headers.Location!, secondMethod, body with { Amount = 30m }, version));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent);
            var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.NoContent);
            await SuccessAsync(responses[winner]);
            var winnerMethod = winner == 0 ? firstMethod : secondMethod;
            Assert.Equal(winnerMethod == "DELETE" ? HttpStatusCode.NotFound : HttpStatusCode.PreconditionFailed, responses[1 - winner].StatusCode);
            Assert.Equal(2, observer.Versions.Count);
            Assert.All(observer.Versions, value => Assert.Equal(Convert.FromBase64String(version.Trim('"')), value));
            Assert.Equal(winnerMethod == "DELETE" ? 0m : winner == 0 ? 20m : 30m, await BalanceAsync(client, references.AccountId));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Failure_after_the_sql_update_but_before_commit_rolls_back_data_version_and_balance(string method)
    {
        var fault = new FailureAfterSave();
        using var factory = new MutationFactory(fixture, fault);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        var original = await ReadAsync(created);
        var body = Payload(original);
        fault.Arm(original.GetProperty("id").GetGuid());

        using var failed = await MutateAsync(client, created.Headers.Location!, method, body with { Amount = 20m }, created.Headers.ETag!.ToString());

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(fault.Injected);
        using var current = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(created.Headers.ETag, current.Headers.ETag);
        Assert.Equal(body, Payload(await ReadAsync(current)));
        Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
        using var retry = await MutateAsync(client, created.Headers.Location!, method, body with { Amount = 20m }, created.Headers.ETag.ToString());
        await SuccessAsync(retry);
    }

    [Theory]
    [InlineData("FinancialAccounts", false)]
    [InlineData("Categories", false)]
    [InlineData("FinancialAccounts", true)]
    public async Task Reassociation_holds_reference_shared_locks_until_commit_and_deactivation_waits(string table, bool sourceAccount)
    {
        var observer = new MutationObserver();
        using var factory = new MutationFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await ReferencesAsync(client);
        var target = await ReferencesAsync(client);
        var deactivated = sourceAccount ? source : target;
        using var created = await CreateAsync(client, source);
        var body = Payload(await ReadAsync(created)) with { AccountId = target.AccountId, CategoryId = target.CategoryId };
        observer.PauseReferences = true;
        var editing = MutateAsync(client, created.Headers.Location!, "PUT", body, created.Headers.ETag!.ToString());
        Task<HttpResponseMessage>? deactivating = null;
        try
        {
            await observer.ReferencesRead.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            Assert.Equal(IsolationLevel.RepeatableRead, observer.ReferenceIsolation);
            Assert.True(await HasHeldLockAsync(observer.SessionId, table));
            deactivating = FinancialChangeRequests.ChangeAsync(client,
                table == "FinancialAccounts" ? deactivated.AccountLocation : deactivated.CategoryLocation, "PATCH", new { isActive = false },
                table == "FinancialAccounts" ? deactivated.AccountVersion : deactivated.CategoryVersion);
            await observer.DeactivationStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            Assert.False(deactivating.IsCompleted);
        }
        finally
        {
            observer.ReleaseReferences.TrySetResult();
        }
        using var edited = await editing;
        using var inactive = await deactivating!;
        await SuccessAsync(edited);
        Assert.Equal(HttpStatusCode.NoContent, inactive.StatusCode);
        Assert.Equal(0m, await BalanceAsync(client, source.AccountId));
        Assert.Equal(10m, await BalanceAsync(client, target.AccountId));
    }

    [Theory]
    [InlineData("FinancialAccounts", false)]
    [InlineData("Categories", false)]
    [InlineData("FinancialAccounts", true)]
    public async Task Deactivation_committed_before_reference_validation_blocks_the_new_link(string table, bool sourceAccount)
    {
        var observer = new MutationObserver { ReadTable = table };
        using var factory = new MutationFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await ReferencesAsync(client);
        var target = await ReferencesAsync(client);
        var deactivated = sourceAccount ? source : target;
        using var created = await CreateAsync(client, source);
        var body = Payload(await ReadAsync(created)) with { AccountId = target.AccountId, CategoryId = target.CategoryId };
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var sqlTransaction = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await using var command = connection.CreateCommand();
        command.Transaction = sqlTransaction;
        command.CommandText = $"UPDATE dbo.{table} SET IsActive=0 WHERE Id=@id";
        command.Parameters.AddWithValue("@id", table == "FinancialAccounts" ? deactivated.AccountId : deactivated.CategoryId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Cancellation));
        observer.ObserveReferenceStart = true;

        var editing = MutateAsync(client, created.Headers.Location!, "PUT", body, created.Headers.ETag!.ToString());
        await observer.ReferenceReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
        Assert.False(editing.IsCompleted);
        await sqlTransaction.CommitAsync(Cancellation);

        using var rejected = await editing;
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(10m, await BalanceAsync(client, source.AccountId));
        Assert.Equal(0m, await BalanceAsync(client, target.AccountId));
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Sql_timeout_is_503_and_leaves_the_original_row_and_version_intact(string method)
    {
        var timeout = new TimeoutOnMutation();
        using var factory = new MutationFactory(fixture, timeout);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        using var created = await CreateAsync(client, references);
        var body = Payload(await ReadAsync(created));
        timeout.Armed = true;

        using var failed = await MutateAsync(client, created.Headers.Location!, method, body with { Amount = 20m }, created.Headers.ETag!.ToString());

        Assert.True(timeout.Injected);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("service_unavailable", (await ReadAsync(failed)).GetProperty("code").GetString());
        using var current = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(created.Headers.ETag, current.Headers.ETag);
        Assert.Equal(body, Payload(await ReadAsync(current)));
        Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
    }

    private async Task<bool> HasHeldLockAsync(int sessionId, string table)
    {
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            InitialCatalog = new SqlConnectionStringBuilder(fixture.MigrationConnectionString).InitialCatalog
        };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.dm_tran_locks AS locks
            LEFT JOIN sys.partitions AS partitions ON locks.resource_associated_entity_id=partitions.hobt_id
            WHERE locks.request_session_id=@session AND locks.request_status='GRANT' AND locks.request_mode='S'
              AND (partitions.object_id=OBJECT_ID(@table)
                OR (locks.resource_type='OBJECT' AND locks.resource_associated_entity_id=OBJECT_ID(@table)))
            """;
        command.Parameters.AddWithValue("@session", sessionId);
        command.Parameters.AddWithValue("@table", "dbo." + table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(Cancellation)) > 0;
    }

    private sealed class MutationFactory(IdentitySqlFixture fixture, params IInterceptor[] interceptors) : ApiFactory()
    {
        public override string ConnectionString => fixture.ApplicationConnectionString;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(interceptors)));
        }
    }

    private sealed class TimeoutOnMutation : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("UPDATE [dbo].[FinancialTransactions]", StringComparison.Ordinal))
            {
                Armed = false;
                Injected = true;
                // An intentional server delay exceeds this command's timeout; it is not a sleep
                // used to infer ordering, and the real SQL provider must report timeout -2.
                command.CommandTimeout = 1;
                command.CommandText = "WAITFOR DELAY '00:00:02';\n" + command.CommandText;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailureAfterSave : SaveChangesInterceptor
    {
        private Guid target;
        private int armed;
        public bool Injected { get; private set; }
        public void Arm(Guid id)
        {
            target = id;
            armed = 1;
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<FinanceFlow.Domain.Transactions.FinancialTransaction>().Any(entry => entry.Entity.Id == target)
                && Interlocked.Exchange(ref armed, 0) == 1)
            {
                Injected = true;
                throw new InvalidOperationException("Injected failure after financial update and before commit.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MutationObserver : DbCommandInterceptor
    {
        private int pendingReads;
        private readonly TaskCompletionSource bothReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<byte[]> Versions { get; } = new();
        public bool PauseReferences { get; set; }
        public bool ObserveReferenceStart { get; set; }
        public string ReadTable { get; init; } = "FinancialAccounts";
        public int SessionId { get; private set; }
        public IsolationLevel? ReferenceIsolation { get; private set; }
        public TaskCompletionSource ReferencesRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReferences { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReferenceReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeactivationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ArmInitialReads() => pendingReads = 2;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (ObserveReferenceStart && command.CommandText.Contains($"FROM [dbo].[{ReadTable}]", StringComparison.Ordinal))
            {
                ReferenceReadStarted.TrySetResult();
            }
            if (command.CommandText.Contains("UPDATE [dbo].[FinancialAccounts]", StringComparison.Ordinal)
                || command.CommandText.Contains("UPDATE [dbo].[Categories]", StringComparison.Ordinal))
            {
                DeactivationStarted.TrySetResult();
            }
            if (command.CommandText.Contains("UPDATE [dbo].[FinancialTransactions]", StringComparison.Ordinal))
            {
                var predicate = Regex.Match(command.CommandText, @"WHERE[\s\S]*?\[RowVersion\]\s*=\s*(?<parameter>@\w+)");
                Assert.True(predicate.Success, "The mutation SQL must use the client's rowversion as a predicate.");
                var parameter = command.Parameters.Cast<DbParameter>().Single(value => value.ParameterName.TrimStart('@') == predicate.Groups["parameter"].Value.TrimStart('@'));
                Versions.Enqueue(Assert.IsType<byte[]>(parameter.Value).ToArray());
            }
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [dbo].[FinancialTransactions]", StringComparison.Ordinal) && Volatile.Read(ref pendingReads) > 0)
            {
                Assert.Null(command.Transaction);
                if (Interlocked.Decrement(ref pendingReads) == 0) bothReads.TrySetResult();
                await bothReads.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            if (PauseReferences && command.CommandText.Contains("FROM [dbo].[Categories]", StringComparison.Ordinal))
            {
                PauseReferences = false;
                SessionId = ((SqlConnection)command.Connection!).ServerProcessId;
                ReferenceIsolation = command.Transaction!.IsolationLevel;
                ReferencesRead.TrySetResult();
                await ReleaseReferences.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
