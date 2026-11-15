using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceFlow.Api.Concurrency;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace FinanceFlow.IntegrationTests;

public sealed class IfMatchContractTests
{
    [Fact]
    public async Task Absent_header_is_428_and_empty_header_is_400()
    {
        var headers = new HeaderDictionary();

        Assert.Equal(428, IfMatchHeader.Read(headers, out var missingVersion));
        Assert.Null(missingVersion);
        headers["If-Match"] = string.Empty;
        Assert.Equal(400, IfMatchHeader.Read(headers, out var emptyVersion));
        Assert.Null(emptyVersion);

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app => app.Run(context =>
            {
                context.Response.StatusCode = IfMatchHeader.Read(context.Request.Headers, out _) ?? 204;
                return Task.CompletedTask;
            })))
            .StartAsync(TestContext.Current.CancellationToken);
        var server = host.GetTestServer();
        using var client = server.CreateClient();
        using var missing = await client.PutAsync("/", null, TestContext.Current.CancellationToken);
        Assert.Equal(428, (int)missing.StatusCode);

        // Configure the incoming context because the HttpClient transport omits an empty value.
        using var emptyHeaderClient = new HttpClient(server.CreateHandler(context =>
            context.Request.Headers["If-Match"] = string.Empty))
        {
            BaseAddress = client.BaseAddress
        };
        using var empty = await emptyHeaderClient.PutAsync("/", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("W/\"AAAAAAAAAAE=\"")]
    [InlineData("AAAAAAAAAAE=")]
    [InlineData("\"AAAAAAAAAAE=\",\"AAAAAAAAAAI=\"")]
    [InlineData("\"!!!!!!!!!!!=\"")]
    [InlineData("\"AAAAAAAAAA==\"")]
    [InlineData("\"AAAAAAAAAAAA\"")]
    [InlineData("\"AAAAAAAAAAJ=\"")]
    [InlineData(" \"AAAAAAAAAAE=\"")]
    [InlineData("\"AAAAAAAAAAE=\" ")]
    public void Malformed_or_noncanonical_validator_is_rejected(string value)
    {
        var headers = new HeaderDictionary { ["If-Match"] = value };

        var status = IfMatchHeader.Read(headers, out var version);

        Assert.Equal(400, status);
        Assert.Null(version);
    }

    [Fact]
    public void Multiple_header_values_are_not_accepted_as_a_single_version()
    {
        var headers = new HeaderDictionary { ["If-Match"] = new StringValues(["\"AAAAAAAAAAE=\"", "\"AAAAAAAAAAI=\""]) };

        Assert.Equal(400, IfMatchHeader.Read(headers, out var version));
        Assert.Null(version);
    }

    [Theory]
    [InlineData("AAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAE=")]
    [InlineData("//////////8=")]
    public void Canonical_strong_validator_decodes_exactly_eight_opaque_bytes(string encoded)
    {
        var headers = new HeaderDictionary { ["If-Match"] = '"' + encoded + '"' };

        var status = IfMatchHeader.Read(headers, out var version);

        Assert.Null(status);
        Assert.Equal(Convert.FromBase64String(encoded), version);
    }

    [Theory]
    [InlineData("accounts", "PUT")]
    [InlineData("accounts", "PATCH")]
    [InlineData("categories", "PUT")]
    [InlineData("categories", "PATCH")]
    public async Task Mutation_routes_require_jwt_and_errors_are_not_cached(string resource, string method)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/{resource}/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { })
        };

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Openapi_documents_four_versioned_mutations_and_preserves_creation_headers()
    {
        using var factory = new ApiFactory("Development");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var resource in new[] { "accounts", "categories" })
        {
            var path = paths.GetProperty($"/api/v1/{resource}/{{id}}");
            Assert.False(path.TryGetProperty("delete", out _));
            foreach (var method in new[] { "put", "patch" })
            {
                var operation = path.GetProperty(method);
                Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
                var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
                Assert.Contains(parameters, parameter => parameter.GetProperty("name").GetString() == "If-Match"
                    && parameter.GetProperty("required").GetBoolean());
                Assert.DoesNotContain(parameters, parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
                var responses = operation.GetProperty("responses");
                foreach (var status in new[] { "204", "400", "401", "404", "412", "428", "429", "503" })
                {
                    Assert.True(responses.TryGetProperty(status, out _));
                }

                var success = responses.GetProperty("204");
                Assert.False(success.TryGetProperty("content", out _));
                Assert.True(success.GetProperty("headers").TryGetProperty("ETag", out _));
            }
        }

        Assert.True(paths.GetProperty("/api/v1/categories/{id}").GetProperty("put").GetProperty("responses").TryGetProperty("409", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(new[] { "name", "type" }, schemas.GetProperty("UpdateAccountRequest").GetProperty("properties").EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(new[] { "name" }, schemas.GetProperty("RenameCategoryRequest").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        foreach (var schema in new[] { "SetAccountActivityRequest", "SetCategoryActivityRequest" })
        {
            Assert.Contains(schemas.GetProperty(schema).GetProperty("required").EnumerateArray(), value => value.GetString() == "isActive");
            Assert.Equal("boolean", schemas.GetProperty(schema).GetProperty("properties").GetProperty("isActive").GetProperty("type").GetString());
        }
    }
}
