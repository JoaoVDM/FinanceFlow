using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Transactions;
using FinanceFlow.Infrastructure.Persistence;
using FinanceFlow.Infrastructure.Persistence.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionMutationRequests;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class FinancialRecoverySqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Same_key_is_separated_by_operation_and_equivalent_json_replays_the_original_snapshot()
    {
        using var factory = new RecoveryFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var key = Guid.NewGuid().ToString("N");
        using var account = await PostAsync(client, "accounts", key, null);
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        using var category = await CategoryTestRequests.CreateAsync(client, "Namespace", "income");
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var accountId = (await ReadAsync(account)).GetProperty("id").GetGuid();
        var categoryId = (await ReadAsync(category)).GetProperty("id").GetGuid();
        var references = new TransactionReferences(accountId, categoryId, account.Headers.Location!, category.Headers.Location!,
            account.Headers.ETag!.ToString(), category.Headers.ETag!.ToString());
        using var transaction = await PostAsync(client, "transactions", key, references);
        Assert.Equal(HttpStatusCode.Created, transaction.StatusCode);

        // Reordered properties, an equivalent exponent and GUID casing must not change the command.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions")
        {
            Content = new StringContent($$"""{"description":"Fictitious transaction","occurredOn":"2000-01-01","amount":1e1,"kind":"income","categoryId":"{{categoryId.ToString("D").ToUpperInvariant()}}","accountId":"{{accountId.ToString("D").ToUpperInvariant()}}"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        using var replay = await client.SendAsync(request, Cancellation);

        await SameSnapshotAsync(transaction, replay);
        using var accountReplay = await PostAsync(client, "accounts", key, null);
        await SameSnapshotAsync(account, accountReplay);
        await AssertStateAsync(factory, owner, "accounts", key, 1);
        await AssertStateAsync(factory, owner, "transactions", key, 1);
        Assert.Equal(133.45m, await BalanceAsync(client, accountId));
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("transactions")]
    public async Task Failure_after_both_inserts_before_commit_rolls_back_finance_and_snapshot_and_allows_retry(string resource)
    {
        var fault = new BeforeCommitFault();
        using var factory = new RecoveryFactory(fixture, fault);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var references = resource == "transactions" ? await ReferencesAsync(client) : null;
        var key = Guid.NewGuid().ToString("N");
        fault.Armed = true;

        using var failed = await PostAsync(client, resource, key, references);

        Assert.True(fault.Injected);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await AssertStateAsync(factory, owner, resource, key, 0);
        using var retry = await PostAsync(client, resource, key, references);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        await AssertStateAsync(factory, owner, resource, key, 1);
        using var replay = await PostAsync(client, resource, key, references);
        await SameSnapshotAsync(retry, replay);
        if (references is not null)
        {
            Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
        }
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("transactions")]
    public async Task Lost_response_after_real_commit_recovers_the_persisted_snapshot_without_a_second_financial_effect(string resource)
    {
        var fault = new AfterCommitFault();
        using var factory = new RecoveryFactory(fixture, fault);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var references = resource == "transactions" ? await ReferencesAsync(client) : null;
        var key = Guid.NewGuid().ToString("N");
        fault.Armed = true;

        using var lost = await PostAsync(client, resource, key, references);

        Assert.True(fault.Injected);
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        await AssertStateAsync(factory, owner, resource, key, 1);
        if (references is not null)
        {
            await DeactivateAsync(client, references.AccountLocation, references.AccountVersion);
            await DeactivateAsync(client, references.CategoryLocation, references.CategoryVersion);
        }
        using var recovered = await PostAsync(client, resource, key, references);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        var record = await db.IdempotencyRecords.AsNoTracking().SingleAsync(value => value.UserId == owner
            && value.Operation == Operation(resource) && value.Key == key, Cancellation);
        Assert.Equal(record.StatusCode, (int)recovered.StatusCode);
        Assert.Equal(record.ResponseBody, await recovered.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(record.Location, recovered.Headers.Location!.ToString());
        Assert.Equal(record.ETag, recovered.Headers.ETag!.ToString());
        using var repeated = await PostAsync(client, resource, key, references);
        await SameSnapshotAsync(recovered, repeated);
        await AssertStateAsync(factory, owner, resource, key, 1);
        if (references is not null)
        {
            Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
        }
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("transactions")]
    public async Task Real_insert_timeout_leaves_no_success_record_and_same_key_retry_creates_once(string resource)
    {
        var fault = new InsertTimeout(resource == "accounts" ? "FinancialAccounts" : "FinancialTransactions");
        using var factory = new RecoveryFactory(fixture, fault);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var references = resource == "transactions" ? await ReferencesAsync(client) : null;
        var key = Guid.NewGuid().ToString("N");
        fault.Armed = true;

        using var failed = await PostAsync(client, resource, key, references);

        Assert.Equal(-2, fault.SqlError);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        await AssertStateAsync(factory, owner, resource, key, 0);
        using var retry = await PostAsync(client, resource, key, references);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        using var replay = await PostAsync(client, resource, key, references);
        await SameSnapshotAsync(retry, replay);
        await AssertStateAsync(factory, owner, resource, key, 1);
    }

    [Fact]
    public async Task Real_deadlock_victim_has_no_partial_creation_and_can_repeat_the_same_key()
    {
        var observer = new DeadlockObserver();
        using var factory = new RecoveryFactory(fixture, observer);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var references = await ReferencesAsync(client);
        var key = Guid.NewGuid().ToString("N");
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var blocker = (SqlTransaction)await connection.BeginTransactionAsync(Cancellation);
        await using var lockTable = connection.CreateCommand();
        lockTable.Transaction = blocker;
        lockTable.CommandText = "SET DEADLOCK_PRIORITY HIGH; SELECT TOP (1) Id FROM dbo.FinancialTransactions WITH (TABLOCKX, HOLDLOCK)";
        await lockTable.ExecuteScalarAsync(Cancellation);
        observer.Armed = true;

        var creating = PostAsync(client, "transactions", key, references);
        await observer.InsertStarted.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
        // The API holds a shared account lock and needs the transaction table. This second
        // connection holds that table and needs the account, producing a real SQL deadlock.
        await using var lockAccount = connection.CreateCommand();
        lockAccount.Transaction = blocker;
        lockAccount.CommandText = "UPDATE dbo.FinancialAccounts SET Name=Name WHERE Id=@id";
        lockAccount.Parameters.AddWithValue("@id", references.AccountId);
        Assert.Equal(1, await lockAccount.ExecuteNonQueryAsync(Cancellation));
        await blocker.CommitAsync(Cancellation);
        using var failed = await creating;

        Assert.Equal(1205, observer.SqlError);
        var strategyFailure = Assert.IsType<InvalidOperationException>(observer.PersistenceFailure);
        var updateFailure = Assert.IsType<DbUpdateException>(strategyFailure.InnerException);
        Assert.Equal(1205, Assert.IsType<SqlException>(updateFailure.InnerException).Number);
        await AssertStateAsync(factory, owner, "transactions", key, 0);
        Assert.Equal(0m, await BalanceAsync(client, references.AccountId));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var problem = await ReadAsync(failed);
        Assert.Equal("service_unavailable", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("sqlError", out _));
        Assert.DoesNotContain("SqlException", await failed.Content.ReadAsStringAsync(Cancellation));
        using var retry = await PostAsync(client, "transactions", key, references);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        using var replay = await PostAsync(client, "transactions", key, references);
        await SameSnapshotAsync(retry, replay);
        await AssertStateAsync(factory, owner, "transactions", key, 1);
        Assert.Equal(10m, await BalanceAsync(client, references.AccountId));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string resource, string key, TransactionReferences? references)
    {
        if (resource == "transactions")
        {
            return await CreateAsync(client, references!, key);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts")
        {
            Content = JsonContent.Create(new { name = "Recovery account", type = "cash", currency = "BRL", openingBalance = 123.45m, openingDate = "2000-01-01" })
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, Cancellation);
    }

    private static string Operation(string resource) => resource == "accounts" ? AccountCreationFingerprint.Operation : TransactionCreationFingerprint.Operation;

    private static async Task AssertStateAsync(RecoveryFactory factory, Guid owner, string resource, string key, int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        var financialCount = resource == "accounts"
            ? await db.FinancialAccounts.CountAsync(value => value.UserId == owner, Cancellation)
            : await db.FinancialTransactions.CountAsync(value => value.UserId == owner, Cancellation);
        Assert.Equal(count, financialCount);
        Assert.Equal(count, await db.IdempotencyRecords.CountAsync(value => value.UserId == owner
            && value.Operation == Operation(resource) && value.Key == key, Cancellation));
    }

    private static async Task SameSnapshotAsync(HttpResponseMessage original, HttpResponseMessage replay)
    {
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await original.Content.ReadAsStringAsync(Cancellation), await replay.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(original.Headers.Location, replay.Headers.Location);
        Assert.Equal(original.Headers.ETag, replay.Headers.ETag);
        Assert.Equal(original.Content.Headers.ContentType, replay.Content.Headers.ContentType);
    }

    private sealed class RecoveryFactory(IdentitySqlFixture fixture, params IInterceptor[] interceptors) : ApiFactory()
    {
        public override string ConnectionString => fixture.ApplicationConnectionString;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(interceptors)));
        }
    }

    private sealed class BeforeCommitFault : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<IdempotencyRecord>().Any())
            {
                Armed = false;
                Injected = true;
                throw new InvalidOperationException("Injected after snapshot insert, before commit.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AfterCommitFault : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                Injected = true;
                throw new IOException("Injected response loss after the database committed.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class InsertTimeout(string table) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int? SqlError { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains($"INSERT INTO [dbo].[{table}]", StringComparison.Ordinal))
            {
                Armed = false;
                // Induce a real command timeout, not an ordering assumption about concurrent tasks.
                command.CommandTimeout = 1;
                command.CommandText = "WAITFOR DELAY '00:00:02';\n" + command.CommandText;
            }
            return ValueTask.FromResult(result);
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is SqlException exception)
            {
                SqlError = exception.Number;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class DeadlockObserver : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int? SqlError { get; private set; }
        public Exception? PersistenceFailure { get; private set; }
        public TaskCompletionSource InsertStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            PersistenceFailure = eventData.Exception;
            return Task.CompletedTask;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("INSERT INTO [dbo].[FinancialTransactions]", StringComparison.Ordinal))
            {
                Armed = false;
                InsertStarted.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is SqlException exception)
            {
                SqlError = exception.Number;
            }
            return Task.CompletedTask;
        }
    }
}
