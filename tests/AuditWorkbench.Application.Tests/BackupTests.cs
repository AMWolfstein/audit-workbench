using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Backup;
using AuditWorkbench.Infrastructure.Backup;
using Microsoft.Data.Sqlite;

namespace AuditWorkbench.Application.Tests;

public class BackupTests
{
    [Fact]
    public async Task BackupLandsInTheWorkspaceFolderWithoutElevation()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.SeedDemoAsync();

        var package = await workspace.UseAsync(scope =>
            scope.GetRequiredService<BackupService>().CreateAsync());

        Assert.StartsWith(workspace.Paths.BackupsDirectory, package.Directory, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(package.Directory, "workspace.db")));
        Assert.True(File.Exists(Path.Combine(package.Directory, "backup-manifest.json")));
        Assert.True(package.SizeBytes > 0);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Paths.BackupsDirectory, ".*.partial"));
    }

    [Fact]
    public async Task TheCopyIsAUsableDatabaseHoldingTheSameData()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.SeedDemoAsync();
        var expectedEvents = await workspace.ScalarAsync("SELECT COUNT(*) FROM audit_event");

        var package = await workspace.UseAsync(scope =>
            scope.GetRequiredService<BackupService>().CreateAsync());

        var copy = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(package.Directory, "workspace.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(copy);
        await connection.OpenAsync();

        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", (await integrity.ExecuteScalarAsync())?.ToString());
        }

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM audit_event";
            Assert.Equal(expectedEvents, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }

        await using (var revenue = connection.CreateCommand())
        {
            revenue.CommandText =
                "SELECT amount_minor FROM financial_data f JOIN account a ON a.account_id = f.account_id " +
                "JOIN engagement e ON e.engagement_id = f.engagement_id " +
                "WHERE a.account_code = '4000' AND e.status = 'FINALIZED'";
            Assert.Equal(85_000_000_000L, Convert.ToInt64(await revenue.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task BackupIsAuditedWithoutLeakingTheFilePath()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.SeedDemoAsync();

        var package = await workspace.UseAsync(scope =>
            scope.GetRequiredService<BackupService>().CreateAsync());

        var row = (await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(eventType: "BACKUP_CREATED"))).Single();

        Assert.Equal("WORKSPACE", row.EntityType);
        Assert.Equal(package.Name, row.EntityId);
        Assert.Contains("DEFAULT_WORKSPACE_FOLDER", row.DetailsJson);
        Assert.DoesNotContain(workspace.Root, row.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Root, row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagesAreVerifiableAndTamperEvident()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.SeedDemoAsync();
        var package = await workspace.UseAsync(scope =>
            scope.GetRequiredService<BackupService>().CreateAsync());

        Assert.True(SqliteBackupWriter.Verify(package.Directory).IsValid);

        await File.AppendAllTextAsync(Path.Combine(package.Directory, "workspace.db"), "tamper");

        var result = SqliteBackupWriter.Verify(package.Directory);
        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SuccessiveBackupsAreListedNewestFirst()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.CreateCompanyAsync();

        var first = await workspace.UseAsync(scope => scope.GetRequiredService<BackupService>().CreateAsync());
        await Task.Delay(5);
        var second = await workspace.UseAsync(scope => scope.GetRequiredService<BackupService>().CreateAsync());

        Assert.NotEqual(first.Name, second.Name);

        var listed = SqliteBackupWriter.List(workspace.Paths.BackupsDirectory);

        Assert.Equal(2, listed.Count);
        Assert.Equal(second.Name, listed[0].Name);
    }

    [Fact]
    public async Task BackupToAUserSelectedFolderIsRecordedAsSuch()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.CreateCompanyAsync();
        var destination = Path.Combine(workspace.Root, "user-chosen");
        Directory.CreateDirectory(destination);

        var package = await workspace.UseAsync(scope =>
            scope.GetRequiredService<BackupService>().CreateAsync(destination));

        Assert.StartsWith(destination, package.Directory, StringComparison.Ordinal);

        var row = (await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(eventType: "BACKUP_CREATED"))).Single();
        Assert.Contains("USER_SELECTED_FOLDER", row.DetailsJson);
    }

    [Fact]
    public async Task BackupDoesNotModifyTheLiveWorkspace()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var (_, priorId, _) = await workspace.SeedDemoAsync();
        var eventsBefore = await workspace.ScalarAsync("SELECT COUNT(*) FROM audit_event");

        await workspace.UseAsync(scope => scope.GetRequiredService<BackupService>().CreateAsync());

        // Exactly one new event: the backup itself.
        Assert.Equal(eventsBefore + 1, await workspace.ScalarAsync("SELECT COUNT(*) FROM audit_event"));
        Assert.True(await workspace.UseAsync(scope =>
            scope.GetRequiredService<Finalization.FinalizationService>().VerifyDigestAsync(priorId)));
    }
}
