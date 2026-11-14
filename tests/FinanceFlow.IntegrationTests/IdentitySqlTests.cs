using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceFlow.Application.Users;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinanceFlow.IntegrationTests;

[Collection("identity-sql")]
public sealed class IdentitySqlTests(IdentitySqlFixture fixture)
{
    [Fact]
    public async Task Migration_and_application_permissions_are_real()
    {
        await DatabaseTasks.VerifyApplicationPrivilegesAsync(
            fixture.ApplicationConnectionString,
            TestContext.Current.CancellationToken);
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        Assert.Contains(
            "20261113000100_InitialIdentity",
            await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(db.Database.HasPendingModelChanges());
        await using var connection = new SqlConnection(fixture.ApplicationConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT HAS_PERMS_BY_NAME('dbo.AspNetUsers','OBJECT','INSERT'), HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE'), HAS_PERMS_BY_NAME('dbo.__EFMigrationsHistory','OBJECT','UPDATE'), HAS_PERMS_BY_NAME('dbo.AspNetUsers','OBJECT','DELETE')";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("algorithm")]
    [InlineData("signature")]
    [InlineData("missing-subject")]
    [InlineData("missing-version")]
    [InlineData("missing-expiration")]
    [InlineData("missing-not-before")]
    [InlineData("missing-id")]
    [InlineData("missing-issued-at")]
    [InlineData("tampered")]
    public async Task Invalid_jwt_is_rejected_with_an_active_persisted_user(string variant)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var (_, _, validToken) = await RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", validToken);
        using var positiveControl = await client.GetAsync("/api/v1/users/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, positiveControl.StatusCode);
        var subject = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(validToken).Subject);
        // A real active user/version prevents database failure from masking broken JWT validation.
        var invalidToken = AuthenticationSecurityTests.CreateToken(factory, subject, 0, variant);
        client.DefaultRequestHeaders.Authorization = new("Bearer", invalidToken);
        using var response = await client.GetAsync("/api/v1/users/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(body.Contains(invalidToken, StringComparison.Ordinal));
        Assert.DoesNotContain("IDX", body);
    }

    [Fact]
    public async Task Application_account_with_additional_delete_grant_is_rejected()
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        {
            InitialCatalog = "FinanceFlowExtraGrant_" + Guid.NewGuid().ToString("N")
        };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            InitialCatalog = migration.InitialCatalog
        };
        await DatabaseTasks.ProvisionAsync(
            fixture.AdminConnectionString,
            migration.ConnectionString,
            application.ConnectionString,
            TestContext.Current.CancellationToken);
        await DatabaseTasks.MigrateAsync(migration.ConnectionString, TestContext.Current.CancellationToken);
        await DatabaseTasks.ProvisionAsync(
            fixture.AdminConnectionString,
            migration.ConnectionString,
            application.ConnectionString,
            TestContext.Current.CancellationToken);
        var admin = new SqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            InitialCatalog = migration.InitialCatalog
        };
        await using var connection = new SqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        // This grant affects only the disposable test database, never the local development database.
        command.CommandText = "GRANT DELETE ON OBJECT::dbo.AspNetUsers TO FinanceFlowApi";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseTasks.VerifyApplicationPrivilegesAsync(
            application.ConnectionString,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Available_database_without_schema_is_not_ready()
    {
        var migration = new SqlConnectionStringBuilder(fixture.MigrationConnectionString)
        {
            InitialCatalog = "FinanceFlowEmpty_" + Guid.NewGuid().ToString("N")
        };
        var application = new SqlConnectionStringBuilder(fixture.ApplicationConnectionString)
        {
            InitialCatalog = migration.InitialCatalog
        };
        await DatabaseTasks.ProvisionAsync(
            fixture.AdminConnectionString,
            migration.ConnectionString,
            application.ConnectionString,
            TestContext.Current.CancellationToken);
        using var factory = new SqlApiFactory(fixture, application.ConnectionString);
        using var client = factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task Register_normalizes_email_persists_hash_and_rejects_duplicates()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var email = Email();
        var password = IdentitySqlFixture.RandomPassword();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { displayName = "Fictional Person", email, password },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/users/me", response.Headers.Location?.OriginalString);
        var profile = await response.Content.ReadFromJsonAsync<UserProfile>(TestContext.Current.CancellationToken);
        Assert.NotNull(profile);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("passwordHash", body);
        Assert.False(body.Contains(password, StringComparison.Ordinal));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == profile.Id, TestContext.Current.CancellationToken);
        Assert.Equal(email.ToUpperInvariant(), user.NormalizedEmail);
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash));
        Assert.False(string.Equals(password, user.PasswordHash, StringComparison.Ordinal));
        Assert.True(user.IsActive);
        using var duplicate = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { displayName = "Other", email = email.ToUpperInvariant(), password },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(129)]
    public async Task Password_lengths_outside_contract_are_rejected(int length)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { displayName = "Fictional", email = Email(), password = new string('x', length) },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Concurrent_registration_creates_only_one_normalized_email()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var command = new
        {
            displayName = "Fictional",
            email = Email(),
            password = IdentitySqlFixture.RandomPassword()
        };
        var attempts = await Task.WhenAll(
            client.PostAsJsonAsync("/api/v1/auth/register", command, TestContext.Current.CancellationToken),
            client.PostAsJsonAsync("/api/v1/auth/register", command, TestContext.Current.CancellationToken));
        try
        {
            Assert.Equal(new[] { 201, 409 }, attempts.Select(x => (int)x.StatusCode).Order().ToArray());
        }
        finally
        {
            foreach (var response in attempts)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Profile_uses_only_authenticated_subject_and_rejects_mass_assignment()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var (_, _, token) = await RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var before = await client.GetAsync("/api/v1/users/me", TestContext.Current.CancellationToken);
        var profile = await before.Content.ReadFromJsonAsync<UserProfile>(TestContext.Current.CancellationToken);
        Assert.NotNull(profile);
        using var invalid = await client.PatchAsJsonAsync(
            "/api/v1/users/me",
            new { displayName = "Changed", userId = Guid.NewGuid(), isActive = false },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var update = await client.PatchAsJsonAsync(
            "/api/v1/users/me",
            new { displayName = "Changed" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var changed = await client.GetFromJsonAsync<UserProfile>("/api/v1/users/me", TestContext.Current.CancellationToken);
        Assert.Equal(profile.Id, changed?.Id);
        Assert.Equal("Changed", changed?.DisplayName);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("logout")]
    [InlineData("deactivate")]
    public async Task Security_changes_revoke_all_previous_tokens(string operation)
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var (email, password, token) = await RegisterAndLoginAsync(client);
        var secondToken = await LoginAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var newPassword = IdentitySqlFixture.RandomPassword();
        HttpResponseMessage response;
        if (operation == "password")
        {
            response = await client.PostAsJsonAsync(
                "/api/v1/users/me/change-password",
                new { currentPassword = password, newPassword },
                TestContext.Current.CancellationToken);
        }
        else if (operation == "logout")
        {
            response = await client.PostAsync("/api/v1/auth/logout", null, TestContext.Current.CancellationToken);
        }
        else
        {
            // HttpClient sends a JSON confirmation body on DELETE; no contract change.
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/users/me")
            {
                Content = JsonContent.Create(new { currentPassword = password })
            };
            response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        using (response)
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        foreach (var oldToken in new[]
        {
            token,
            secondToken
        })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", oldToken);
            using var rejected = await client.GetAsync("/api/v1/users/me", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        client.DefaultRequestHeaders.Authorization = null;
        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password = operation == "password" ? newPassword : password },
            TestContext.Current.CancellationToken);
        Assert.Equal(operation == "deactivate" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, login.StatusCode);
        if (operation == "deactivate")
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FinanceFlowDbContext>();
            var user = await db.Users.SingleAsync(
                x => x.NormalizedEmail == email.ToUpperInvariant(),
                TestContext.Current.CancellationToken);
            Assert.False(user.IsActive);
            Assert.NotNull(user.DeactivatedAtUtc); // User persists; no physical deletion.
        }
    }

    [Fact]
    public async Task Identity_lockout_blocks_correct_password_after_five_failures()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var (email, password, _) = await RegisterAndLoginAsync(client);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // Exercise real Identity/SQL without confusing IP throttling with account lockout.
            await using var scope = factory.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IUserAccounts>().LoginAsync(
                new(email, IdentitySqlFixture.RandomPassword()),
                TestContext.Current.CancellationToken);
            Assert.Equal(AccountError.InvalidCredentials, result.Error);
        }

        using var locked = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    }

    [Fact]
    public async Task Valid_authenticated_user_receives_403_when_policy_denies()
    {
        using var factory = new SqlApiFactory(fixture);
        using var client = factory.CreateClient();
        var (_, _, token) = await RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var response = await client.GetAsync("/api/v1/__tests/authorization", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    internal static async Task<(string Email, string Password, string Token)> RegisterAndLoginAsync(HttpClient client)
    {
        var email = Email();
        var password = IdentitySqlFixture.RandomPassword();
        using var registration = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { displayName = "Fictional Test User", email, password },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        return (email, password, await LoginAsync(client, email, password));
    }

    internal static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        using var login = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(900, json.RootElement.GetProperty("expiresIn").GetInt32());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static string Email() => $"person-{Guid.NewGuid():N}@example.invalid";
}
