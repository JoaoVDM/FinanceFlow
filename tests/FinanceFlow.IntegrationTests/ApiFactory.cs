using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace FinanceFlow.IntegrationTests;

public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string environment;
    private readonly string? documentTitle;

    public ApiFactory(string environment = "Production", string? documentTitle = null)
    {
        this.environment = environment;
        this.documentTitle = documentTitle;
        ClientOptions.BaseAddress = new Uri("https://localhost");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        // The test host must not depend on access to the Windows Event Log.
        // Keep console logging without changing the application's providers.
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
        builder.ConfigureAppConfiguration((context, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenApi:Title"] = documentTitle ?? "FinanceFlow API",
                    ["Jwt:Issuer"] = "FinanceFlow.Tests",
                    ["Jwt:Audience"] = "FinanceFlow.Tests.Client",
                    ["Jwt:SigningKeyBase64"] = TestKey,
                    ["Database:ConnectionString"] = ConnectionString
                }));

        // These routes exist only in the test host, never in the shipped API.
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(TestOnlyController).Assembly);
            services.AddAuthorization(options => options.AddPolicy("test-deny", policy => policy.RequireAssertion(_ => false)));
        });
    }

    public string TestKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    public virtual string ConnectionString => "Server=127.0.0.1,1;Database=FinanceFlowUnavailable;User ID=UnavailableApi;Password="
        + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + ";Encrypt=True;TrustServerCertificate=True;Connect Timeout=1";
}
