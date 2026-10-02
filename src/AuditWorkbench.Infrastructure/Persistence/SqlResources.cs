using System.Reflection;

namespace AuditWorkbench.Infrastructure.Persistence;

/// <summary>Loads the embedded SQL artefacts from /db.</summary>
public static class SqlResources
{
    private const string MigrationPrefix = "AuditWorkbench.Db.Migrations.";
    private const string QueryPrefix = "AuditWorkbench.Db.Sql.";

    private static readonly Assembly ResourceAssembly = typeof(SqlResources).Assembly;

    public static IReadOnlyList<(string Id, string Script)> Migrations() => ResourceAssembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(MigrationPrefix, StringComparison.Ordinal))
        .OrderBy(name => name, StringComparer.Ordinal)
        .Select(name => (
            Id: Path.GetFileNameWithoutExtension(name[MigrationPrefix.Length..]),
            Script: Read(name)))
        .ToList();

    public static string Query(string fileName) => Read(QueryPrefix + fileName);

    private static string Read(string resourceName)
    {
        using var stream = ResourceAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        // Normalise line endings so migration checksums are identical on every
        // platform and match the verification harness.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
