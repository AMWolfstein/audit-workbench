using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Infrastructure.Persistence;
using AuditWorkbench.Infrastructure.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Infrastructure.Backup;

/// <summary>
/// Application-managed backup (ADR-011). Uses the SQLite online backup API for
/// a transactionally consistent snapshot, writes to a staging folder and
/// atomically renames it on success. No administrator rights are required: the
/// destination is an ordinary user-writable folder.
/// </summary>
public sealed class SqliteBackupWriter
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly WorkspacePaths _paths;
    private readonly IClock _clock;

    public SqliteBackupWriter(AuditWorkbenchDbContext dbContext, WorkspacePaths paths, IClock clock)
    {
        _dbContext = dbContext;
        _paths = paths;
        _clock = clock;
    }

    public const string PackageFormat = "AWB-BACKUP/1.0";

    public async Task<BackupPackage> CreateAsync(string? destinationDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var destination = string.IsNullOrWhiteSpace(destinationDirectory)
            ? _paths.BackupsDirectory
            : Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destination);

        var createdAt = IClock.Format(_clock.UtcNow);
        var stamp = createdAt.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        var packageName = $"audit-workbench-backup-{stamp}";
        var packageDirectory = Path.Combine(destination, packageName);
        var stagingDirectory = Path.Combine(destination, "." + packageName + ".partial");

        if (Directory.Exists(packageDirectory))
        {
            throw new ValidationException("A backup with this timestamp already exists. Try again in a moment.");
        }

        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var databaseCopy = Path.Combine(stagingDirectory, "workspace.db");
            await CopyDatabaseAsync(databaseCopy, cancellationToken).ConfigureAwait(false);

            var checksum = Sha256File(databaseCopy);
            var sizeBytes = new FileInfo(databaseCopy).Length;
            var manifest = await BuildManifestAsync(createdAt, checksum, sizeBytes, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Combine(stagingDirectory, "backup-manifest.json"),
                    manifest,
                    new UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);

            Directory.Move(stagingDirectory, packageDirectory);

            return new BackupPackage
            {
                Name = packageName,
                Directory = packageDirectory,
                CreatedAtUtc = createdAt,
                SizeBytes = sizeBytes,
                DatabaseChecksum = checksum,
            };
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            throw;
        }
    }

    private async Task CopyDatabaseAsync(string databaseCopyPath, CancellationToken cancellationToken)
    {
        var source = (SqliteConnection)_dbContext.Database.GetDbConnection();
        if (source.State != System.Data.ConnectionState.Open)
        {
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseCopyPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        await target.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(target);
    }

    private async Task<string> BuildManifestAsync(
        string createdAt,
        string checksum,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        var digests = await _dbContext.Engagements
            .Where(e => e.FinalizationDigest != null)
            .OrderBy(e => e.EngagementId)
            .Select(e => new { e.EngagementId, e.FinalizationDigest })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.Append("{\n");
        builder.Append($"  \"package_format\": \"{PackageFormat}\",\n");
        builder.Append($"  \"created_at_utc\": \"{createdAt}\",\n");
        builder.Append($"  \"schema_version\": \"{SqlMigrationRunner.SchemaVersion}\",\n");
        builder.Append($"  \"workspace_format_version\": \"{SqlMigrationRunner.WorkspaceFormatVersion}\",\n");
        builder.Append("  \"files\": [\n");
        builder.Append("    { \"name\": \"workspace.db\", \"sha256\": \"").Append(checksum)
            .Append("\", \"bytes\": ").Append(sizeBytes.ToString(CultureInfo.InvariantCulture)).Append(" }\n");
        builder.Append("  ],\n");
        builder.Append("  \"engagement_digests\": [\n");
        for (var i = 0; i < digests.Count; i++)
        {
            builder.Append("    { \"engagement_id\": \"").Append(digests[i].EngagementId.ToString("D"))
                .Append("\", \"root_digest\": \"").Append(digests[i].FinalizationDigest).Append("\" }");
            builder.Append(i == digests.Count - 1 ? "\n" : ",\n");
        }

        builder.Append("  ]\n");
        builder.Append("}\n");
        return builder.ToString();
    }

    public static BackupVerificationResult Verify(string packageDirectory)
    {
        var name = new DirectoryInfo(packageDirectory).Name;
        var problems = new List<string>();
        var manifestPath = Path.Combine(packageDirectory, "backup-manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new BackupVerificationResult { Name = name, Problems = new[] { "Backup manifest is missing." } };
        }

        var manifest = File.ReadAllText(manifestPath);
        if (!manifest.Contains(PackageFormat, StringComparison.Ordinal))
        {
            problems.Add("Unsupported backup package format.");
        }

        var databasePath = Path.Combine(packageDirectory, "workspace.db");
        if (!File.Exists(databasePath))
        {
            problems.Add("Backup file 'workspace.db' is missing.");
            return new BackupVerificationResult { Name = name, Problems = problems };
        }

        var expected = ExtractChecksum(manifest);
        var actual = Sha256File(databasePath);
        if (expected is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
        {
            problems.Add("Backup file 'workspace.db' failed its checksum verification.");
        }

        // A damaged copy must be reported, never thrown at the operator.
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                var result = command.ExecuteScalar()?.ToString();
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"Restored database integrity check failed: {result}");
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_key_check;";
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    problems.Add("Restored database has foreign-key violations.");
                }
            }
        }
        catch (SqliteException ex)
        {
            problems.Add($"Backup file 'workspace.db' could not be opened: {ex.Message}");
        }

        return new BackupVerificationResult { Name = name, Problems = problems };
    }

    public static IReadOnlyList<BackupPackage> List(string backupsDirectory)
    {
        if (!Directory.Exists(backupsDirectory))
        {
            return Array.Empty<BackupPackage>();
        }

        return Directory.EnumerateDirectories(backupsDirectory, "audit-workbench-backup-*")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var databasePath = Path.Combine(path, "workspace.db");
                var info = File.Exists(databasePath) ? new FileInfo(databasePath) : null;
                return new BackupPackage
                {
                    Name = new DirectoryInfo(path).Name,
                    Directory = path,
                    CreatedAtUtc = info?.CreationTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                                   ?? string.Empty,
                    SizeBytes = info?.Length ?? 0,
                    DatabaseChecksum = string.Empty,
                };
            })
            .ToList();
    }

    private static string? ExtractChecksum(string manifest)
    {
        const string marker = "\"sha256\": \"";
        var index = manifest.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = manifest.IndexOf('"', start);
        return end < 0 ? null : manifest[start..end];
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
