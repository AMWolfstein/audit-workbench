using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuditWorkbench.Domain.Common;
using Microsoft.Data.Sqlite;

namespace AuditWorkbench.Infrastructure.Persistence;

/// <summary>
/// Applies the ordered, checksummed SQL migrations embedded from /db/migrations
/// (NFR-07). Each migration runs as one transaction together with its ledger
/// row, so a half-applied migration can never be committed. A migration whose
/// checksum changed after it was applied refuses to open the workspace.
/// </summary>
public sealed class SqlMigrationRunner
{
    private readonly string _connectionString;
    private readonly IClock _clock;

    public SqlMigrationRunner(string connectionString, IClock clock)
    {
        _connectionString = connectionString;
        _clock = clock;
    }

    public IReadOnlyList<string> Apply()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        SqliteConnectionPolicyInterceptor.Apply(connection);

        var applied = new List<string>();
        var alreadyApplied = ReadLedger(connection);

        foreach (var (id, script) in SqlResources.Migrations())
        {
            var checksum = Sha256Hex(script);
            if (alreadyApplied.TryGetValue(id, out var recordedChecksum))
            {
                if (recordedChecksum != checksum)
                {
                    throw new IntegrityGuardException(
                        $"Migration {id} changed after it was applied to this workspace. " +
                        "Opening it could corrupt audit data, so the workspace was not opened.");
                }

                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = script;
                command.ExecuteNonQuery();
            }

            using (var ledger = connection.CreateCommand())
            {
                ledger.Transaction = transaction;
                ledger.CommandText =
                    "INSERT INTO schema_migration (migration_id, checksum_sha256, applied_at_utc) " +
                    "VALUES ($id, $checksum, $appliedAt);";
                ledger.Parameters.AddWithValue("$id", id);
                ledger.Parameters.AddWithValue("$checksum", checksum);
                ledger.Parameters.AddWithValue("$appliedAt", IClock.Format(_clock.UtcNow));
                ledger.ExecuteNonQuery();
            }

            transaction.Commit();
            applied.Add(id);
        }

        WriteMetadata(connection);
        return applied;
    }

    public static string SchemaVersion => SqlResources.Migrations().Last().Id;

    public const string WorkspaceFormatVersion = "1";

    private static Dictionary<string, string> ReadLedger(SqliteConnection connection)
    {
        var ledger = new Dictionary<string, string>(StringComparer.Ordinal);

        using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_migration';";
            if (exists.ExecuteScalar() is null)
            {
                return ledger;
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT migration_id, checksum_sha256 FROM schema_migration;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ledger[reader.GetString(0)] = reader.GetString(1);
        }

        return ledger;
    }

    private void WriteMetadata(SqliteConnection connection)
    {
        foreach (var (key, value) in new[]
                 {
                     ("schema_version", SchemaVersion),
                     ("workspace_format_version", WorkspaceFormatVersion),
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO workspace_metadata (metadata_key, metadata_value) VALUES ($key, $value) " +
                "ON CONFLICT (metadata_key) DO UPDATE SET metadata_value = excluded.metadata_value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(new UTF8Encoding(false).GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string FormatCount(int value) => value.ToString(CultureInfo.InvariantCulture);
}
