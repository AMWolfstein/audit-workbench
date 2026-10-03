using System.Text.Json.Serialization;

namespace AuditWorkbench.Application.Handover;

public interface IClientHandoverPackageService
{
    Task ExportAsync(Guid companyId, Stream destination, CancellationToken cancellationToken = default);
    Task<ClientHandoverValidationReport> ValidateAsync(Stream package, CancellationToken cancellationToken = default);
    Task<ClientHandoverImportResult> ImportAsync(Stream package, CancellationToken cancellationToken = default);
}

public sealed record ClientHandoverFinding(
    string Code,
    string Severity,
    string Entity,
    string? SourceId,
    string Message);

public sealed record ClientHandoverValidationReport(
    string? PackageId,
    string? CompanyName,
    int EngagementCount,
    IReadOnlyList<ClientHandoverFinding> Findings)
{
    public bool IsValid => Findings.All(f => f.Severity != "ERROR");
}

public sealed record ClientHandoverImportResult(Guid ImportId, Guid CompanyId, string PackageDigest);

internal sealed record HandoverManifest(
    string PackageFormat,
    Guid PackageId,
    string CreatedAtUtc,
    ExportActor ExportedBy,
    SourceDescriptor Source,
    RequirementDescriptor Requires,
    ManifestClient Client,
    IReadOnlyList<ManifestEngagement> Engagements,
    IReadOnlyDictionary<string, int> Counts,
    string HashAlgorithm,
    IReadOnlyList<PackageFileDescriptor> Files);

internal sealed record ExportActor(Guid UserId, string DisplayName);
internal sealed record SourceDescriptor(string SchemaVersion, string WorkspaceFormatVersion, string ApplicationVersion);
internal sealed record RequirementDescriptor(string MinSchemaVersion, IReadOnlyList<string> ManifestVersions);
internal sealed record ManifestClient(Guid CompanyId, string ShortName, string LegalName, string Status);
internal sealed record ManifestEngagement(Guid EngagementId, Guid FinancialYearId, string Label, string PeriodEnd,
    string Status, string? FinalizationDigest);
internal sealed record PackageFileDescriptor(string Path, long Bytes, string Sha256, string MediaType, string Owner);

internal sealed record CompanyTransfer(Guid CompanyId, string LegalName, string ShortName, string Industry,
    string CountryCode, string? TaxReference, string Status, string CreatedAtUtc, Guid CreatedBy,
    string? ArchivedAtUtc, int RowVersion);
internal sealed record FinancialYearTransfer(Guid FinancialYearId, string Label, string PeriodStart,
    string PeriodEnd, string CreatedAtUtc);
internal sealed record EngagementTransfer(Guid EngagementId, Guid CompanyId, Guid FinancialYearId, string Status,
    string CurrencyCode, int MinorUnitScale, string CreatedAtUtc, Guid CreatedBy, string? FinalizedAtUtc,
    Guid? FinalizedBy, string? FinalizationDigest, string? FinalizationManifestVersion, int RowVersion);
internal sealed record AccountTransfer(Guid AccountId, Guid EngagementId, string AccountCode, string AccountName,
    string AccountType, int DisplayOrder, string CreatedAtUtc, Guid CreatedBy);
internal sealed record FinancialDataTransfer(Guid FinancialDataId, Guid EngagementId, Guid AccountId, int RevisionNo,
    long AmountMinor, string CurrencyCode, Guid? SupersedesId, string? CorrectionReason,
    string RecordedAtUtc, Guid RecordedBy);
internal sealed record PriorYearTransfer(Guid RelationshipId, Guid CurrentEngagementId, Guid PriorEngagementId,
    string LinkedAtUtc, Guid LinkedBy);
internal sealed record FinalizationManifestTransfer(Guid ManifestId, Guid EngagementId, string ManifestVersion,
    string RootDigest, string CanonicalContent, int RecordCount, string CreatedAtUtc, Guid CreatedBy);
internal sealed record PrincipalTransfer(Guid UserId, string Username, string DisplayName);
internal sealed record TeamMemberTransfer(Guid EngagementMemberId, Guid EngagementId, Guid UserId, string RoleKey,
    string Status, string AddedAtUtc, Guid AddedBy, string UpdatedAtUtc, int RowVersion);
internal sealed record AssignmentTransfer(Guid AssignmentId, Guid EngagementId, Guid AssigneeUserId, string ScopeType,
    string ScopeId, string Title, string Status, string AssignedAtUtc, Guid AssignedBy, string UpdatedAtUtc,
    int RowVersion);
internal sealed record AuditEventTransfer(Guid AuditEventId, long SequenceNo, string OccurredAtUtc, Guid ActorUserId,
    string ActorDisplayName, string EventType, string Outcome, Guid? CompanyId, Guid? EngagementId,
    string EntityType, string EntityId, string Description, string DetailsJson, string? PreviousEventHash,
    string EventHash);
internal sealed record SourceChainTransfer(long HeadSequence, string? HeadHash, bool VerifiedAtExport, int SubsetCount);

internal sealed class ParsedClientPackage
{
    public required HandoverManifest Manifest { get; init; }
    public required byte[] ManifestBytes { get; init; }
    public required CompanyTransfer Company { get; init; }
    public required List<FinancialYearTransfer> FinancialYears { get; init; }
    public required List<EngagementTransfer> Engagements { get; init; }
    public required List<AccountTransfer> Accounts { get; init; }
    public required List<FinancialDataTransfer> FinancialData { get; init; }
    public required List<PriorYearTransfer> PriorYears { get; init; }
    public required List<FinalizationManifestTransfer> FinalizationManifests { get; init; }
    public required List<PrincipalTransfer> Principals { get; init; }
    public required List<TeamMemberTransfer> TeamMembers { get; init; }
    public required List<AssignmentTransfer> Assignments { get; init; }
    public required List<AuditEventTransfer> AuditEvents { get; init; }
    public required SourceChainTransfer SourceChain { get; init; }
    public required IReadOnlyDictionary<string, byte[]> Entries { get; init; }
    public string PackageDigest => CanonicalJson.Sha256(ManifestBytes);
}
