using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinanceFlow.IntegrationTests;

public sealed class ApiFactory(string environment = "Production", string? documentTitle = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        // The test host must not depend on access to the Windows Event Log.
        // Keep console logging without changing the application's providers.
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
        if (documentTitle is not null)
        {
            builder.ConfigureAppConfiguration((context, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenApi:Title"] = documentTitle
                }));
        }

        // These routes exist only in the test host, never in the shipped API.
        builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(TestOnlyController).Assembly));
    }
}
