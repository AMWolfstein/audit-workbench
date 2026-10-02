namespace AuditWorkbench.Domain.Finalization;

/// <summary>
/// The immutable record of exactly which content formed a finalized financial
/// year. Stored in the same transaction as the status transition.
/// </summary>
public class FinalizationManifest
{
    private FinalizationManifest()
    {
    }

    public Guid ManifestId { get; private set; }

    public Guid EngagementId { get; private set; }

    public string ManifestVersion { get; private set; } = FinalizationManifestBuilder.ManifestVersion;

    public string RootDigest { get; private set; } = string.Empty;

    public string CanonicalContent { get; private set; } = string.Empty;

    public int RecordCount { get; private set; }

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public Guid CreatedBy { get; private set; }

    public static FinalizationManifest Create(
        Guid manifestId,
        Guid engagementId,
        string canonicalContent,
        int recordCount,
        string createdAtUtc,
        Guid createdBy) => new()
        {
            ManifestId = manifestId,
            EngagementId = engagementId,
            ManifestVersion = FinalizationManifestBuilder.ManifestVersion,
            RootDigest = FinalizationManifestBuilder.ComputeDigest(canonicalContent),
            CanonicalContent = canonicalContent,
            RecordCount = recordCount,
            CreatedAtUtc = createdAtUtc,
            CreatedBy = createdBy,
        };

    public bool DigestMatchesContent() =>
        RootDigest == FinalizationManifestBuilder.ComputeDigest(CanonicalContent);
}
