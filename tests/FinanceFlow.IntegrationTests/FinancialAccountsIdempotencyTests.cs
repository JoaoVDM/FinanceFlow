using System.Data.Common;
using System.Net;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed partial class FinancialAccountsSqlTests
{
    [Fact]
    public async Task Replay_preserves_original_bytes_location_and_etag_after_later_database_change()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);

        using var first = await CreateAccountAsync(client, "Original", "replay", "10.000");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var originalBody = await first.Content.ReadAsStringAsync(TestCancellationToken);
        var account = await ReadAccountAsync(first);
        await using (var connection = new SqlConnection(fixture.MigrationConnectionString))
        {
            await connection.OpenAsync(TestCancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dbo.FinancialAccounts SET Name=N'Later', IsActive=0 WHERE Id=@id";
            command.Parameters.AddWithValue("@id", account.Id);
            await command.ExecuteNonQueryAsync(TestCancellationToken);
        }

        // Same command with another property order and equivalent decimal representation.
        using var replay = await SendCreationAsync(
            client,
            "replay",
            "{\"openingDate\":\"2000-01-01\",\"openingBalance\":10.00,\"currency\":\"BRL\",\"type\":\"checking\",\"name\":\"Original\"}");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync(TestCancellationToken));
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(first.Headers.ETag, replay.Headers.ETag);
        using var current = await client.GetAsync(first.Headers.Location, TestCancellationToken);
        Assert.Equal("Later", (await ReadAccountAsync(current)).Name);
        Assert.NotEqual(first.Headers.ETag, current.Headers.ETag);
        using var conflict = await CreateAccountAsync(client, "Different", "replay");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Simultaneous_identical_keys_create_one_account_and_replay_identically()
    {
        var gate = new InitialLookupGate();
        using var factory = new InstrumentedFactory(fixture, gate);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        gate.Arm();

        var responses = await Task.WhenAll(
            CreateAccountAsync(client, "Concurrent", "concurrent"),
            CreateAccountAsync(client, "Concurrent", "concurrent"));
        try
        {
            Assert.All(responses, x => Assert.Equal(HttpStatusCode.Created, x.StatusCode));
            Assert.Equal(await responses[0].Content.ReadAsStringAsync(TestCancellationToken), await responses[1].Content.ReadAsStringAsync(TestCancellationToken));
            Assert.Equal(responses[0].Headers.ETag, responses[1].Headers.ETag);
            Assert.Equal(responses[0].Headers.Location, responses[1].Headers.Location);
            await AssertRecordCountsAsync(factory, owner, 1);
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
    public async Task Simultaneous_different_payloads_with_one_key_have_one_winner()
    {
        var gate = new InitialLookupGate();
        using var factory = new InstrumentedFactory(fixture, gate);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        gate.Arm();

        var responses = await Task.WhenAll(CreateAccountAsync(client, "First", "race"), CreateAccountAsync(client, "Second", "race"));
        try
        {
            Assert.Equal(new[] { 201, 409 }, responses.Select(x => (int)x.StatusCode).Order().ToArray());
            await AssertRecordCountsAsync(factory, owner, 1);
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
    public async Task Violation_of_another_unique_constraint_is_not_replayed()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);
        using var first = await CreateAccountAsync(client, "Existing", "existing");
        var existing = await ReadAccountAsync(first);
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IFinancialAccountStore>();
        var collision = FinanceFlow.Domain.Accounts.FinancialAccount.Create(
            existing.Id,
            owner,
            "Collision",
            "cash",
            "BRL",
            0m,
            existing.OpeningDate,
            new FinancialCalendar().DateAt(DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(
            collision,
            "new-key",
            AccountCreationFingerprint.Compute(collision),
            TestCancellationToken));
        await AssertRecordCountsAsync(factory, owner, 1);
    }

    [Fact]
    public async Task Failure_after_real_account_insert_rolls_back_and_key_can_be_retried()
    {
        var fault = new RecordInsertFault();
        using var factory = new InstrumentedFactory(fixture, fault);
        using var client = factory.CreateClient();
        var owner = await AuthenticateAsync(client);

        using var failed = await CreateAccountAsync(client, "Rollback", "rollback");

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.True(fault.AccountInsertExecuted);
        await AssertRecordCountsAsync(factory, owner, 0);
        Assert.DoesNotContain("injected-record-failure", await failed.Content.ReadAsStringAsync(TestCancellationToken));
        using var retry = await CreateAccountAsync(client, "Rollback", "rollback");
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        await AssertRecordCountsAsync(factory, owner, 1);
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
        private int remainingLookups;
        private readonly TaskCompletionSource bothLookupsCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => remainingLookups = 2;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            // Both initial SELECTs must finish before either request can attempt its INSERT.
            if (command.CommandText.Contains("FROM [dbo].[IdempotencyRecords]", StringComparison.Ordinal)
                && Volatile.Read(ref remainingLookups) > 0)
            {
                if (Interlocked.Decrement(ref remainingLookups) == 0)
                {
                    bothLookupsCompleted.TrySetResult();
                }

                await bothLookupsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    private sealed class RecordInsertFault : DbCommandInterceptor
    {
        private int remainingFailures = 1;

        public bool AccountInsertExecuted { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [dbo].[FinancialAccounts]", StringComparison.Ordinal))
            {
                AccountInsertExecuted = true;
            }

            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            // Fail between the two real INSERTs, so the assertions exercise SQL rollback.
            if (command.CommandText.Contains("INSERT INTO [dbo].[IdempotencyRecords]", StringComparison.Ordinal)
                && Interlocked.Exchange(ref remainingFailures, 0) == 1)
            {
                throw new InvalidOperationException("injected-record-failure");
            }

            return ValueTask.FromResult(result);
        }
    }
}
