namespace AuditWorkbench.Infrastructure.Backup;

public sealed class BackupPackage
{
    public required string Name { get; init; }

    public required string Directory { get; init; }

    public required string CreatedAtUtc { get; init; }

    public required long SizeBytes { get; init; }

    public required string DatabaseChecksum { get; init; }
}

public sealed class BackupVerificationResult
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Problems { get; init; }

    public bool IsValid => Problems.Count == 0;
}
