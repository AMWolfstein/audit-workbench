namespace AuditWorkbench.Infrastructure.Workspace;

/// <summary>
/// Separates replaceable application binaries from mutable workspace data
/// (NFR-04). Everything lives under a normal user-writable folder: no admin
/// rights, no service, no shared database (NFR-01).
/// </summary>
public sealed class WorkspacePaths
{
    public WorkspacePaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        DatabasePath = Path.Combine(RootDirectory, "workspace.db");
        BackupsDirectory = Path.Combine(RootDirectory, "backups");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        AttachmentsDirectory = Path.Combine(RootDirectory, "attachments");
    }

    public string RootDirectory { get; }

    public string DatabasePath { get; }

    public string BackupsDirectory { get; }

    public string LogsDirectory { get; }

    public string AttachmentsDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
    }

    /// <summary>Default workspace next to the user profile; overridable by configuration.</summary>
    public static WorkspacePaths Default()
    {
        var root = Environment.GetEnvironmentVariable("AUDITWORKBENCH_WORKSPACE");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuditWorkbenchData");
        }

        return new WorkspacePaths(root);
    }

    public string ConnectionString =>
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
}
