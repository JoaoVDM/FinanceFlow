using System.Net;
using System.Text;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Common;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FinanceFlow.IntegrationTests.TransactionSqlRequests;
using static FinanceFlow.IntegrationTests.TransferSqlRequests;
using static FinanceFlow.IntegrationTests.TransferCancellationRequests;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class TransferCancellationSqlTests(IdentitySqlFixture fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cancellation_preserves_the_row_snapshot_and_patrimony_even_with_inactive_accounts(bool sourceInactive, bool destinationInactive)
    {
        using var factory = new TransferCancellationApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client, 2000.01m);
        var destination = await AccountAsync(client, 1000.02m);
        var body = new TransferBody(source.Id, destination.Id, 500.01m, new(2000, 1, 3), "Preserved description");
        var key = Guid.NewGuid().ToString("N");
        using var created = await PostTransferAsync(client, body, key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var original = await ReadAsync(created);
        var id = original.GetProperty("id").GetGuid();
        Assert.Equal(2000.01m, await BalanceAsync(client, source.Id, "2000-01-02"));
        Assert.Equal(1000.02m, await BalanceAsync(client, destination.Id, "2000-01-02"));
        Assert.Equal(1500m, await BalanceAsync(client, source.Id, "2000-01-03"));
        Assert.Equal(1500.03m, await BalanceAsync(client, destination.Id, "2000-01-03"));
        if (sourceInactive)
        {
            await TransactionMutationRequests.DeactivateAsync(client, source.Location, source.Version);
        }
        if (destinationInactive)
        {
            await TransactionMutationRequests.DeactivateAsync(client, destination.Location, destination.Version);
        }
        factory.Clock.Now = factory.Clock.Now.AddMinutes(5);

        using var canceled = await CancelAsync(client, created.Headers.Location!, created.Headers.ETag!.ToString());

        await AssertSuccessAsync(canceled);
        Assert.NotEqual(created.Headers.ETag, canceled.Headers.ETag);
        using var get = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        foreach (var cut in new[] { "2000-01-02", "2000-01-03", "2000-01-04" })
        {
            Assert.Equal(2000.01m, await BalanceAsync(client, source.Id, cut));
            Assert.Equal(1000.02m, await BalanceAsync(client, destination.Id, cut));
        }
        Assert.Equal(3000.03m, await BalanceAsync(client, source.Id) + await BalanceAsync(client, destination.Id));
        using var oldVersion = await CancelAsync(client, created.Headers.Location!, created.Headers.ETag.ToString());
        using var canceledVersion = await CancelAsync(client, created.Headers.Location!, canceled.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, oldVersion.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, canceledVersion.StatusCode);
        using var replay = await PostTransferAsync(client, body, key);
        await SameTransferSnapshotAsync(created, replay);
        using var conflict = await PostTransferAsync(client, body with { Amount = 500m }, key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        var persisted = await db.Transfers.AsNoTracking().SingleAsync(value => value.Id == id && value.UserId == owner, Cancellation);
        Assert.True(persisted.IsCanceled);
        Assert.Equal(factory.Clock.Now, persisted.CanceledAtUtc);
        Assert.Equal(TimeSpan.Zero, persisted.CanceledAtUtc!.Value.Offset);
        Assert.Equal(persisted.CanceledAtUtc, persisted.UpdatedAtUtc);
        Assert.Equal(original.GetProperty("createdAtUtc").GetDateTimeOffset(), persisted.CreatedAtUtc);
        Assert.Equal(source.Id, persisted.SourceAccountId);
        Assert.Equal(destination.Id, persisted.DestinationAccountId);
        Assert.Equal(body.Amount, persisted.Amount);
        Assert.Equal(body.OccurredOn, persisted.OccurredOn);
        Assert.Equal(body.Description, persisted.Description);
        Assert.Equal('"' + Convert.ToBase64String(persisted.RowVersion) + '"', canceled.Headers.ETag.ToString());
        Assert.Equal(1, await db.Transfers.CountAsync(value => value.UserId == owner, Cancellation));
        Assert.Equal(0, await db.FinancialTransactions.CountAsync(value => value.UserId == owner, Cancellation));
        var snapshot = await db.IdempotencyRecords.AsNoTracking().SingleAsync(value => value.UserId == owner
            && value.Operation == TransferCreationFingerprint.Operation && value.Key == key, Cancellation);
        Assert.Equal(await created.Content.ReadAsStringAsync(Cancellation), snapshot.ResponseBody);
        Assert.Equal(created.Headers.ETag.ToString(), snapshot.ETag);
        Assert.Equal(created.Headers.Location!.ToString(), snapshot.Location);
        Assert.Equal(created.Content.Headers.ContentType!.ToString(), snapshot.ContentType);
        Assert.Equal(201, snapshot.StatusCode);
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("", 400)]
    [InlineData("*", 400)]
    [InlineData("W/\"AAAAAAAAAAA=\"", 400)]
    [InlineData("\"AAAAAAAAAAA=\", \"AAAAAAAAAAE=\"", 400)]
    [InlineData("\"not-base64!!\"", 400)]
    [InlineData("\"AQ==\"", 400)]
    [InlineData("\"AAAAAAAAAAB=\"", 400)]
    [InlineData("\"AAAAAAAAAAA=\"", 412)]
    public async Task Invalid_or_stale_preconditions_never_cancel_or_change_balances(string? version, int status)
    {
        using var factory = new TransferCancellationApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 10.01m, new(2000, 1, 1), null));

        // HttpClient/TestServer can omit an empty value. Preserve that value on the incoming
        // request, as in the existing financial mutation tests, without bypassing JWT.
        using var headerClient = new HttpClient(factory.Server.CreateHandler(context =>
        {
            if (version == string.Empty)
            {
                context.Request.Headers["If-Match"] = string.Empty;
            }
        })) { BaseAddress = client.BaseAddress };
        headerClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        using var response = await CancelAsync(headerClient, created.Headers.Location!, version);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var get = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(created.Headers.ETag, get.Headers.ETag);
        Assert.Equal(-10.01m, await BalanceAsync(client, source.Id));
        Assert.Equal(10.01m, await BalanceAsync(client, destination.Id));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"isCanceled\":true,\"amount\":100}")]
    public async Task Body_is_rejected_before_preconditions_without_applying_client_fields(string body)
    {
        using var factory = new TransferCancellationApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null));
        foreach (var version in new[] { null, created.Headers.ETag!.ToString() })
        {
            using var response = await CancelAsync(client, created.Headers.Location!, version, body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        using var get = await client.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(created.Headers.ETag, get.Headers.ETag);
    }

    [Fact]
    public async Task Foreign_and_missing_transfers_have_the_same_safe_404_without_changing_the_owner_balance()
    {
        using var factory = new TransferCancellationApiFactory(fixture);
        using var owner = factory.CreateClient();
        using var stranger = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(owner);
        await CategoryTestRequests.AuthenticateAsync(stranger);
        var source = await AccountAsync(owner);
        var destination = await AccountAsync(owner);
        using var created = await PostTransferAsync(owner, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null));

        using var foreign = await CancelAsync(stranger, created.Headers.Location!, created.Headers.ETag!.ToString());
        using var missing = await CancelAsync(stranger, new Uri("/api/v1/transfers/" + Guid.NewGuid(), UriKind.Relative), created.Headers.ETag.ToString());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(foreign.StatusCode, missing.StatusCode);
        var foreignProblem = await ReadAsync(foreign);
        var missingProblem = await ReadAsync(missing);
        foreach (var field in new[] { "status", "code", "title" })
        {
            Assert.Equal(foreignProblem.GetProperty(field).ToString(), missingProblem.GetProperty(field).ToString());
        }
        Assert.True(foreign.Headers.CacheControl?.NoStore);
        using var get = await owner.GetAsync(created.Headers.Location, Cancellation);
        Assert.Equal(created.Headers.ETag, get.Headers.ETag);
        Assert.Equal(-10m, await BalanceAsync(owner, source.Id));
        Assert.Equal(10m, await BalanceAsync(owner, destination.Id));
    }

    [Fact]
    public async Task Revoked_token_cannot_cancel_an_existing_transfer()
    {
        using var factory = new TransferCancellationApiFactory(fixture);
        using var client = factory.CreateClient();
        var owner = await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 10m, new(2000, 1, 1), null));
        using var logout = await client.PostAsync("/api/v1/auth/logout", null, Cancellation);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var rejected = await CancelAsync(client, created.Headers.Location!, created.Headers.ETag!.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal("Bearer", Assert.Single(rejected.Headers.WwwAuthenticate).Scheme);
        Assert.True(rejected.Headers.CacheControl?.NoStore);
        await using var scope = factory.Services.CreateAsyncScope();
        var value = await scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>().Transfers.AsNoTracking()
            .SingleAsync(value => value.UserId == owner, Cancellation);
        Assert.False(value.IsCanceled);
        Assert.Null(value.CanceledAtUtc);
    }

    [Fact]
    public async Task Authenticated_cancellation_uses_the_existing_global_rate_limit_and_safe_retry_after()
    {
        using var factory = new TransferCancellationApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var missing = new Uri("/api/v1/transfers/" + Guid.NewGuid(), UriKind.Relative);
        var rejected = false;

        // Exercise the real 120-request policy without changing production limits or sleeping.
        for (var attempt = 0; attempt < 121; attempt++)
        {
            using var response = await CancelAsync(client, missing, "\"AAAAAAAAAAA=\"");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                rejected = true;
                break;
            }
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.True(rejected);
    }

    [Fact]
    public async Task Cancellation_is_allowed_when_it_leaves_the_inactive_destination_with_a_negative_balance()
    {
        using var factory = new TransferCancellationApiFactory(fixture);
        using var client = factory.CreateClient();
        await CategoryTestRequests.AuthenticateAsync(client);
        var source = await AccountAsync(client);
        var destination = await AccountAsync(client);
        using var expense = await IncomeExpenseAsync(client, destination.Id, "expense", 750m);
        using var created = await PostTransferAsync(client, new(source.Id, destination.Id, 500m, new(2000, 1, 3), null));
        Assert.Equal(-500m, await BalanceAsync(client, source.Id));
        Assert.Equal(-250m, await BalanceAsync(client, destination.Id));
        await TransactionMutationRequests.DeactivateAsync(client, destination.Location, destination.Version);

        using var canceled = await CancelAsync(client, created.Headers.Location!, created.Headers.ETag!.ToString());

        await AssertSuccessAsync(canceled);
        Assert.Equal(0m, await BalanceAsync(client, source.Id));
        Assert.Equal(-750m, await BalanceAsync(client, destination.Id));
        Assert.Equal(-750m, await BalanceAsync(client, destination.Id, "2000-01-03"));
    }
}

internal static class TransferCancellationRequests
{
    internal static async Task<HttpResponseMessage> CancelAsync(HttpClient client, Uri location, string? version, string? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, location);
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", version);
        }
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return await client.SendAsync(request, Cancellation);
    }

    internal static async Task AssertSuccessAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(Cancellation));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag.IsWeak);
        Assert.Equal(8, Convert.FromBase64String(response.Headers.ETag.Tag.Trim('"')).Length);
    }
}

internal sealed class TransferCancellationClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class TransferCancellationApiFactory(IdentitySqlFixture fixture, params IInterceptor[] interceptors) : ApiFactory()
{
    public override string ConnectionString => fixture.ApplicationConnectionString;
    public TransferCancellationClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // Freeze only the financial service, leaving JWT validation/issuance on its real clock.
            services.AddScoped(provider => new TransfersService(provider.GetRequiredService<ITransferStore>(),
                provider.GetRequiredService<ICurrentUser>(), Clock, provider.GetRequiredService<FinancialCalendar>()));
            services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(interceptors));
        });
    }
}
