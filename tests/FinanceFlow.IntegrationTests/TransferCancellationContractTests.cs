using System.Net;
using System.Text.Json;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class TransferCancellationContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("not.a.jwt")]
    public async Task Cancellation_requires_valid_jwt_before_version_and_never_caches_the_challenge(string? token)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.DeleteAsync("/api/v1/transfers/" + Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Openapi_documents_versioned_bodyless_cancellation_without_idempotency_or_other_mutations()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = json.RootElement.GetProperty("paths");
        var resource = paths.GetProperty("/api/v1/transfers/{id}");
        var operation = resource.GetProperty("delete");

        Assert.Equal(new[] { "delete", "get" }, resource.EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal(new[] { "get", "post" }, paths.GetProperty("/api/v1/transfers").EnumerateObject().Select(value => value.Name).Order());
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.False(operation.TryGetProperty("requestBody", out _));
        var headers = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Contains(headers, header => header.GetProperty("name").GetString() == "If-Match"
            && header.GetProperty("required").GetBoolean());
        Assert.DoesNotContain(headers, header => header.GetProperty("name").GetString() == "Idempotency-Key");
        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "204", "400", "401", "404", "412", "428", "429", "503" })
        {
            Assert.True(responses.TryGetProperty(status, out _));
        }
        Assert.False(responses.GetProperty("204").TryGetProperty("content", out _));
        var responseHeaders = responses.GetProperty("204").GetProperty("headers");
        Assert.True(responseHeaders.TryGetProperty("ETag", out _));
        Assert.True(responseHeaders.TryGetProperty("Cache-Control", out _));
    }
}
