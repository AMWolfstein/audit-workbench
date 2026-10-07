using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.FinancialData;

/// <summary>Version overview row: what was imported, from which file, with which result.</summary>
public sealed record DatasetImportSummary(
    Guid ImportId,
    Guid EngagementId,
    Guid FinancialPeriodId,
    string DatasetKind,
    int ImportNo,
    string Label,
    string Status,
    bool IsActive,
    string? SupersededByLabel,
    string? RepeatOfLabel,
    string SourceFileName,
    string SourceFileSha256,
    long SourceFileSizeBytes,
    string? SourceSheetName,
    int? SourceHeaderRowNo,
    string? CoverageThroughDate,
    string ColumnMappingJson,
    string ValidationStatus,
    int RowCount,
    int ValidRowCount,
    int WarningCount,
    int ErrorCount,
    int OutOfPeriodCount,
    long TotalDebitMinor,
    long TotalCreditMinor,
    long DifferenceMinor,
    bool IsBalanced,
    bool UnbalancedOverride,
    string ImportedAtUtc,
    string ImportedByDisplayName,
    string? FinalizedAtUtc,
    string? FinalizedByDisplayName,
    int RowVersion,
    IReadOnlyList<ImportIssue> Issues)
{
    public bool IsFinalized => Status == DatasetImportStatus.Finalized;

    public bool IsSuperseded => Status == DatasetImportStatus.Superseded;

    public bool HasRows => DatasetImportStatus.HasRows(Status);
}

