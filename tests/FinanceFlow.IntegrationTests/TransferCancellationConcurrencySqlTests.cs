using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using FinanceFlow.Domain.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.TransferCancellationRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransferCancellationConcurrencySqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Two_independent_cancellations_have_one_winner_and_both_use_the_clients_version_in_sql()
    {
        var observer = new CancellationObserver { CoordinateSaves = true };
        using var factory = new TransferCancellationApiFactory(fixture, observer);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 10.01m, new(2000, 1, 1), null));
        var version = created.Headers.ETag!.ToString();
        observer.Armed = true;

        var responses = await Task.WhenAll(CancelAsync(client, created.Headers.Location!, version),
            CancelAsync(client, created.Headers.Location!, version));

        try
        {
            await AssertSuccessAsync(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NoContent));
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NotFound);
            Assert.Equal(2, observer.Contexts.Distinct().Count());
            Assert.Equal(2, observer.Versions.Count);
            Assert.All(observer.Versions, actual => Assert.Equal(Convert.FromBase64String(version.Trim('"')), actual));
            Assert.Equal(0m, await BalanceAsync(client, source.Id));
            Assert.Equal(0m, await BalanceAsync(client, destination.Id));
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
            var persisted = await db.Transfers.AsNoTracking().SingleAsync(value => value.UserId == owner, Cancellation);
            Assert.True(persisted.IsCanceled);
            Assert.Equal(factory.Clock.Now, persisted.CanceledAtUtc);
            using var repeated = await CancelAsync(client, created.Headers.Location!, version);
            Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_version_changed_after_the_read_is_rejected_by_the_actual_sql_update()
    {
        var observer = new CancellationObserver { PauseSave = true };
        using var factory = new TransferCancellationApiFactory(fixture, observer);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null));
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        observer.Armed = true;
        var canceling = CancelAsync(client, created.Headers.Location!, created.Headers.ETag!.ToString());
        try
        {
            await observer.AtSave.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            // Simulate a separate writer on this disposable row without canceling it.
            await using var connection = new SqlConnection(fixture.MigrationConnectionString);
            await connection.OpenAsync(Cancellation);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE dbo.Transfers SET UpdatedAtUtc=@now WHERE Id=@id";
            update.Parameters.AddWithValue("@now", factory.Clock.Now.AddMinutes(1));
            update.Parameters.AddWithValue("@id", id);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(Cancellation));
        }
        finally
        {
            observer.ReleaseSave.TrySetResult();
        }

        using var rejected = await canceling;

        Assert.Equal(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
        Assert.Equal(Convert.FromBase64String(created.Headers.ETag.ToString().Trim('"')), Assert.Single(observer.Versions));
        using var current = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.NotEqual(created.Headers.ETag, current.Headers.ETag);
        Assert.Equal(-10m, await BalanceAsync(client, source.Id));
        Assert.Equal(10m, await BalanceAsync(client, destination.Id));
        observer.Armed = false;
        using var retry = await CancelAsync(client, created.Headers.Location!, current.Headers.ETag!.ToString());
        await AssertSuccessAsync(retry);
    }

    [Theory]
    [InlineData("before-update")]
    [InlineData("after-save")]
    [InlineData("timeout")]
    public async Task Failures_roll_back_cancellation_timestamps_version_balances_and_allow_a_later_cancel(string stage)
    {
        var fault = new CancellationFault(stage);
        using var factory = new TransferCancellationApiFactory(fixture, fault);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        var body = new TransferBody(source.Id, destination.Id, 10.01m, new(2000, 1, 1), null);
        var key = Guid.NewGuid().ToString("N");
        using var created = await PostTransferAsync(client, body, key);
        fault.Armed = true;

        using var failed = await CancelAsync(client, created.Headers.Location!, created.Headers.ETag!.ToString());

        Assert.True(fault.Injected);
        Assert.Equal(stage == "timeout" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(failed.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("SqlException", await failed.Content.ReadAsStringAsync(Cancellation));
        if (stage == "timeout")
        {
            var persistenceFailure = fault.PersistenceFailure;
            if (persistenceFailure is InvalidOperationException { InnerException: { } strategyFailure })
            {
                persistenceFailure = strategyFailure;
            }
            var failure = Assert.IsType<DbUpdateException>(persistenceFailure);
            Assert.Equal(-2, Assert.IsType<SqlException>(failure.InnerException).Number);
        }
        using var current = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(created.Headers.ETag, current.Headers.ETag);
        Assert.Equal(-10.01m, await BalanceAsync(client, source.Id));
        Assert.Equal(10.01m, await BalanceAsync(client, destination.Id));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var persisted = await scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>().Transfers.AsNoTracking()
                .SingleAsync(value => value.UserId == owner, Cancellation);
            Assert.False(persisted.IsCanceled);
            Assert.Null(persisted.CanceledAtUtc);
            Assert.Null(persisted.UpdatedAtUtc);
        }
        using var retry = await CancelAsync(client, created.Headers.Location!, created.Headers.ETag.ToString());
        await AssertSuccessAsync(retry);
        using var replay = await PostTransferAsync(client, body, key);
        await SameTransferSnapshotAsync(created, replay);
        Assert.Equal(0m, await BalanceAsync(client, source.Id));
        Assert.Equal(0m, await BalanceAsync(client, destination.Id));
    }

    private sealed class CancellationObserver : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool CoordinateSaves { get; init; }
        public bool PauseSave { get; init; }
        public ConcurrentBag<Guid> Contexts { get; } = [];
        public ConcurrentBag<byte[]> Versions { get; } = [];
        public TaskCompletionSource AtSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource bothSaves = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int saves;

        public async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<Transfer>().Any(entry => entry.State == EntityState.Modified))
            {
                Contexts.Add(eventData.Context.ContextId.InstanceId);
                AtSave.TrySetResult();
                if (CoordinateSaves)
                {
                    if (Interlocked.Increment(ref saves) == 2)
                    {
                        bothSaves.TrySetResult();
                    }
                    await bothSaves.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
                if (PauseSave)
                {
                    await ReleaseSave.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
            }
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FROM [dbo].[Transfers]", StringComparison.Ordinal)
                && command.CommandText.Contains("[RowVersion]", StringComparison.Ordinal))
            {
                Assert.Null(command.Transaction);
            }
            if (Armed && command.CommandText.Contains("UPDATE [dbo].[Transfers]", StringComparison.Ordinal))
            {
                Assert.Equal(IsolationLevel.ReadCommitted, command.Transaction!.IsolationLevel);
                var where = command.CommandText[command.CommandText.IndexOf("WHERE", StringComparison.Ordinal)..];
                var version = Regex.Match(where, @"\[RowVersion\]\s*=\s*(@\w+)");
                Assert.True(version.Success);
                Versions.Add(Assert.IsType<byte[]>(command.Parameters[version.Groups[1].Value].Value).ToArray());
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancellationFault(string stage) : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public Exception? PersistenceFailure { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("UPDATE [dbo].[Transfers]", StringComparison.Ordinal) && stage != "after-save")
            {
                Armed = false;
                Injected = true;
                if (stage == "timeout")
                {
                    command.CommandTimeout = 1;
                    command.CommandText = "WAITFOR DELAY '00:00:02';\n" + command.CommandText;
                }
                else
                {
                    throw new IOException("Injected cancellation failure before the update.");
                }
            }
            return ValueTask.FromResult(result);
        }

        public ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armed && stage == "after-save" && eventData.Context!.ChangeTracker.Entries<Transfer>().Any(entry => entry.Entity.IsCanceled))
            {
                Armed = false;
                Injected = true;
                throw new IOException("Injected cancellation failure after SaveChanges, before commit.");
            }
            return ValueTask.FromResult(result);
        }

        public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Injected)
            {
                PersistenceFailure = eventData.Exception;
            }
            return Task.CompletedTask;
        }
    }
}
