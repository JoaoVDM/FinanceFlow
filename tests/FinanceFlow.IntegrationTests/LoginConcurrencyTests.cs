using System.Data;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class LoginConcurrencyTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Login_cannot_mint_a_new_version_for_an_old_password()
    {
        var observer = new LockAttemptObserver();
        using var factory = new ConcurrentApiFactory(fixture, observer);
        using var client = factory.CreateClient();
        var (email, password, token) = await IdentitySqlTests.RegisterAndLoginAsync(client);
        await using var connection = new SqlConnection(fixture.MigrationConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT [Id] FROM dbo.AspNetUsers WITH (UPDLOCK, ROWLOCK) WHERE [Id] = @id";
            command.Parameters.AddWithValue("@id", Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Subject));
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }
        observer.Arm();
        var newPassword = IdentitySqlFixture.RandomPassword();
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        { Content = JsonContent.Create(new { email, password }) };
        using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users/me/change-password")
        { Content = JsonContent.Create(new { currentPassword = password, newPassword }) };
        changeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var login = client.SendAsync(loginRequest, TestContext.Current.CancellationToken);
        var change = client.SendAsync(changeRequest, TestContext.Current.CancellationToken);
        try
        {
            // Real SQL row lock holds both operations. The interceptor observes commands, not fake results.
            await observer.BothAttempted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally { await transaction.CommitAsync(TestContext.Current.CancellationToken); }
        using var loginResponse = await login;
        using var changeResponse = await change;
        Assert.Equal(HttpStatusCode.NoContent, changeResponse.StatusCode);
        Assert.True(loginResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized);
        if (loginResponse.IsSuccessStatusCode)
        {
            using var json = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            client.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("accessToken").GetString());
            using var oldSession = await client.GetAsync("/api/v1/users/me", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = null;
        await IdentitySqlTests.LoginAsync(client, email, newPassword);
        using var oldPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
    }

    private sealed class ConcurrentApiFactory(IdentitySqlFixture sql, LockAttemptObserver observer) : ApiFactory()
    {
        public override string ConnectionString => sql.ApplicationConnectionString;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddDbContext<FinanceFlowDbContext>(options => options.AddInterceptors(observer)));
        }
    }

    private sealed class LockAttemptObserver : DbCommandInterceptor
    {
        private int remaining;
        private readonly TaskCompletionSource bothAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BothAttempted => bothAttempted.Task;
        public void Arm() => Interlocked.Exchange(ref remaining, 2);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal) && Interlocked.Decrement(ref remaining) == 0)
                bothAttempted.TrySetResult();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
