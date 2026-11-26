using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using FinanceFlow.Application.Statements;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.FinancialReadRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class SnapshotConnectionPoolingSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_control_records_commit_or_rollback_and_actual_physical_pool_reuse(bool rollback)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var before = await ProbeAsync(scenario.Application, "control-before");
        await using (var connection = new SqlConnection(scenario.Application))
        {
            await connection.OpenAsync(Cancellation);
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Snapshot, Cancellation))
            {
                var during = await ProbeAsync(connection, transaction, "control-during", Cancellation);
                SameSession(before, during);
                Assert.Equal(5, during.Isolation);
                Assert.Equal(1, during.Transactions);
                if (rollback)
                {
                    await transaction.RollbackAsync(Cancellation);
                }
                else
                {
                    await transaction.CommitAsync(Cancellation);
                }
            }
            var disposed = await ProbeAsync(connection, null, "control-after-dispose", Cancellation);
            SameSession(before, disposed);
            Assert.Equal(0, disposed.Transactions);
            Assert.True(disposed.Isolation is 2 or 5, $"Unexpected isolation: {disposed.Isolation}");
        }
        var reused = await ProbeAsync(scenario.Application, "control-pooled-reuse");
        SameSession(before, reused);
        Assert.Equal(0, reused.Transactions);
        // Diagnostic control, not proof about the stores: record whether this provider/server
        // reset the pool session. The production-path tests below always require level 2.
        Assert.True(reused.Isolation is 2 or 5, $"Unexpected isolation: {reused.Isolation}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_store_keeps_snapshot_and_restores_the_same_session_before_pool_reuse(bool statement)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var before = await ProbeAsync(scenario.Application, "before-store");
        scenario.Trace.Armed = true;

        using var response = await scenario.Client.GetAsync(scenario.Path(statement), Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await ReadAsync(response);
        Assert.Equal(statement ? 3 : 1, body.GetProperty("totalCount").GetInt32());
        if (statement)
        {
            Assert.Equal(105m, body.GetProperty("closingBalance").GetDecimal());
            Assert.Equal(7m, body.GetProperty("result").GetDecimal());
        }
        var after = await ProbeAsync(scenario.Application, "after-store-reused");
        AssertRestored(scenario.Trace, before, after);
        scenario.Trace.Armed = false;
        var direct = await scenario.ReadStoreAsync(statement, Cancellation);
        if (statement)
        {
            Assert.Equal(105m, Assert.IsType<StatementResult>(direct).Statement!.ClosingBalance);
        }
        else
        {
            Assert.Equal(1, Assert.IsType<TransferListResult>(direct).Page!.TotalCount);
        }
        Assert.Equal(105m, await BalanceAsync(scenario.Client, scenario.Account.Id, "2000-01-06"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_JWT_owner_and_revocation_work_on_the_reused_session_after_snapshot(bool statement)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var before = await ProbeAsync(scenario.Application, "before-auth-reuse");
        await scenario.WarmAsync(statement);
        var after = await ProbeAsync(scenario.Application, "after-auth-warm");
        AssertRestored(scenario.Trace, before, after);

        using var profile = await scenario.Client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Equal(scenario.Owner, (await ReadAsync(profile)).GetProperty("id").GetGuid());
        SameSession(after, await ProbeAsync(scenario.Application, "after-profile"));
        using var logout = await scenario.Client.PostAsync("/api/v1/auth/logout", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revoked = await scenario.Client.GetAsync("/api/v1/users/me", Cancellation);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Equal("Bearer", Assert.Single(revoked.Headers.WwwAuthenticate).Scheme);
        SameSession(after, await ProbeAsync(scenario.Application, "after-revocation"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Existing_account_and_category_updates_keep_their_contract_after_snapshot(bool statement, bool category)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var before = await ProbeAsync(scenario.Application, "before-valid-update");
        await scenario.WarmAsync(statement);
        AssertRestored(scenario.Trace, before, await ProbeAsync(scenario.Application, "before-legacy-write"));
        scenario.Trace.InspectUpdate = true;
        var location = scenario.Resource(category);
        using var current = await scenario.Client.GetAsync(location, Cancellation);
        var version = current.Headers.ETag!.ToString();

        using var update = await scenario.UpdateAsync(category, version);

        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.NotEqual(version, update.Headers.ETag!.ToString());
        Assert.Equal(2, scenario.Trace.UpdateSession!.Isolation);
        SameSession(before, scenario.Trace.UpdateSession);
        using var stale = await scenario.UpdateAsync(category, version);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var get = await scenario.Client.GetAsync(location, Cancellation);
        Assert.Equal("Changed by owner", (await ReadAsync(get)).GetProperty("name").GetString());
        Assert.Equal(105m, await BalanceAsync(scenario.Client, scenario.Account.Id, "2000-01-06"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_real_blocked_rowversion_update_returns_412_without_inheriting_snapshot(bool statement, bool category)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var before = await ProbeAsync(scenario.Application, "before-concurrent-update");
        await scenario.WarmAsync(statement);
        AssertRestored(scenario.Trace, before, await ProbeAsync(scenario.Application, "after-warm-before-race"));
        var location = scenario.Resource(category);
        using var original = await scenario.Client.GetAsync(location, Cancellation);
        var originalName = (await ReadAsync(original)).GetProperty("name").GetString();
        scenario.Trace.InspectUpdate = true;
        scenario.Trace.PauseUpdate = true;
        var updating = scenario.UpdateAsync(category, original.Headers.ETag!.ToString());
        await using var writer = new SqlConnection(scenario.Migration);
        await writer.OpenAsync(Cancellation);
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, Cancellation);
        try
        {
            await scenario.Trace.AtUpdate.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            await using var change = writer.CreateCommand();
            change.Transaction = transaction;
            change.CommandText = category
                ? "UPDATE dbo.Categories SET UpdatedAtUtc=@now WHERE Id=@id"
                : "UPDATE dbo.FinancialAccounts SET UpdatedAtUtc=@now WHERE Id=@id";
            change.Parameters.AddWithValue("@now", new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
            change.Parameters.AddWithValue("@id", category ? scenario.Category : scenario.Account.Id);
            Assert.Equal(1, await change.ExecuteNonQueryAsync(Cancellation));
            scenario.Trace.ReleaseUpdate.TrySetResult();
            await WaitForRequestAsync(scenario.Admin, scenario.Trace.UpdateSession!.Spid, writer.ServerProcessId, waitForDelay: false);
            await transaction.CommitAsync(Cancellation);
        }
        finally
        {
            scenario.Trace.ReleaseUpdate.TrySetResult();
            await transaction.DisposeAsync();
        }
        using var rejected = await updating.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

        Assert.Equal(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
        Assert.Equal(2, scenario.Trace.UpdateSession!.Isolation);
        SameSession(before, scenario.Trace.UpdateSession);
        Assert.NotEqual(3960, scenario.Trace.SqlNumber);
        using var current = await scenario.Client.GetAsync(location, Cancellation);
        Assert.Equal(originalName, (await ReadAsync(current)).GetProperty("name").GetString());
        Assert.NotEqual(original.Headers.ETag, current.Headers.ETag);
        Assert.Equal(105m, await BalanceAsync(scenario.Client, scenario.Account.Id, "2000-01-06"));
    }

    [Theory]
    [InlineData(false, "timeout")]
    [InlineData(true, "timeout")]
    [InlineData(false, "unexpected")]
    [InlineData(true, "unexpected")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task Failed_or_canceled_store_reads_leave_only_safe_pool_sessions(bool statement, string stage)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var before = await ProbeAsync(scenario.Application, "before-failed-read");
        scenario.Trace.Stage = stage;
        scenario.Trace.Armed = true;
        if (stage == "cancel")
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            var reading = scenario.ReadStoreAsync(statement, requestCancellation.Token);
            try
            {
                await scenario.Trace.AtRead.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
                await WaitForRequestAsync(scenario.Admin, scenario.Trace.During!.Spid, 0, waitForDelay: true);
                Assert.False(reading.IsCompleted);
                Assert.False(requestCancellation.IsCancellationRequested);
                requestCancellation.Cancel();
            }
            finally
            {
                requestCancellation.Cancel();
            }
            var failure = await Record.ExceptionAsync(() => reading);
            Assert.False(Cancellation.IsCancellationRequested);
            AssertCanceledRead(scenario.Trace, failure, requestCancellation.Token);
        }
        else
        {
            using var response = await scenario.Client.GetAsync(scenario.Path(statement), Cancellation);
            Assert.Equal(stage == "timeout" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync(Cancellation);
            Assert.DoesNotContain("closingBalance", body);
            Assert.DoesNotContain("items", body);
            Assert.DoesNotContain("Injected", body);
            Assert.DoesNotContain("SqlException", body);
            if (stage == "timeout")
            {
                Assert.Equal(-2, scenario.Trace.SqlNumber);
            }
        }
        var after = await ProbeAsync(scenario.Application, "after-failed-read");
        AssertSafeReuseOrDiscard(scenario.Trace, before, after);
        scenario.Trace.Armed = false;
        Assert.Equal(105m, (await StatementAsync(scenario.Client, scenario.Account.Id)).GetProperty("closingBalance").GetDecimal());
    }

    [Theory]
    [InlineData(false, "reset-only")]
    [InlineData(true, "reset-only")]
    [InlineData(false, "primary-and-reset")]
    [InlineData(true, "primary-and-reset")]
    [InlineData(false, "reset-timeout")]
    [InlineData(true, "reset-timeout")]
    public async Task Failed_reset_retires_only_its_pool_and_never_masks_the_primary_failure(bool statement, string stage)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var healthy = await ProbeAsync(fixture.ApplicationConnectionString, "unrelated-healthy-pool-before");
        var before = await ProbeAsync(scenario.Application, "before-reset-failure");
        scenario.Trace.Stage = stage;
        scenario.Trace.Armed = true;
        if (stage == "primary-and-reset")
        {
            var failure = await Assert.ThrowsAsync<IOException>(() => scenario.ReadStoreAsync(statement, Cancellation));
            Assert.Same(scenario.Trace.PrimaryFailure, failure);
            Assert.NotSame(scenario.Trace.ResetFailure, failure);
        }
        else
        {
            using var response = await scenario.Client.GetAsync(scenario.Path(statement), Cancellation);
            Assert.Equal(stage == "reset-timeout" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.DoesNotContain("closingBalance", await response.Content.ReadAsStringAsync(Cancellation));
            if (stage == "reset-timeout")
            {
                Assert.Equal(-2, scenario.Trace.SqlNumber);
            }
        }
        Assert.True(scenario.Trace.ResetInjected);
        Assert.True(scenario.Trace.Discarded);
        SameSession(before, scenario.Trace.During!);
        var replacement = await ProbeAsync(scenario.Application, "replacement-after-failed-reset");
        Assert.NotEqual(before.ConnectionId, replacement.ConnectionId);
        Assert.Equal(2, replacement.Isolation);
        Assert.Equal(0, replacement.Transactions);
        SameSession(healthy, await ProbeAsync(fixture.ApplicationConnectionString, "unrelated-healthy-pool-after"));
        scenario.Trace.Armed = false;
        Assert.Equal(105m, await BalanceAsync(scenario.Client, scenario.Account.Id, "2000-01-06"));
    }

    private static void AssertCanceledRead(SessionTrace trace, Exception? failure, CancellationToken token)
    {
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(token, trace.ReadToken);
        Assert.True(trace.ReadCanceled);
        if (failure is OperationCanceledException)
        {
            return;
        }

        // SqlClient 6.1.8 can surface the attention acknowledgment as a faulted task,
        // preserving SqlException rather than producing OperationCanceledException.
        // Require the canceled reader's session and attention error shape.
        var sql = Assert.IsType<SqlException>(failure);
        Assert.Equal(trace.During!.ConnectionId, sql.ClientConnectionId);
        var errors = sql.Errors.Cast<SqlError>().ToArray();
        Assert.NotEmpty(errors);
        Assert.All(errors, error =>
        {
            TestContext.Current.TestOutputHelper!.WriteLine(
                $"Canceled reader SqlError: Number={error.Number}; State={error.State}; Class={error.Class}");
            Assert.Equal(0, error.Number);
            Assert.Equal((byte)0, error.State);
            Assert.Equal((byte)11, error.Class);
        });
        Assert.Contains(errors, error => error.Message == "Operation cancelled by user.");
    }

    private static void AssertSafeReuseOrDiscard(SessionTrace trace, Session before, Session after)
    {
        if (trace.Discarded)
        {
            SameSession(before, trace.During!);
            Assert.NotEqual(before.ConnectionId, after.ConnectionId);
            Assert.Equal(2, after.Isolation);
            Assert.Equal(0, after.Transactions);
        }
        else
        {
            AssertRestored(trace, before, after);
        }
    }

    private static void AssertRestored(SessionTrace trace, Session before, Session after)
    {
        Assert.False(trace.Discarded);
        SameSession(before, trace.During!);
        Assert.Equal(5, trace.During!.Isolation);
        Assert.Equal(1, trace.During.Transactions);
        SameSession(before, trace.BeforeReset!);
        Assert.Equal(0, trace.BeforeReset!.Transactions);
        SameSession(before, trace.AfterReset!);
        Assert.Equal(2, trace.AfterReset!.Isolation);
        Assert.Equal(0, trace.AfterReset.Transactions);
        SameSession(before, after);
        Assert.Equal(2, after.Isolation);
        Assert.Equal(0, after.Transactions);
    }

    private static void SameSession(Session expected, Session actual)
    {
        Assert.Equal(expected.Spid, actual.Spid);
        Assert.Equal(expected.ConnectionId, actual.ConnectionId);
    }

    private sealed record Session(int Spid, Guid ConnectionId, int Isolation, int Transactions);

    private static async Task<Session> ProbeAsync(string connectionString, string phase)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Cancellation);
        return await ProbeAsync(connection, null, phase, Cancellation);
    }

    private static async Task<Session> ProbeAsync(SqlConnection connection, SqlTransaction? transaction, string phase, CancellationToken token,
        Action<string>? write = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT CAST(@@SPID AS int), CAST(transaction_isolation_level AS int), @@TRANCOUNT
            FROM sys.dm_exec_sessions WHERE session_id=@@SPID
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        var state = new Session(reader.GetInt32(0), connection.ClientConnectionId, reader.GetInt32(1), reader.GetInt32(2));
        Assert.True(state.Spid > 0);
        Assert.NotEqual(Guid.Empty, state.ConnectionId);
        var message = $"{phase}: SPID={state.Spid}; ClientConnectionId={state.ConnectionId}; Isolation={state.Isolation}; TranCount={state.Transactions}";
        if (write is not null)
        {
            write(message);
        }
        else
        {
            TestContext.Current.TestOutputHelper!.WriteLine(message);
        }
        return state;
    }

    private static async Task WaitForRequestAsync(string adminConnectionString, int session, int blocker, bool waitForDelay)
    {
        await using var observer = new SqlConnection(adminConnectionString);
        await observer.OpenAsync(Cancellation);
        await using var query = observer.CreateCommand();
        query.CommandTimeout = 5;
        query.CommandText = waitForDelay
            ? "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id=@session AND wait_type='WAITFOR'"
            : "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id=@session AND blocking_session_id=@blocker AND wait_type LIKE 'LCK_M_%'";
        query.Parameters.AddWithValue("@session", session);
        query.Parameters.AddWithValue("@blocker", blocker);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (Convert.ToInt32(await query.ExecuteScalarAsync(deadline.Token)) == 0)
        {
            deadline.Token.ThrowIfCancellationRequested();
        }
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required string Application { get; init; }
        public required string Migration { get; init; }
        public required string Admin { get; init; }
        public required PoolingFactory Factory { get; init; }
        public required HttpClient Client { get; init; }
        public required SessionTrace Trace { get; init; }
        public required Guid Owner { get; init; }
        public required Guid AccountId { get; init; }
        public required Uri AccountLocation { get; init; }
        public required Guid Category { get; init; }
        public (Guid Id, Uri Location) Account => (AccountId, AccountLocation);

        public static async Task<Scenario> CreateAsync(IdentitySqlFixture fixture)
        {
            var name = "FinanceFlowPooling_" + Guid.NewGuid().ToString("N");
            var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString) { InitialCatalog = name, Pooling = false };
            var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
            {
                InitialCatalog = name, ApplicationName = name, Pooling = true, MaxPoolSize = 1, MinPoolSize = 0
            };
            await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
            await DatabaseTasks.MigrateAsync(migration.ConnectionString, Cancellation);
            await DatabaseTasks.ProvisionAsync(fixture.AdminConnectionString, migration.ConnectionString, application.ConnectionString, Cancellation);
            // TestServer need not preserve the test's ExecutionContext inside HTTP handlers.
            var output = TestContext.Current.TestOutputHelper!;
            var trace = new SessionTrace(message => output.WriteLine(message));
            var factory = new PoolingFactory(application.ConnectionString, trace);
            var client = factory.CreateClient();
            try
            {
                var owner = await CategoryTestRequests.AuthenticateAsync(client);
                var account = await AccountAsync(client, 100m);
                var other = await AccountAsync(client, 50m);
                using var income = await MovementAsync(client, account.Id, "income", 10m);
                using var expense = await MovementAsync(client, account.Id, "expense", 3m);
                using var transfer = await PostTransferAsync(client, new(account.Id, other.Id, 2m, new(2000, 1, 3), null));
                Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
                var category = (await ReadAsync(income)).GetProperty("categoryId").GetGuid();
                // Establish a measured control baseline, independent of earlier write transactions.
                await using (var connection = new SqlConnection(application.ConnectionString))
                {
                    await connection.OpenAsync(Cancellation);
                    await using var reset = connection.CreateCommand();
                    reset.CommandText = "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;";
                    await reset.ExecuteNonQueryAsync(Cancellation);
                }
                var baseline = await ProbeAsync(application.ConnectionString, "seeded-baseline");
                Assert.Equal(2, baseline.Isolation);
                return new Scenario { Application = application.ConnectionString, Migration = migration.ConnectionString,
                    Admin = fixture.AdminConnectionString, Factory = factory, Client = client, Trace = trace,
                    Owner = owner, AccountId = account.Id, AccountLocation = account.Location, Category = category };
            }
            catch
            {
                client.Dispose();
                factory.Dispose();
                throw;
            }
        }

        public string Path(bool statement) => statement
            ? $"/api/v1/accounts/{AccountId}/statement?from=2000-01-03&to=2000-01-06"
            : $"/api/v1/transfers?accountId={AccountId}";
        public Uri Resource(bool category) => category ? new Uri($"/api/v1/categories/{Category}", UriKind.Relative) : AccountLocation;
        public async Task WarmAsync(bool statement)
        {
            Trace.Armed = true;
            using var response = await Client.GetAsync(Path(statement), Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Trace.Armed = false;
        }
        public async Task<HttpResponseMessage> UpdateAsync(bool category, string version)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, Resource(category))
            {
                Content = category ? JsonContent.Create(new { name = "Changed by owner" })
                    : JsonContent.Create(new { name = "Changed by owner", type = "cash" })
            };
            request.Headers.Add("If-Match", version);
            return await Client.SendAsync(request, Cancellation);
        }
        public async Task<object> ReadStoreAsync(bool statement, CancellationToken token)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
            try
            {
                if (statement)
                {
                    return await scope.ServiceProvider.GetRequiredService<IAccountStatementStore>()
                        .GetAsync(Owner, AccountId, new(new(2000, 1, 3), new(2000, 1, 6)), token);
                }
                return await scope.ServiceProvider.GetRequiredService<ITransferStore>()
                    .ListAsync(Owner, new(AccountId: AccountId), token);
            }
            finally
            {
                // Check before the service scope could hide a leak by disposing the DbContext.
                Assert.Null(db.Database.CurrentTransaction);
                Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
            }
        }
        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            Factory.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PoolingFactory(string connectionString, SessionTrace trace) : ApiFactory()
    {
        public override string ConnectionString => connectionString;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging => logging.AddProvider(new DiscardLog(trace)));
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options =>
            {
                options.AddInterceptors(trace);
                options.LogTo((eventId, _) => eventId == CoreEventId.QueryIterationFailed, trace.QueryFailed);
            }));
        }
    }

    private sealed class DiscardLog(SessionTrace trace) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, trace);
        public void Dispose() { }
        private sealed class Capture(string category, SessionTrace trace) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "FinanceFlow.Infrastructure.Persistence.SnapshotReadSession" && eventId.Id == 6001)
                {
                    trace.Discarded = true;
                }
            }
        }
    }

    private sealed class SessionTrace(Action<string> write) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public string Stage { get; set; } = "";
        public Session? During { get; private set; }
        public Session? BeforeReset { get; private set; }
        public Session? AfterReset { get; private set; }
        public bool Discarded { get; set; }
        public bool ResetInjected { get; private set; }
        public int? SqlNumber { get; private set; }
        private Guid? ReadCommandId { get; set; }
        public CancellationToken ReadToken { get; private set; }
        public bool ReadCanceled { get; private set; }
        public IOException PrimaryFailure { get; } = new("Injected primary read failure.");
        public IOException ResetFailure { get; } = new("Injected reset failure.");
        public bool InspectUpdate { get; set; }
        public bool PauseUpdate { get; set; }
        public Session? UpdateSession { get; private set; }
        public TaskCompletionSource AtUpdate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUpdate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AtRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (InspectUpdate && command.CommandText.Contains("UPDATE [", StringComparison.Ordinal)
                && (command.CommandText.Contains("[FinancialAccounts]", StringComparison.Ordinal) || command.CommandText.Contains("[Categories]", StringComparison.Ordinal)))
            {
                UpdateSession = await ProbeAsync((SqlConnection)command.Connection!, (SqlTransaction?)command.Transaction, "legacy-update", cancellationToken, write);
                write($"Legacy UPDATE has EF transaction: {command.Transaction is not null}");
                AtUpdate.TrySetResult();
                if (PauseUpdate)
                {
                    await ReleaseUpdate.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
            }
            if (Armed && During is null && command.Transaction?.IsolationLevel == IsolationLevel.Snapshot)
            {
                During = await ProbeAsync((SqlConnection)command.Connection!, (SqlTransaction)command.Transaction, "store-snapshot", cancellationToken, write);
                if (Stage is "unexpected" or "primary-and-reset")
                {
                    throw PrimaryFailure;
                }
                if (Stage is "timeout" or "cancel")
                {
                    ReadCommandId = eventData.CommandId;
                    ReadToken = cancellationToken;
                    command.CommandTimeout = Stage == "timeout" ? 1 : 30;
                    command.CommandText = (Stage == "timeout" ? "WAITFOR DELAY '00:00:02';\n" : "WAITFOR DELAY '00:00:30';\n") + command.CommandText;
                    AtRead.TrySetResult();
                }
            }
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.StartsWith("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", StringComparison.Ordinal))
            {
                Assert.False(cancellationToken.CanBeCanceled);
                Assert.Null(command.Transaction);
                Assert.Null(eventData.Context!.Database.CurrentTransaction);
                BeforeReset = await ProbeAsync((SqlConnection)command.Connection!, null, "after-tx-dispose-before-reset", cancellationToken, write);
                if (Stage is "reset-only" or "primary-and-reset")
                {
                    ResetInjected = true;
                    throw ResetFailure;
                }
                if (Stage == "reset-timeout")
                {
                    ResetInjected = true;
                    command.CommandTimeout = 1;
                    command.CommandText = "WAITFOR DELAY '00:00:02';\n" + command.CommandText;
                }
            }
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.StartsWith("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", StringComparison.Ordinal))
            {
                AfterReset = await ProbeAsync((SqlConnection)command.Connection!, null, "after-reset-before-close", cancellationToken, write);
            }
            return result;
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Observe(eventData.Exception);
            return Task.CompletedTask;
        }
        public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Stage == "cancel" && eventData.CommandId == ReadCommandId)
            {
                ReadCanceled = ReadToken.IsCancellationRequested;
            }
            return Task.CompletedTask;
        }
        public void QueryFailed(EventData eventData)
        {
            if (eventData is DbContextTypeErrorEventData error)
            {
                Observe(error.Exception);
            }
        }
        private void Observe(Exception exception)
        {
            if (exception is InvalidOperationException { InnerException: { } inner })
            {
                exception = inner;
            }
            if (exception is SqlException sql)
            {
                SqlNumber = sql.Number;
            }
        }
    }
}
