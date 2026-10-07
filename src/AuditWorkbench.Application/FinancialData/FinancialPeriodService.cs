using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.FinancialData;

/// <summary>Period metadata with the counts the financial-data screens need.</summary>
public sealed record FinancialPeriodSummary(
    Guid FinancialPeriodId,
    Guid EngagementId,
    Guid FinancialYearId,
    string Label,
    string PeriodStart,
    string PeriodEnd,
    string ReportingDate,
    string Status,
    int RowVersion,
    bool IsOpenForImports,
    int TbImportCount,
    int GlImportCount,
    string? ActiveTbLabel,
    string? ActiveGlLabel,
    int MaterialityVersion);

/// <summary>
/// Financial period use cases: period provisioning, the reporting date, and the
/// period lock state that controls whether TB/GL imports are accepted.
/// </summary>
public sealed class FinancialPeriodService
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly EngagementService _engagements;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;
    private readonly EngagementAuthorizationService _authorization;

    public FinancialPeriodService(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        EngagementService engagements,
        IClock clock,
        ICurrentActor actor,
        EngagementAuthorizationService authorization)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _engagements = engagements;
        _clock = clock;
        _actor = actor;
        _authorization = authorization;
    }

    /// <summary>Period of one engagement. Reading requires engagement access only.</summary>
    public async Task<FinancialPeriodSummary> GetAsync(Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        var period = await LoadOrCreateAsync(engagementId, cancellationToken).ConfigureAwait(false);
        return await SummarizeAsync(period, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read-only load used by other services. Creates the period row when an
    /// engagement predates this phase or arrived through a client handover.
    /// </summary>
    internal async Task<FinancialPeriod> LoadOrCreateAsync(Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.FinancialPeriods
            .FirstOrDefaultAsync(p => p.EngagementId == engagementId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var engagement = await _engagements.LoadAsync(engagementId, cancellationToken).ConfigureAwait(false);
        var year = await _engagements.LoadYearAsync(engagement.FinancialYearId, cancellationToken)
            .ConfigureAwait(false);
        var created = FinancialPeriod.Create(
            Guid.NewGuid(),
            engagement.EngagementId,
            year.FinancialYearId,
            year.PeriodEnd,
            IClock.Format(_clock.UtcNow),
            _actor.UserId,
            FinancialPeriodStatus.IsFinalized(engagement.Status) ? FinancialPeriodStatus.Locked : FinancialPeriodStatus.Open);
        _dbContext.FinancialPeriods.Add(created);
        return created;
    }

    /// <summary>Creates the period inside an existing transaction (engagement creation).</summary>
    internal static FinancialPeriod CreateForNewEngagement(Engagement engagement, FinancialYear year, string createdAtUtc,
        Guid createdBy)
    {
        var period = FinancialPeriod.Create(
            Guid.NewGuid(),
            engagement.EngagementId,
            year.FinancialYearId,
            year.PeriodEnd,
            createdAtUtc,
            createdBy,
            FinancialPeriodStatus.Open);
        period.EnsureReportingDateWithin(year.Start, year.End);
        return period;
    }

    public Task ChangeReportingDateAsync(Guid engagementId, string reportingDate, int? expectedRowVersion = null,
        CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            await _authorization.RequireAsync(engagementId, Permissions.EditEngagement, token).ConfigureAwait(false);
            var engagement = await _engagements.LoadAsync(engagementId, token).ConfigureAwait(false);
            engagement.EnsureOpenForEditing((await _engagements.LoadYearAsync(engagement.FinancialYearId, token)
                .ConfigureAwait(false)).Label);
            var period = await LoadOrCreateAsync(engagementId, token).ConfigureAwait(false);
            period.EnsureExpectedVersion(expectedRowVersion);
            period.ChangeReportingDate(reportingDate, IClock.Format(_clock.UtcNow));
            await _auditTrail.AppendAsync(
                    AuditEventType.FinancialPeriodUpdated,
                    AuditEntityType.FinancialPeriod,
                    period.FinancialPeriodId.ToString("D"),
                    $"Reporting date of the financial period set to {period.ReportingDate}.",
                    companyId: engagement.CompanyId,
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("reporting_date", period.ReportingDate)
                        .With("status", period.Status),
                    cancellationToken: token)
                .ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Moves the period through OPEN / IN_PROGRESS / FINALIZED / LOCKED.</summary>
    public Task ChangeStatusAsync(Guid engagementId, string newStatus, int? expectedRowVersion = null,
        CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            var permission = newStatus is FinancialPeriodStatus.Finalized or FinancialPeriodStatus.Locked
                ? Permissions.FinalizeEngagement
                : Permissions.EditEngagement;
            await _authorization.RequireAsync(engagementId, permission, token).ConfigureAwait(false);
            var engagement = await _engagements.LoadAsync(engagementId, token).ConfigureAwait(false);
            var period = await LoadOrCreateAsync(engagementId, token).ConfigureAwait(false);
            period.EnsureExpectedVersion(expectedRowVersion);

            var previousStatus = period.Status;
            period.ChangeStatus(newStatus, IClock.Format(_clock.UtcNow));

            await _auditTrail.AppendAsync(
                    AuditEventType.FinancialPeriodStatusChanged,
                    AuditEntityType.FinancialPeriod,
                    period.FinancialPeriodId.ToString("D"),
                    $"Financial period status changed from {previousStatus} to {period.Status}.",
                    companyId: engagement.CompanyId,
                    engagementId: engagementId,
                    details: AuditDetails.Empty().With("from", previousStatus).With("to", period.Status),
                    cancellationToken: token)
                .ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Locks the period inside the engagement finalization transaction: finalized
    /// evidence cannot receive another import.
    /// </summary>
    internal async Task LockForFinalizationAsync(Guid engagementId, CancellationToken cancellationToken)
    {
        var period = await LoadOrCreateAsync(engagementId, cancellationToken).ConfigureAwait(false);
        if (period.Status == FinancialPeriodStatus.Locked)
        {
            return;
        }

        period.ChangeStatus(FinancialPeriodStatus.Locked, IClock.Format(_clock.UtcNow));
    }

    /// <summary>
    /// Guard used by every import/activation path: the period must accept imports
    /// and the engagement must not be finalized.
    /// </summary>
    internal async Task<FinancialPeriod> RequireOpenForImportsAsync(Guid engagementId, string permission,
        CancellationToken cancellationToken)
    {
        await _authorization.RequireAsync(engagementId, permission, cancellationToken).ConfigureAwait(false);
        var engagement = await _engagements.LoadAsync(engagementId, cancellationToken).ConfigureAwait(false);
        var year = await _engagements.LoadYearAsync(engagement.FinancialYearId, cancellationToken).ConfigureAwait(false);
        engagement.EnsureOpenForEditing(year.Label);
        var period = await LoadOrCreateAsync(engagementId, cancellationToken).ConfigureAwait(false);
        FinancialPeriodStatus.EnsureOpenForImports(period.Status, year.Label);
        return period;
    }

    internal async Task<FinancialPeriodSummary> SummarizeAsync(FinancialPeriod period,
        CancellationToken cancellationToken)
    {
        var year = await _engagements.LoadYearAsync(period.FinancialYearId, cancellationToken).ConfigureAwait(false);
        var imports = await _dbContext.DatasetImports.AsNoTracking()
            .Where(i => i.EngagementId == period.EngagementId)
            .Select(i => new { i.DatasetKind, i.SnapshotLabel, i.IsActive, i.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var materialityVersion = await _dbContext.MaterialityRecords.AsNoTracking()
            .Where(m => m.EngagementId == period.EngagementId)
            .Select(m => (int?)m.VersionNo)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return new FinancialPeriodSummary(
            period.FinancialPeriodId,
            period.EngagementId,
            period.FinancialYearId,
            year.Label,
            year.PeriodStart,
            year.PeriodEnd,
            period.ReportingDate,
            period.Status,
            period.RowVersion,
            period.IsOpenForImports,
            imports.Count(i => i.DatasetKind == FinancialDatasetKind.TrialBalance && i.Status != DatasetImportStatus.Draft),
            imports.Count(i => i.DatasetKind == FinancialDatasetKind.GeneralLedger && i.Status != DatasetImportStatus.Draft),
            imports.FirstOrDefault(i => i.DatasetKind == FinancialDatasetKind.TrialBalance && i.IsActive)?.SnapshotLabel,
            imports.FirstOrDefault(i => i.DatasetKind == FinancialDatasetKind.GeneralLedger && i.IsActive)?.SnapshotLabel,
            materialityVersion);
    }
}
