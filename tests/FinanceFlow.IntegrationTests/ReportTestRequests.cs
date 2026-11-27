using System.Net;
using System.Text.Json;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Reports;
using FinanceFlow.Application.Statements;
using FinanceFlow.Application.Transactions;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using Xunit;

namespace FinanceFlow.IntegrationTests;

internal static class ReportTestRequests
{
    internal static string SummaryPath(Guid? account = null, string from = "2026-11-01", string to = "2026-11-26") =>
        $"/api/v1/reports/summary?from={from}&to={to}" + (account is null ? "" : $"&accountId={account}");

    internal static async Task<JsonElement> SummaryAsync(HttpClient client, Guid? account = null,
        string from = "2026-11-01", string to = "2026-11-26")
    {
        using var response = await client.GetAsync(SummaryPath(account, from, to), Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
        var body = await ReadAsync(response);
        Assert.Equal(new[] { "currency", "expense", "from", "income", "result", "to" },
            body.EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal("BRL", body.GetProperty("currency").GetString());
        Assert.Equal(from, body.GetProperty("from").GetString());
        Assert.Equal(to, body.GetProperty("to").GetString());
        return body;
    }

    internal static void Totals(JsonElement body, decimal income, decimal expense)
    {
        Assert.Equal(income, body.GetProperty("income").GetDecimal());
        Assert.Equal(expense, body.GetProperty("expense").GetDecimal());
        Assert.Equal(income - expense, body.GetProperty("result").GetDecimal());
    }
}

// Only financial clocks are replaced. Real JWT issuance, lifetime and revocation are unchanged.
internal sealed class ReportApiFactory(IdentitySqlFixture fixture, IInterceptor? interceptor = null,
    Action<EventData>? queryFailure = null, string? connectionString = null) : ApiFactory()
{
    public override string ConnectionString => connectionString ?? fixture.ApplicationConnectionString;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var clock = new ReportClock();
            services.AddScoped(provider => new ReportsService(provider.GetRequiredService<IReportStore>(),
                provider.GetRequiredService<ICurrentUser>(), clock, provider.GetRequiredService<FinancialCalendar>()));
            services.AddScoped(provider => new FinancialAccountsService(provider.GetRequiredService<IFinancialAccountStore>(),
                provider.GetRequiredService<ICurrentUser>(), clock, provider.GetRequiredService<FinancialCalendar>()));
            services.AddScoped(provider => new FinancialTransactionsService(provider.GetRequiredService<IFinancialTransactionStore>(),
                provider.GetRequiredService<ICurrentUser>(), clock, provider.GetRequiredService<FinancialCalendar>()));
            services.AddScoped(provider => new TransfersService(provider.GetRequiredService<ITransferStore>(),
                provider.GetRequiredService<ICurrentUser>(), clock, provider.GetRequiredService<FinancialCalendar>()));
            services.AddScoped(provider => new AccountStatementsService(provider.GetRequiredService<IAccountStatementStore>(),
                provider.GetRequiredService<ICurrentUser>(), clock, provider.GetRequiredService<FinancialCalendar>()));
            services.AddDbContext<FinanceFlowDbContext>(options =>
            {
                if (interceptor is not null)
                {
                    options.AddInterceptors(interceptor);
                }
                if (queryFailure is not null)
                {
                    options.LogTo((eventId, _) => eventId == CoreEventId.QueryIterationFailed, queryFailure);
                }
            });
        });
    }

    private sealed class ReportClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    }
}
