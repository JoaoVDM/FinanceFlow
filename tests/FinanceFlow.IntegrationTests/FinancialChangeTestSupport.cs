using System.Net.Http.Json;
using System.Text.Json;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceFlow.IntegrationTests;

internal static class FinancialChangeRequests
{
    internal static CancellationToken TestCancellationToken => Xunit.TestContext.Current.CancellationToken;

    internal static async Task<HttpResponseMessage> CreateResourceAsync(HttpClient client, string resource, string name = "Original")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/" + resource);
        if (resource == "accounts")
        {
            request.Content = JsonContent.Create(new { name, type = "checking", currency = "BRL", openingBalance = -50.25m, openingDate = "2000-01-01" });
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        }
        else
        {
            request.Content = JsonContent.Create(new { name, kind = "expense" });
        }

        return await client.SendAsync(request, TestCancellationToken);
    }

    internal static object Command(string resource, string method, string name = "Changed", bool isActive = false)
    {
        if (method == "PATCH")
        {
            return new { isActive };
        }

        if (resource == "accounts")
        {
            return new { name, type = "cash" };
        }

        return new { name };
    }

    internal static async Task<HttpResponseMessage> ChangeAsync(
        HttpClient client, Uri location, string method, object body, string? version)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), location) { Content = JsonContent.Create(body) };
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", version);
        }

        return await client.SendAsync(request, TestCancellationToken);
    }

    internal static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestCancellationToken));
        return json.RootElement.Clone();
    }
}

internal sealed class FinancialChangeApiFactory(IdentitySqlFixture fixture, DbCommandInterceptor? interceptor = null) : ApiFactory()
{
    public DateTimeOffset Instant { get; } = DateTimeOffset.UtcNow;
    public override string ConnectionString => fixture.ApplicationConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(new FixedClock(Instant));
            if (interceptor is not null)
            {
                services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(interceptor));
            }
        });
    }

    private sealed class FixedClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
