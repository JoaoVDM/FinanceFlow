using System.Security.Cryptography;
using FinanceFlow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace FinanceFlow.IntegrationTests;

[CollectionDefinition("identity-sql", DisableParallelization = true)]
public sealed class IdentitySqlCollection : ICollectionFixture<IdentitySqlFixture>
{
}

public sealed class IdentitySqlFixture : IAsyncLifetime
{
    public const string SqlImage = "mcr.microsoft.com/mssql/server:2022-CU22-GDR1-ubuntu-22.04";
    private readonly MsSqlContainer container = new MsSqlBuilder(SqlImage)
        .WithPassword(RandomPassword())
        .WithCreateParameterModifier(parameters =>
        {
            var hostConfig = parameters.HostConfig
                ?? throw new InvalidOperationException("The SQL test container requires host configuration.");
            if (hostConfig.PortBindings is not { } ports
                || !ports.TryGetValue("1433/tcp", out var bindings)
                || bindings is null || bindings.Count == 0)
                throw new InvalidOperationException("The SQL test container requires an explicit port binding.");
            foreach (var binding in bindings)
            {
                if (binding is null)
                    throw new InvalidOperationException("The SQL test container has an invalid port binding.");
                binding.HostIP = "127.0.0.1";
            }
        }).Build();

    public string ApplicationConnectionString { get; private set; } = string.Empty;
    public string MigrationConnectionString { get; private set; } = string.Empty;
    public string AdminConnectionString => container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        // No skip or fallback: Docker unavailability must fail this mandatory fixture.
        await container.StartAsync(TestContext.Current.CancellationToken);
        var builder = new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = "FinanceFlowTest_" + Guid.NewGuid().ToString("N"),
            UserID = "FinanceFlowMigrator", Password = RandomPassword(), Encrypt = true, TrustServerCertificate = true
        };
        MigrationConnectionString = builder.ConnectionString;
        builder.UserID = "FinanceFlowApi";
        builder.Password = RandomPassword();
        ApplicationConnectionString = builder.ConnectionString;
        await DatabaseTasks.ProvisionAsync(container.GetConnectionString(), MigrationConnectionString, ApplicationConnectionString, TestContext.Current.CancellationToken);
        await DatabaseTasks.MigrateAsync(MigrationConnectionString, TestContext.Current.CancellationToken);
        await DatabaseTasks.ProvisionAsync(container.GetConnectionString(), MigrationConnectionString, ApplicationConnectionString, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
    public static string RandomPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "Aa1!";
}

public sealed class SqlApiFactory(IdentitySqlFixture fixture, string? connectionString = null) : ApiFactory()
{
    public override string ConnectionString => connectionString ?? fixture.ApplicationConnectionString;
}
