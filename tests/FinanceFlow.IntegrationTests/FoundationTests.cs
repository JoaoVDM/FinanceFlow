using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceFlow.Api.Controllers;
using Microsoft.Extensions.Options;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class FoundationTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    public async Task Health_starts_host_and_returns_healthy_json(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var health = await response.Content.ReadFromJsonAsync<HealthResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("healthy", health?.Status);
    }

    [Fact]
    public async Task Development_exposes_openapi_with_controller_routes_and_configured_title()
    {
        using var factory = new ApiFactory("Development", "FinanceFlow test documentation");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.StartsWith("3.", root.GetProperty("openapi").GetString());
        Assert.Equal("FinanceFlow test documentation", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("v1", root.GetProperty("info").GetProperty("version").GetString());
        Assert.True(root.GetProperty("paths").TryGetProperty("/health", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty("/health/live", out _));
    }

    [Fact]
    public async Task Development_serves_swagger_ui()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("swagger-ui", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/index.html")]
    [InlineData("/missing-resource")]
    public async Task Production_returns_problem_details_for_unavailable_routes(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Unhandled_exception_returns_safe_problem_details_in_all_environments(string environment)
    {
        using var factory = new ApiFactory(environment);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/__tests/failure", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("sensitive-test-detail", body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    public async Task Invalid_input_returns_validation_problem_details(string json)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/v1/__tests/validation", content, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_error");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(document.RootElement.GetProperty("errors").EnumerateObject().Any());
    }

    [Fact]
    public async Task Unsupported_method_returns_problem_details()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/health", null, TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed, "method_not_allowed");
        Assert.Contains("GET", response.Content.Headers.Allow);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_used_configuration_fails_at_startup(string title)
    {
        using var factory = new ApiFactory("Development", title);
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal((int)status, root.GetProperty("status").GetInt32());
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("instance").GetString()));
    }
}
