namespace AuditWorkbench.Domain.Finalization;

/// <summary>One in-scope account and its latest revision at finalization time.</summary>
public sealed class ManifestAccountLine
{
    public required string AccountCode { get; init; }

    public required string AccountName { get; init; }

    public required string AccountType { get; init; }

    public int? RevisionNo { get; init; }

    public long? AmountMinor { get; init; }
}
