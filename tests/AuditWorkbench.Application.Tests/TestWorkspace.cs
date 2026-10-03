using AuditWorkbench.Application.Companies;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Finalization;
using AuditWorkbench.Application.FinancialData;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Infrastructure.Persistence;
using AuditWorkbench.Infrastructure.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AuditWorkbench.Application.Tests;

/// <summary>
/// A real on-disk SQLite workspace per test (architecture.md section 10: the
/// EF in-memory provider cannot prove constraints, triggers or transactions).
/// </summary>
public sealed class TestWorkspace : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TestWorkspace(string root, ServiceProvider provider)
    {
        Root = root;
        _provider = provider;
        Paths = new WorkspacePaths(root);
    }

    public string Root { get; }

    public WorkspacePaths Paths { get; }

    public static async Task<TestWorkspace> CreateAsync(ICurrentActor? actor = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "awb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuditWorkbench(new WorkspacePaths(root), actor);
        var provider = services.BuildServiceProvider();

        var workspace = new TestWorkspace(root, provider);
        await workspace.UseAsync(async scope =>
        {
            var initializer = scope.GetRequiredService<WorkspaceInitializer>();
            var report = await initializer.InitializeAsync();
            if (!report.IsHealthy)
            {
                throw new InvalidOperationException("Test workspace failed its integrity checks.");
            }
        });

        return workspace;
    }

    /// <summary>Runs one unit of work in its own scope, like one HTTP request.</summary>
    public async Task UseAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = _provider.CreateScope();
        await action(scope.ServiceProvider);
    }

    public async Task<T> UseAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _provider.CreateScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Opens a raw connection that bypasses every application-level guard.</summary>
    public SqliteConnection OpenRawConnection()
    {
        var connection = new SqliteConnection(Paths.ConnectionString);
        connection.Open();
        SqliteConnectionPolicyInterceptor.Apply(connection);
        return connection;
    }

    public async Task<SqliteException> ExpectRawFailureAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = OpenRawConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Assert.ThrowsAny<SqliteException>(() => command.ExecuteNonQuery());
    }

    public async Task ExecuteRawAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = OpenRawConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async Task<string?> TextScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = OpenRawConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (await command.ExecuteScalarAsync())?.ToString();
    }

    public async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = OpenRawConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    // -- scenario helpers --------------------------------------------------

    public Task<Guid> CreateCompanyAsync(string shortName = "ABC-DEMO") => UseAsync(scope =>
        scope.GetRequiredService<CompanyService>().CreateAsync(new CreateCompanyCommand(
            $"{shortName} (Demo) Limited", shortName, "Manufacturing", "ZZ", "DEMO-TAX-0000001")));

    public Task<Guid> CreateYearAsync(Guid companyId, string label, int year, Guid? prior = null) =>
        UseAsync(scope => scope.GetRequiredService<EngagementService>().CreateAsync(new CreateEngagementCommand(
            companyId, label, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31),
            EngagementStatus.Draft, "USD", 2, prior)));

    public Task<Guid> AddAccountAsync(Guid engagementId, string code, string name, string? amount) =>
        UseAsync(scope => scope.GetRequiredService<FinancialDataService>().AddAccountAsync(
            new AddAccountCommand(engagementId, code, name, "UNCLASSIFIED", amount)));

    public Task<Guid> RecordValueAsync(Guid engagementId, Guid accountId, string amount, string? reason = null) =>
        UseAsync(scope => scope.GetRequiredService<FinancialDataService>().RecordValueAsync(
            new RecordValueCommand(engagementId, accountId, amount, reason, null)));

    public Task<string> FinalizeAsync(Guid engagementId) => UseAsync(async scope =>
    {
        var engagements = scope.GetRequiredService<EngagementService>();
        var finalization = scope.GetRequiredService<FinalizationService>();
        var summary = await engagements.GetAsync(engagementId);
        return await finalization.FinalizeAsync(engagementId, FinalizationService.ConfirmationPhrase(summary));
    });

    /// <summary>Creates the documented FY2026 (finalized) + FY2027 (linked draft) scenario.</summary>
    public async Task<(Guid CompanyId, Guid PriorId, Guid CurrentId)> SeedDemoAsync()
    {
        var result = await UseAsync(scope =>
            scope.GetRequiredService<DemoData.DemoDataSeeder>().SeedAsync());
        return (result.CompanyId, result.PriorEngagementId, result.CurrentEngagementId);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A locked temporary file must never fail a test run.
        }
    }
}
