using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Statements;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;

namespace FinanceFlow.IntegrationTests;

internal static class FinancialReadRequests
{
    internal static async Task<HttpResponseMessage> MovementAsync(HttpClient client, Guid account, string kind, decimal amount,
        string date = "2000-01-03")
    {
        using var category = await CategoryTestRequests.CreateAsync(client, Guid.NewGuid().ToString("N"), kind);
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions")
        {
            Content = JsonContent.Create(new { accountId = account, categoryId = (await ReadAsync(category)).GetProperty("id").GetGuid(),
                kind, amount, occurredOn = date, description = "Fictitious read scenario" })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return response;
    }

    internal static async Task<JsonElement> StatementAsync(HttpClient client, Guid account, string from = "2000-01-03",
        string to = "2000-01-06", int page = 1, int size = 20)
    {
        using var response = await client.GetAsync($"/api/v1/accounts/{account}/statement?from={from}&to={to}&page={page}&pageSize={size}", Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await ReadAsync(response);
    }
}

internal sealed class FinancialReadApiFactory(IdentitySqlFixture fixture, params IInterceptor[] interceptors) : ApiFactory()
{
    public override string ConnectionString => fixture.ApplicationConnectionString;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(interceptors));
            services.AddScoped(provider => new AccountStatementsService(provider.GetRequiredService<IAccountStatementStore>(),
                provider.GetRequiredService<ICurrentUser>(), new ReadClock(), provider.GetRequiredService<FinancialCalendar>()));
            services.AddScoped(provider => new TransfersService(provider.GetRequiredService<ITransferStore>(),
                provider.GetRequiredService<ICurrentUser>(), new ReadClock(), provider.GetRequiredService<FinancialCalendar>()));
        });
    }
    private sealed class ReadClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 22, 2, 0, 0, TimeSpan.Zero);
    }
}
