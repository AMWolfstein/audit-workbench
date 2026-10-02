using AuditWorkbench.Application.Dashboard;
using AuditWorkbench.Application.DemoData;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Infrastructure.Persistence;
using AuditWorkbench.Infrastructure.Workspace;

namespace AuditWorkbench.Application.Tests;

public class WorkspaceIntegrityTests
{
    [Fact]
    public async Task FreshWorkspaceIsCreatedWithoutAdministratorRights()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        Assert.True(File.Exists(workspace.Paths.DatabasePath));
        Assert.True(Directory.Exists(workspace.Paths.BackupsDirectory));
        Assert.True(Directory.Exists(workspace.Paths.LogsDirectory));
        Assert.True(Directory.Exists(workspace.Paths.AttachmentsDirectory));
    }

    [Fact]
    public async Task AllMigrationsAreRecordedWithTheirChecksums()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        var applied = await workspace.ScalarAsync("SELECT COUNT(*) FROM schema_migration");
        Assert.Equal(SqlResources.Migrations().Count, applied);
        Assert.Equal(SqlMigrationRunner.SchemaVersion,
            await workspace.TextScalarAsync("SELECT max(migration_id) FROM schema_migration"));
        Assert.Equal(0, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM schema_migration WHERE checksum IS NULL OR length(checksum) <> 64"));
    }

    [Fact]
    public async Task InitializationIsIdempotent()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        var report = await workspace.UseAsync(scope =>
            scope.GetRequiredService<WorkspaceInitializer>().InitializeAsync());

        Assert.True(report.IsHealthy);
        Assert.Empty(report.IntegrityProblems);
        Assert.Empty(report.AppliedMigrations); // nothing left to apply the second time
        Assert.Equal(SqlMigrationRunner.SchemaVersion, report.SchemaVersion);
        Assert.Equal(SqlResources.Migrations().Count, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM schema_migration"));
    }

    [Fact]
    public async Task ConnectionPolicyIsAppliedToEveryConnection()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        Assert.Equal(1, await workspace.ScalarAsync("PRAGMA foreign_keys"));
        await using var connection = workspace.OpenRawConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (await command.ExecuteScalarAsync())?.ToString()?.ToLowerInvariant());
    }

    [Fact]
    public async Task DemoDataMatchesTheDocumentedScenario()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        var result = await workspace.UseAsync(scope =>
            scope.GetRequiredService<DemoDataSeeder>().SeedAsync());

        Assert.Equal(1, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM company WHERE short_name = 'ABC-DEMO' AND legal_name LIKE '%(Demo)%'"));
        Assert.Equal(3, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM account WHERE engagement_id = $id", ("$id", result.PriorEngagementId.ToString("D"))));
        Assert.Equal(1, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM engagement WHERE engagement_id = $id AND status = 'FINALIZED'",
            ("$id", result.PriorEngagementId.ToString("D"))));
        Assert.Equal(1, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM engagement WHERE engagement_id = $id AND status <> 'FINALIZED'",
            ("$id", result.CurrentEngagementId.ToString("D"))));
        Assert.Equal(92_000_000_000L, await workspace.ScalarAsync(
            "SELECT amount_minor FROM financial_data f JOIN account a ON a.account_id = f.account_id " +
            "WHERE f.engagement_id = $id AND a.account_code = '4000'",
            ("$id", result.CurrentEngagementId.ToString("D"))));
    }

    [Fact]
    public async Task DemoDataCannotBeSeededTwice()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.SeedDemoAsync();

        var error = await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<DemoDataSeeder>().SeedAsync()));

        Assert.Contains("demo", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await workspace.ScalarAsync("SELECT COUNT(*) FROM company"));
    }

    [Fact]
    public async Task DashboardReportsTheWorkspaceState()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.SeedDemoAsync();

        var dashboard = await workspace.UseAsync(scope =>
            scope.GetRequiredService<DashboardService>().GetAsync());

        Assert.Equal(1, dashboard.CompanyCount);
        Assert.Equal(2, dashboard.EngagementCount);
        Assert.Equal(1, dashboard.FinalizedEngagementCount);
        Assert.Equal(1, dashboard.OpenEngagementCount);
        Assert.True(dashboard.AuditChainValid);
        Assert.True(dashboard.AuditEventCount > 0);
        Assert.True(dashboard.DemoDataPresent);
        Assert.NotEmpty(dashboard.RecentEvents);
        Assert.NotEmpty(dashboard.RecentEngagements);
        Assert.False(string.IsNullOrWhiteSpace(dashboard.WorkspaceRoot));
    }

    [Fact]
    public async Task StrictTablesRejectWrongTypesAndUnknownEnumValues()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();

        var statusError = await workspace.ExpectRawFailureAsync(
            "UPDATE company SET status = 'DELETED' WHERE company_id = $id", ("$id", companyId.ToString("D")));
        Assert.Contains("CHECK", statusError.Message, StringComparison.OrdinalIgnoreCase);

        var typeError = await workspace.ExpectRawFailureAsync(
            "INSERT INTO financial_year (financial_year_id, year_label, period_start, period_end, created_at_utc) " +
            "VALUES ('y1', 'FY2026', 20260101, '2026-12-31', '2026-01-01T00:00:00.000Z')");
        Assert.Contains("TEXT", typeError.Message, StringComparison.OrdinalIgnoreCase);
    }
}