/// <summary>
/// Shared lifecycle of imported datasets (TB versions and GL snapshots):
/// listing, provenance, activation/superseding and finalization. Both importers
/// use this service so the versioning rules exist in exactly one place.
/// </summary>
public sealed class DatasetImportService
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly FinancialPeriodService _periods;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;
    private readonly EngagementAuthorizationService _authorization;

    public DatasetImportService(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        FinancialPeriodService periods,
        IClock clock,
        ICurrentActor actor,
        EngagementAuthorizationService authorization)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _periods = periods;
        _clock = clock;
        _actor = actor;
        _authorization = authorization;
    }

    public async Task<IReadOnlyList<DatasetImportSummary>> ListAsync(Guid engagementId, string? datasetKind = null,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);

        var query = from import in _dbContext.DatasetImports.AsNoTracking()
                    join user in _dbContext.Users.AsNoTracking() on import.ImportedBy equals user.UserId
                    where import.EngagementId == engagementId
                    where datasetKind == null || import.DatasetKind == datasetKind
                    orderby import.DatasetKind, import.ImportNo descending
                    select new { import, ImportedByName = user.DisplayName };

        var imports = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        var labels = imports.ToDictionary(x => x.import.ImportId, x => x.import.SnapshotLabel);
        var finalizedBy = await FinalizedByNamesAsync(imports.Select(x => x.import.FinalizedBy).ToList(),
            cancellationToken).ConfigureAwait(false);

        return imports.Select(x => Map(x.import, x.ImportedByName, labels, finalizedBy)).ToList();
    }

    public async Task<DatasetImportSummary> GetAsync(Guid engagementId, Guid importId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        return await BuildSummaryAsync(engagementId, importId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validation report stored as provenance, including the capped issue sample.</summary>
    public async Task<ImportValidationReportSnapshot> GetValidationAsync(Guid engagementId, Guid importId,
        CancellationToken cancellationToken = default)
    {
        var import = await LoadTrackedAsync(engagementId, importId, null, requireWritable: false,
            Permissions.ViewEngagement, cancellationToken).ConfigureAwait(false);
        return ImportValidationReport.Parse(import.ValidationJson);
    }

    /// <summary>
    /// Loads one import, verifying that it belongs to the engagement and to the
    /// expected dataset kind. Callers never pass an import id straight to SQL.
    /// </summary>
    internal async Task<FinancialDatasetImport> LoadTrackedAsync(Guid engagementId, Guid importId,
        string? expectedKind, bool requireWritable, string permission, CancellationToken cancellationToken)
    {
        await _authorization.RequireAsync(engagementId, permission, cancellationToken).ConfigureAwait(false);
        var import = await _dbContext.DatasetImports
            .FirstOrDefaultAsync(i => i.ImportId == importId, cancellationToken)
            .ConfigureAwait(false);
        if (import is null || import.EngagementId != engagementId)
        {
            throw new NotFoundException("That imported dataset does not exist in this engagement.");
        }

        if (expectedKind is not null && import.DatasetKind != expectedKind)
        {
            throw new NotFoundException(
                $"That import is a {FinancialDatasetKind.DisplayName(import.DatasetKind)} import, not a " +
                $"{FinancialDatasetKind.DisplayName(expectedKind)} import.");
        }

        if (requireWritable)
        {
            await _periods.RequireOpenForImportsAsync(engagementId, permission, cancellationToken)
                .ConfigureAwait(false);
        }

        return import;
    }

    internal Task<int> NextImportNumberAsync(Guid engagementId, string datasetKind,
        CancellationToken cancellationToken) =>
        NextNumberCoreAsync(engagementId, datasetKind, cancellationToken);

    private async Task<int> NextNumberCoreAsync(Guid engagementId, string datasetKind,
        CancellationToken cancellationToken)
    {
        var max = await _dbContext.DatasetImports.AsNoTracking()
            .Where(i => i.EngagementId == engagementId && i.DatasetKind == datasetKind)
            .Select(i => (int?)i.ImportNo)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);
        return (max ?? 0) + 1;
    }

    internal async Task<FinancialDatasetImport?> ActiveImportAsync(Guid engagementId, string datasetKind,
        CancellationToken cancellationToken) =>
        await _dbContext.DatasetImports
            .FirstOrDefaultAsync(i => i.EngagementId == engagementId && i.DatasetKind == datasetKind && i.IsActive,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Inside the import transaction: the previously active version of the same
    /// dataset steps aside for the version being committed. It is never edited -
    /// only marked as replaced - and it stays fully readable.
    /// </summary>
    internal async Task SupersedeActiveAsync(Guid engagementId, string datasetKind, Guid replacementImportId,
        int replacementImportNo, CancellationToken cancellationToken)
    {
        var current = await ActiveImportAsync(engagementId, datasetKind, cancellationToken).ConfigureAwait(false);
        if (current is null || current.ImportId == replacementImportId)
        {
            return;
        }

        if (current.ImportNo < replacementImportNo)
        {
            current.MarkSuperseded(replacementImportId, replacementImportNo);
            await _auditTrail.AppendAsync(
                    AuditEventType.DatasetSuperseded,
                    AuditEntityType.DatasetImport,
                    current.ImportId.ToString("D"),
                    $"{FinanceLabel(current)} superseded by {FinancialDatasetKind.VersionNoun(datasetKind)} " +
                    $"#{replacementImportNo}.",
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", datasetKind)
                        .With("import_no", current.ImportNo)
                        .With("superseded_by_import_no", replacementImportNo),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            current.Deactivate();
        }
    }

    /// <summary>Operator action: make an existing version the active one.</summary>
    public Task ActivateAsync(Guid engagementId, Guid importId, CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            var target = await LoadTrackedAsync(engagementId, importId, null, requireWritable: true,
                Permissions.EditEngagement, token).ConfigureAwait(false);
            if (!target.HasRows)
            {
                throw new ValidationException("Only a committed import can become the active version.");
            }

            var current = await ActiveImportAsync(engagementId, target.DatasetKind, token).ConfigureAwait(false);
            if (current is not null && current.ImportId != target.ImportId)
            {
                if (current.ImportNo < target.ImportNo)
                {
                    current.MarkSuperseded(target.ImportId, target.ImportNo);
                }
                else
                {
                    current.Deactivate();
                }

                // Persist the release of the active flag before claiming it: the
                // database allows exactly one active version per dataset.
                await _dbContext.SaveChangesAsync(token).ConfigureAwait(false);
            }

            target.Activate();
            await _dbContext.SaveChangesAsync(token).ConfigureAwait(false);

            await _auditTrail.AppendAsync(
                    AuditEventType.DatasetActivated,
                    AuditEntityType.DatasetImport,
                    target.ImportId.ToString("D"),
                    $"{FinanceLabel(target)} is now the active {FinancialDatasetKind.DisplayName(target.DatasetKind)} " +
                    "dataset for the period.",
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", target.DatasetKind)
                        .With("import_no", target.ImportNo)
                        .With("status", target.Status),
                    cancellationToken: token)
                .ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Finalizes one version. After this the dataset is evidence: it is never
    /// edited, and a corrected client file creates a new version.
    /// </summary>
    public Task FinalizeAsync(Guid engagementId, Guid importId, int? expectedRowVersion = null,
        CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            await _authorization.RequireAsync(engagementId, Permissions.FinalizeEngagement, token).ConfigureAwait(false);
            var import = await LoadTrackedAsync(engagementId, importId, null, requireWritable: true,
                Permissions.FinalizeEngagement, token).ConfigureAwait(false);
            import.EnsureExpectedVersion(expectedRowVersion);
            import.MarkFinalized(IClock.Format(_clock.UtcNow), _actor.UserId);

            await _auditTrail.AppendAsync(
                    import.DatasetKind == FinancialDatasetKind.TrialBalance
                        ? AuditEventType.TbFinalized
                        : AuditEventType.GlFinalized,
                    AuditEntityType.DatasetImport,
                    import.ImportId.ToString("D"),
                    $"{FinanceLabel(import)} finalized " +
                    $"({import.RowCount} rows, debits {import.TotalDebitMinor}, credits {import.TotalCreditMinor}).",
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", import.DatasetKind)
                        .With("import_no", import.ImportNo)
                        .With("rows", import.RowCount)
                        .With("balanced", import.IsBalanced),
                    cancellationToken: token)
                .ConfigureAwait(false);
        }, cancellationToken);

    internal async Task<DatasetImportSummary> BuildSummaryAsync(Guid engagementId, Guid importId,
        CancellationToken cancellationToken)
    {
        var import = await _dbContext.DatasetImports.AsNoTracking()
            .FirstOrDefaultAsync(i => i.ImportId == importId && i.EngagementId == engagementId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("That imported dataset does not exist in this engagement.");

        var importedByName = await _dbContext.Users.AsNoTracking()
            .Where(u => u.UserId == import.ImportedBy)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;
        var labels = await _dbContext.DatasetImports.AsNoTracking()
            .Where(i => i.EngagementId == engagementId)
            .Select(i => new { i.ImportId, i.SnapshotLabel })
            .ToDictionaryAsync(x => x.ImportId, x => x.SnapshotLabel, cancellationToken)
            .ConfigureAwait(false);
        var finalizedBy = await FinalizedByNamesAsync(new List<Guid?> { import.FinalizedBy }, cancellationToken)
            .ConfigureAwait(false);

        return Map(import, importedByName, labels, finalizedBy);
    }

    private async Task<Dictionary<Guid, string>> FinalizedByNamesAsync(IEnumerable<Guid?> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await _dbContext.Users.AsNoTracking()
            .Where(u => ids.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, u => u.DisplayName, cancellationToken)
            .ConfigureAwait(false);
    }

    private static DatasetImportSummary Map(
        FinancialDatasetImport import,
        string importedByName,
        IReadOnlyDictionary<Guid, string> labels,
        IReadOnlyDictionary<Guid, string> finalizedByNames)
    {
        var issues = ImportValidationReport.Parse(import.ValidationJson).Issues;
        return new DatasetImportSummary(
            import.ImportId,
            import.EngagementId,
            import.FinancialPeriodId,
            import.DatasetKind,
            import.ImportNo,
            import.SnapshotLabel,
            import.Status,
            import.IsActive,
            import.SupersededByImportId is { } superseded && labels.TryGetValue(superseded, out var replacement)
                ? replacement
                : null,
            import.RepeatOfImportId is { } repeat && labels.TryGetValue(repeat, out var original) ? original : null,
            import.SourceFileName,
            import.SourceFileSha256,
            import.SourceFileSizeBytes,
            import.SourceSheetName,
            import.SourceHeaderRowNo,
            import.CoverageThroughDate,
            import.ColumnMappingJson,
            import.ValidationStatus,
            import.RowCount,
            import.ValidRowCount,
            import.WarningCount,
            import.ErrorCount,
            import.OutOfPeriodCount,
            import.TotalDebitMinor,
            import.TotalCreditMinor,
            import.DifferenceMinor,
            import.IsBalanced,
            import.UnbalancedOverride,
            import.ImportedAtUtc,
            importedByName,
            import.FinalizedAtUtc,
            import.FinalizedBy is { } finalizedByUser && finalizedByNames.TryGetValue(finalizedByUser, out var name)
                ? name
                : null,
            import.RowVersion,
            issues);
    }

    private static string FinanceLabel(FinancialDatasetImport import) =>
        $"{FinancialDatasetKind.DisplayName(import.DatasetKind)} {FinancialDatasetKind.VersionNoun(import.DatasetKind)} " +
        $"#{import.ImportNo}";
}
