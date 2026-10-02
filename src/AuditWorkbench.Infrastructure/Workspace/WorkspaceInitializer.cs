using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Companies;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuditWorkbench.Infrastructure.Workspace;

/// <summary>
/// Startup sequence for a workspace: create folders, apply migrations, verify
/// integrity and make sure the local actor exists (architecture.md section 5).
/// </summary>
public sealed class WorkspaceInitializer
{
    private readonly WorkspacePaths _paths;
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<WorkspaceInitializer> _logger;

    public WorkspaceInitializer(
        WorkspacePaths paths,
        AuditWorkbenchDbContext dbContext,
        IClock clock,
        ILogger<WorkspaceInitializer> logger)
    {
        _paths = paths;
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task<WorkspaceStartupReport> InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();

        var runner = new SqlMigrationRunner(_paths.ConnectionString, _clock);
        var applied = runner.Apply();
        if (applied.Count > 0)
        {
            _logger.LogInformation("Applied {Count} workspace migration(s).", applied.Count);
        }

        var integrity = await CheckIntegrityAsync(cancellationToken).ConfigureAwait(false);

        if (!await _dbContext.Users.AnyAsync(u => u.UserId == LocalUser.LocalActorId, cancellationToken)
                .ConfigureAwait(false))
        {
            _dbContext.Users.Add(LocalUser.CreateLocalActor(IClock.Format(_clock.UtcNow)));
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new WorkspaceStartupReport
        {
            WorkspaceRoot = _paths.RootDirectory,
            SchemaVersion = SqlMigrationRunner.SchemaVersion,
            AppliedMigrations = applied,
            IntegrityProblems = integrity,
        };
    }

    public async Task<IReadOnlyList<string>> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var connection = (SqliteConnection)_dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            var result = (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Database integrity check reported: {result}");
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                problems.Add("Database foreign-key check reported violations.");
            }
        }

        return problems;
    }
}

public sealed class WorkspaceStartupReport
{
    public required string WorkspaceRoot { get; init; }

    public required string SchemaVersion { get; init; }

    public required IReadOnlyList<string> AppliedMigrations { get; init; }

    public required IReadOnlyList<string> IntegrityProblems { get; init; }

    public bool IsHealthy => IntegrityProblems.Count == 0;
}
