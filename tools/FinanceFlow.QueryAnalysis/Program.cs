using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using FinanceFlow.Application.Accounts;
using FinanceFlow.Application.Categories;
using FinanceFlow.Application.Transactions;
using FinanceFlow.Application.Transfers;
using FinanceFlow.Infrastructure.Persistence;
using FinanceFlow.Infrastructure.Persistence.Accounts;
using FinanceFlow.Infrastructure.Persistence.Categories;
using FinanceFlow.Infrastructure.Persistence.Reports;
using FinanceFlow.Infrastructure.Persistence.Statements;
using FinanceFlow.Infrastructure.Persistence.Transactions;
using FinanceFlow.Infrastructure.Persistence.Transfers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FinanceFlow.QueryAnalysis;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static readonly DateOnly From = new(2026, 11, 1);
    internal static readonly DateOnly To = new(2026, 11, 26);

    public static async Task<int> Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 1 ? args[1] : "/results");
        Directory.CreateDirectory(output);
        if (args.FirstOrDefault() == "--offline")
        {
            OfflineSql.Write(output);
            Console.WriteLine("Offline SQL/model inspection only; no connection opened.");
            return 0;
        }
        // Never accept a caller-supplied connection string or existing local database.
        var server = Environment.GetEnvironmentVariable("PERF_SQL_SERVER") ?? "";
        var password = Environment.GetEnvironmentVariable("PERF_SQL_PASSWORD") ?? "";
        if (Environment.GetEnvironmentVariable("PERF_DISPOSABLE") != "1"
            || !server.StartsWith("financeflow-7c-sql-", StringComparison.Ordinal)
            || server.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') || password.Length < 32)
        {
            Console.Error.WriteLine("Use Test-QueryPerformance.ps1 with its dedicated disposable SQL container.");
            return 2;
        }
        var stage = "sql-readiness";
        try
        {
            var admin = new SqlConnectionStringBuilder { DataSource = server, UserID = "sa", Password = password,
                TrustServerCertificate = true, Encrypt = true, ConnectTimeout = 2, Pooling = false };
            var ready = false;
            for (var attempt = 0; attempt < 90; attempt++)
            {
                try
                {
                    await using var probe = new SqlConnection(admin.ConnectionString);
                    await probe.OpenAsync();
                    ready = true;
                    break;
                }
                catch (SqlException)
                {
                    // Readiness polling only, never evidence of concurrent ordering.
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
            if (!ready) throw new TimeoutException("Disposable SQL did not become ready.");
            // SQL logins are server-scoped. Provisioning intentionally never changes an
            // existing login password, so use one credential per role for this whole run.
            var migrationPassword = IdentityPassword();
            var applicationPassword = IdentityPassword();
            var sizes = new[] { 200, 40000 };
            var completedScenarios = 0;
            foreach (var size in sizes)
            {
                var root = Path.Combine(output, size.ToString());
                Directory.CreateDirectory(root);
                var database = "FinanceFlowPerf_" + Guid.NewGuid().ToString("N");
                var migration = new SqlConnectionStringBuilder(admin.ConnectionString) { InitialCatalog = database,
                    UserID = "FinanceFlowPerfMigrator", Password = migrationPassword };
                var application = new SqlConnectionStringBuilder(migration.ConnectionString) { UserID = "FinanceFlowPerfApi",
                    Password = applicationPassword, Pooling = true, ApplicationName = database, ConnectTimeout = 15 };
                stage = $"scale-{size}/provision-before-migration";
                await DatabaseTasks.ProvisionAsync(admin.ConnectionString, migration.ConnectionString, application.ConnectionString);
                stage = $"scale-{size}/migrate";
                await DatabaseTasks.MigrateAsync(migration.ConnectionString);
                stage = $"scale-{size}/provision-after-migration";
                await DatabaseTasks.ProvisionAsync(admin.ConnectionString, migration.ConnectionString, application.ConnectionString);
                stage = $"scale-{size}/verify-api-privileges";
                await DatabaseTasks.VerifyApplicationPrivilegesAsync(application.ConnectionString);
                var administrative = new SqlConnectionStringBuilder(admin.ConnectionString) { InitialCatalog = database };
                stage = $"scale-{size}/seed";
                await SeedData.WriteAsync(administrative.ConnectionString, size);
                await File.WriteAllTextAsync(Path.Combine(root, "distribution.json"), SeedData.Distribution);
                stage = $"scale-{size}/catalog";
                await CatalogAsync(administrative.ConnectionString, root);
                foreach (var resource in new[] { "accounts", "categories", "transactions", "transfers", "balance", "statement", "summary", "by-category" })
                foreach (var filtered in new[] { false, true })
                {
                    var prefix = resource + (filtered ? "-filtered" : "-unfiltered");
                    var capture = new Capture();
                    var options = new DbContextOptionsBuilder<FinanceFlowDbContext>().UseSqlServer(application.ConnectionString)
                        .AddInterceptors(capture).Options;
                    stage = $"scale-{size}/{prefix}/warmup";
                    for (var warm = 0; warm < 2; warm++) await RunAsync(resource, filtered, options);
                    var samples = new List<object>();
                    for (var repeat = 0; repeat < 5; repeat++)
                    {
                        stage = $"scale-{size}/{prefix}/sample-{repeat}";
                        capture.Reads.Clear();
                        capture.Resets = 0;
                        var watch = Stopwatch.StartNew();
                        await RunAsync(resource, filtered, options);
                        watch.Stop();
                        samples.Add(new { repeat, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                            financialSelects = capture.Reads.Count, sessionResetCommands = capture.Resets });
                    }
                    stage = $"scale-{size}/{prefix}/write-samples";
                    await File.WriteAllTextAsync(Path.Combine(root, prefix + ".json"), JsonSerializer.Serialize(new
                    {
                        resource, filtered, samples, includesAuthentication = false, credential = "restricted API",
                        commands = capture.Reads.Select(command => new { sql = command.Sql, isolation = command.Isolation?.ToString(),
                            parameters = command.Parameters.Select(p => new { p.ParameterName, type = p.SqlDbType.ToString(), p.Size, p.Precision, p.Scale, p.Value }) })
                    }, Json));
                    for (var index = 0; index < capture.Reads.Count; index++)
                    {
                        stage = $"scale-{size}/{prefix}/statistics-{index}";
                        await ReplayStatisticsAsync(administrative.ConnectionString, capture.Reads[index], Path.Combine(root, prefix + "-" + index));
                    }
                    completedScenarios++;
                    Console.WriteLine($"Rows={size}; {prefix}; SELECTs={capture.Reads.Count}; resets={capture.Resets}");
                }
            }
            stage = "write-completion";
            await File.WriteAllTextAsync(Path.Combine(output, "completion.json"), JsonSerializer.Serialize(new
            {
                success = true, scales = sizes, scenarios = completedScenarios, warmups = 2, repetitions = 5
            }, Json));
            Console.WriteLine("Measurements completed. Review results; no performance threshold or index recommendation is inferred automatically.");
            return 0;
        }
        catch (Exception exception)
        {
            // Stage and SQL metadata are safe; never print provider messages or connection strings.
            var cause = exception;
            while (cause is not SqlException && cause.InnerException is { } inner) cause = inner;
            var sql = cause as SqlException;
            var failure = new { stage, exceptionType = exception.GetType().Name, sqlNumber = sql?.Number,
                sqlErrors = sql?.Errors.Cast<SqlError>().Select(error => new { error.Number, error.State, error.Class }).ToArray() };
            try
            {
                await File.WriteAllTextAsync(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(failure, Json));
            }
            catch (Exception metadataFailure) when (metadataFailure is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Could not write failure.json; original failure follows.");
            }
            Console.Error.WriteLine($"Measurement failed at {stage}: {exception.GetType().Name}" + (sql is not null ? $"; SQL {sql.Number}" : ""));
            return 1;
        }
    }

    private static string IdentityPassword() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) + "Aa1!";

    private static async Task RunAsync(string resource, bool filtered, DbContextOptions<FinanceFlowDbContext> options)
    {
        await using var db = new FinanceFlowDbContext(options);
        var owner = SeedData.Owner(0);
        Guid? account = filtered ? SeedData.Account(0, 0) : null;
        switch (resource)
        {
            case "accounts":
                await new FinancialAccountStore(db, options).ListAsync(owner, new(IsActive: filtered ? true : null, Type: filtered ? "checking" : null), default);
                break;
            case "categories":
                await new CategoryStore(db).ListAsync(owner, new(Kind: filtered ? "expense" : null, IsActive: filtered ? true : null), default);
                break;
            case "transactions":
                var transactions = await new FinancialTransactionStore(db, options).ListAsync(owner,
                    new(AccountId: account, From: From, To: To, Kind: filtered ? "expense" : null,
                        MinAmount: filtered ? 1m : null, MaxAmount: filtered ? 50m : null,
                        Sort: filtered ? "amount" : "occurredOn", Direction: filtered ? "asc" : "desc"), default);
                if (transactions.Error is not null) throw new InvalidOperationException("Transaction read failed.");
                break;
            case "transfers":
                var transfers = await new TransferStore(db, options).ListAsync(owner, new(AccountId: account, From: From, To: To), default);
                if (transfers.Error is not null) throw new InvalidOperationException("Transfer read failed.");
                break;
            case "balance":
                await new FinancialTransactionStore(db, options).BalanceAsync(owner, SeedData.Account(0, 0), filtered ? From : To, default);
                break;
            case "statement":
                var statement = await new AccountStatementStore(db).GetAsync(owner, SeedData.Account(0, 0), new(filtered ? From : new(2026, 10, 1), To), default);
                if (statement.Error is not null) throw new InvalidOperationException("Statement read failed.");
                break;
            case "summary":
                var summary = await new ReportStore(db).GetSummaryAsync(owner, new(From, To, account), default);
                if (summary.Error is not null) throw new InvalidOperationException("Summary read failed.");
                break;
            default:
                var groups = await new ReportStore(db).GetByCategoryAsync(owner, new(From, To, account, filtered ? "expense" : null), default);
                if (groups.Error is not null) throw new InvalidOperationException("Category read failed.");
                break;
        }
    }

    private static async Task CatalogAsync(string connectionString, string root)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.name AS [table], i.name AS [index], i.is_unique, i.is_primary_key, i.is_unique_constraint,
                c.name AS [column], ic.key_ordinal, ic.is_included_column
            FROM sys.tables t JOIN sys.indexes i ON i.object_id=t.object_id
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE t.name IN ('FinancialAccounts','Categories','FinancialTransactions','Transfers')
            ORDER BY t.name,i.name,ic.key_ordinal,ic.index_column_id
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var entries = new List<Dictionary<string, object>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object>();
            for (var column = 0; column < reader.FieldCount; column++) row[reader.GetName(column)] = reader.GetValue(column);
            entries.Add(row);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "indexes.json"), JsonSerializer.Serialize(entries, Json));
        await reader.DisposeAsync();
        command.CommandText = "SELECT CAST(snapshot_isolation_state AS int), is_read_committed_snapshot_on FROM sys.databases WHERE name=DB_NAME()";
        await using var settings = await command.ExecuteReaderAsync();
        if (!await settings.ReadAsync() || settings.GetInt32(0) != 1 || settings.GetBoolean(1))
            throw new InvalidOperationException("Expected Snapshot ON and RCSI OFF in disposable measurement DB.");
        await File.WriteAllTextAsync(Path.Combine(root, "environment.json"), JsonSerializer.Serialize(new
        {
            sqlServerVersion = connection.ServerVersion, runtimeVersion = Environment.Version.ToString(),
            efCoreAssemblyVersion = typeof(DbContext).Assembly.GetName().Version?.ToString(),
            sqlClientAssemblyVersion = typeof(SqlConnection).Assembly.GetName().Version?.ToString(),
            snapshotIsolation = true, readCommittedSnapshot = false, warmups = 2, repetitions = 5,
            plans = "Administrative SHOWPLAN diagnostic replay of captured SQL/parameters, with the captured isolation. Store timings use restricted API credentials."
        }, Json));
    }

    private static async Task ReplayStatisticsAsync(string connectionString, Captured command, string prefix)
    {
        await using var connection = new SqlConnection(connectionString);
        var messages = new List<string>();
        connection.InfoMessage += (_, args) => messages.Add(args.Message);
        await connection.OpenAsync();
        await using var transaction = command.Isolation == IsolationLevel.Snapshot
            ? (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Snapshot) : null;
        await using var replay = connection.CreateCommand();
        replay.Transaction = transaction;
        replay.CommandTimeout = 120;
        // Administrative diagnostic replay for SHOWPLAN only. API store timings above use restricted credentials.
        replay.CommandText = "SET STATISTICS IO ON; SET STATISTICS TIME ON; SET STATISTICS XML ON;\n" + command.Sql;
        foreach (var parameter in command.Parameters) replay.Parameters.Add(((ICloneable)parameter).Clone());
        await using (var reader = await replay.ExecuteReaderAsync())
        {
            var plan = 0;
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase))
                    {
                        var xml = reader.GetValue(0) switch
                        {
                            string text => text,
                            System.Data.SqlTypes.SqlXml value => value.Value,
                            _ => throw new InvalidOperationException("Unexpected Showplan result type.")
                        };
                        await File.WriteAllTextAsync(prefix + $"-{plan++}.sqlplan", xml);
                    }
                }
            } while (await reader.NextResultAsync());
        }
        if (transaction is not null) await transaction.CommitAsync();
        await File.WriteAllLinesAsync(prefix + "-statistics.txt", messages);
    }

    private sealed record Captured(string Sql, SqlParameter[] Parameters, IsolationLevel? Isolation);
    private sealed class Capture : DbCommandInterceptor
    {
        public List<Captured> Reads { get; } = [];
        public int Resets { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads.Add(new(command.CommandText, command.Parameters.Cast<SqlParameter>().Select(p => (SqlParameter)((ICloneable)p).Clone()).ToArray(), command.Transaction?.IsolationLevel));
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SET TRANSACTION ISOLATION LEVEL READ COMMITTED", StringComparison.Ordinal)) Resets++;
            return ValueTask.FromResult(result);
        }
    }
}
