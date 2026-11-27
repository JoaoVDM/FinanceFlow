using System.Data;
using System.Data.Common;
using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.CategoryReportRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class QueryConsolidationSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData("accounts")]
    [InlineData("categories")]
    [InlineData("transactions")]
    public async Task Count_and_page_share_the_snapshot_when_a_writer_commits_between_them(string resource)
    {
        var observer = new PageBarrier(resource);
        using var factory = new ReportApiFactory(fixture, observer);
        using var reader = factory.CreateClient();
        using var writer = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(reader);
        writer.DefaultRequestHeaders.Authorization = reader.DefaultRequestHeaders.Authorization;
        var account = await AccountAsync(reader);
        var category = await CategoryAsync(reader, "Original", "income");
        using var original = await CategoryMovementAsync(reader, account.Id, category.Id, "income", 1m);
        observer.Armed = true;
        var pending = reader.GetAsync($"/api/v1/{resource}", Cancellation);
        try
        {
            await observer.AtPage.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
            if (resource == "accounts")
            {
                _ = await AccountAsync(writer);
            }
            else if (resource == "categories")
            {
                _ = await CategoryAsync(writer, "New", "income");
            }
            else
            {
                using var created = await CategoryMovementAsync(writer, account.Id, category.Id, "income", 2m);
            }
        }
        finally
        {
            observer.Release.TrySetResult();
        }
        using var response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await ReadAsync(response);
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, observer.Reads);
        Assert.Equal(IsolationLevel.Snapshot, observer.Isolation);
        observer.Armed = false;
        using var later = await reader.GetAsync($"/api/v1/{resource}", Cancellation);
        var updated = await ReadAsync(later);
        Assert.Equal(2, updated.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, updated.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("accounts", "timeout")]
    [InlineData("categories", "timeout")]
    [InlineData("transactions", "timeout")]
    [InlineData("accounts", "strategy")]
    [InlineData("categories", "strategy")]
    [InlineData("transactions", "strategy")]
    [InlineData("accounts", "update-wrapper")]
    [InlineData("categories", "update-wrapper")]
    [InlineData("transactions", "update-wrapper")]
    [InlineData("accounts", "unexpected")]
    [InlineData("categories", "unexpected")]
    [InlineData("transactions", "unexpected")]
    public async Task New_snapshot_lists_preserve_safe_failures_and_can_read_again(string resource, string stage)
    {
        var fault = new ListFault(stage);
        using var factory = new ReportApiFactory(fixture, fault, fault.QueryFailed);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        fault.Armed = true;
        using var response = await client.GetAsync($"/api/v1/{resource}", Cancellation);
        Assert.True(fault.Injected);
        Assert.Equal(stage == "unexpected" ? HttpStatusCode.InternalServerError : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(stage == "unexpected" ? null : (int?)-2, fault.SqlNumber);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(Cancellation);
        foreach (var forbidden in new[] { "\"items\"", "\"totalCount\"", "SqlException", "SELECT", "Injected", "stackTrace" })
        {
            Assert.DoesNotContain(forbidden, body);
        }
        using var recovered = await client.GetAsync($"/api/v1/{resource}", Cancellation);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(0, (await ReadAsync(recovered)).GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("accounts", 2)]
    [InlineData("categories", 1)]
    [InlineData("transactions", 1)]
    [InlineData("transfers", 1)]
    [InlineData("statement", 2)]
    public async Task Extreme_valid_offset_has_an_empty_page_and_overflow_is_rejected(string resource, int count)
    {
        using var factory = new ReportApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var a = await AccountAsync(client);
        var b = await AccountAsync(client);
        var category = await CategoryAsync(client, "History", "income");
        using var movement = await CategoryMovementAsync(client, a.Id, category.Id, "income", 1m);
        using var transfer = await PostTransferAsync(client, new(a.Id, b.Id, 1m, new(2026, 11, 1), null));
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        var path = resource == "statement"
            ? $"/api/v1/accounts/{a.Id}/statement?from=2026-11-01&to=2026-11-26&"
            : $"/api/v1/{resource}?";
        using var valid = await client.GetAsync(path + "page=2147483647&pageSize=1", Cancellation);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var page = await ReadAsync(valid);
        Assert.Equal(count, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(count, page.GetProperty("totalPages").GetInt32());
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
        if (resource == "statement")
        {
            Assert.Equal(0m, page.GetProperty("closingBalance").GetDecimal());
            Assert.Equal(1m, page.GetProperty("income").GetDecimal());
            Assert.Equal(1m, page.GetProperty("transferOut").GetDecimal());
        }
        using var invalid = await client.GetAsync(path + "page=2147483647&pageSize=2", Cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("accounts", 2, 1)]
    [InlineData("categories", 2, 1)]
    [InlineData("transactions", 4, 1)]
    [InlineData("transfers", 3, 1)]
    [InlineData("balance", 1, 0)]
    [InlineData("statement", 3, 1)]
    [InlineData("summary", 2, 1)]
    [InlineData("by-category", 2, 1)]
    public async Task Real_queries_have_bounded_financial_commands_without_counting_JWT_or_session_cleanup(string resource, int reads, int resets)
    {
        var counter = new CommandCounter();
        using var factory = new ReportApiFactory(fixture, counter);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var account = await AccountAsync(client);
        var category = await CategoryAsync(client, "Count", "income");
        using var movement = await CategoryMovementAsync(client, account.Id, category.Id, "income", 1m);
        counter.Armed = true;
        var path = resource switch
        {
            "transactions" => $"/api/v1/transactions?accountId={account.Id}&categoryId={category.Id}",
            "transfers" => $"/api/v1/transfers?accountId={account.Id}",
            "balance" => $"/api/v1/accounts/{account.Id}/balance?asOf=2026-11-26",
            "statement" => $"/api/v1/accounts/{account.Id}/statement?from=2026-11-01&to=2026-11-26",
            "summary" or "by-category" => $"/api/v1/reports/{resource}?from=2026-11-01&to=2026-11-26&accountId={account.Id}",
            _ => $"/api/v1/{resource}"
        };
        using var response = await client.GetAsync(path, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(counter.FinancialReads, 1, reads);
        Assert.Equal(resets, counter.Resets);
        Assert.True(counter.AuthenticationReads > 0);
    }

    private sealed class PageBarrier(string resource) : DbCommandInterceptor
    {
        private readonly string table = resource switch { "accounts" => "FinancialAccounts", "categories" => "Categories", _ => "FinancialTransactions" };
        public bool Armed { get; set; }
        public int Reads { get; private set; }
        public IsolationLevel? Isolation { get; private set; }
        public TaskCompletionSource AtPage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains($"[{table}]", StringComparison.Ordinal)
                && command.Transaction?.IsolationLevel != IsolationLevel.RepeatableRead)
            {
                Reads++;
                if (command.CommandText.Contains("OFFSET", StringComparison.Ordinal))
                {
                    Isolation = command.Transaction?.IsolationLevel;
                    AtPage.TrySetResult();
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
                }
            }
            return result;
        }
    }

    private sealed class ListFault(string stage) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Injected { get; private set; }
        public int? SqlNumber { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.Transaction?.IsolationLevel == IsolationLevel.Snapshot && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal))
            {
                Armed = false;
                Injected = true;
                if (stage == "unexpected")
                {
                    throw new InvalidOperationException("Injected list failure without a SQL cause.");
                }
                command.CommandTimeout = 1;
                command.CommandText = "WAITFOR DELAY '00:00:02';\n" + command.CommandText;
                if (stage is "strategy" or "update-wrapper")
                {
                    try
                    {
                        // Run the actual SQL timeout, then exercise only known EF wrappers.
                        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                        throw new InvalidOperationException("The SQL timeout was not induced.");
                    }
                    catch (SqlException sql)
                    {
                        SqlNumber = sql.Number;
                        throw new InvalidOperationException("EF wrapper", stage == "strategy" ? sql : new DbUpdateException("Update wrapper", sql));
                    }
                }
            }
            return result;
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Observe(eventData.Exception);
            return Task.CompletedTask;
        }
        public void QueryFailed(EventData data)
        {
            if (data is DbContextTypeErrorEventData error) Observe(error.Exception);
        }
        private void Observe(Exception exception)
        {
            if (Injected && exception is SqlException sql) SqlNumber = sql.Number;
        }
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int FinancialReads { get; private set; }
        public int AuthenticationReads { get; private set; }
        public int Resets { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                if (command.CommandText.Contains("[AspNetUsers]", StringComparison.Ordinal)) AuthenticationReads++;
                else FinancialReads++;
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.StartsWith("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", StringComparison.Ordinal)) Resets++;
            return ValueTask.FromResult(result);
        }
    }
}
