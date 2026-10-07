using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Companies;
using AuditWorkbench.Application.FinancialData;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Domain.Teams;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Engagements;

public sealed record CreateEngagementCommand(
    Guid CompanyId,
    string Label,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Status,
    string CurrencyCode,
    int MinorUnitScale,
    Guid? PriorEngagementId);

public sealed class EngagementSummary
{
    public required Guid EngagementId { get; init; }

    public required Guid CompanyId { get; init; }

    public required string CompanyLegalName { get; init; }

    public required string CompanyShortName { get; init; }

    public required string Label { get; init; }

    public required string PeriodStart { get; init; }

    public required string PeriodEnd { get; init; }

    public required string Status { get; init; }

    public required string CurrencyCode { get; init; }

    public required int MinorUnitScale { get; init; }

    public required int RowVersion { get; init; }

    public required string CreatedAtUtc { get; init; }

    public string? FinalizedAtUtc { get; init; }

    public string? FinalizedByDisplayName { get; init; }

    public string? FinalizationDigest { get; init; }

    public Guid? PriorEngagementId { get; init; }

    public string? PriorEngagementLabel { get; init; }

    public required int AccountCount { get; init; }

    public bool IsFinalized => Status == EngagementStatus.Finalized;

    public bool IsOpen => EngagementStatus.IsOpen(Status);
}

public sealed class PriorYearOption
{
    public required Guid EngagementId { get; init; }

    public required string Label { get; init; }

    public required string PeriodEnd { get; init; }

    public required string FinalizedAtUtc { get; init; }
}

/// <summary>
/// Engagement (financial year) use cases. Creating a year never modifies an
/// existing one: FY2027 is always a new row with its own identity (ADR-001).
/// </summary>
public sealed class EngagementService
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;
    private readonly EngagementAuthorizationService _authorization;
    private readonly CompanyAuthorizationService _companyAuthorization;

    public EngagementService(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        IClock clock,
        ICurrentActor actor,
        EngagementAuthorizationService authorization,
        CompanyAuthorizationService companyAuthorization)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _clock = clock;
        _actor = actor;
        _authorization = authorization;
        _companyAuthorization = companyAuthorization;
    }

    public Task<Guid> CreateAsync(CreateEngagementCommand command, CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            // Knowing a company id is not authority to create a year and become its Partner.
            await _companyAuthorization.RequireManagementAsync(command.CompanyId, token).ConfigureAwait(false);
            var company = await _dbContext.Companies
                .FirstOrDefaultAsync(c => c.CompanyId == command.CompanyId, token)
                .ConfigureAwait(false)
                ?? throw new NotFoundException("That company does not exist in this workspace.");

            if (!company.IsActive)
            {
                throw new ValidationException("An archived company cannot receive new financial years.");
            }

            if (command.PeriodStart > command.PeriodEnd)
            {
                throw new ValidationException("Period start must not be after period end.");
            }

            var financialYear = await EnsureFinancialYearAsync(
                    command.Label, command.PeriodStart, command.PeriodEnd, token)
                .ConfigureAwait(false);

            var duplicate = await _dbContext.Engagements
                .AnyAsync(e => e.CompanyId == company.CompanyId &&
                               e.FinancialYearId == financialYear.FinancialYearId, token)
                .ConfigureAwait(false);
            if (duplicate)
            {
                throw new ValidationException(
                    $"{company.LegalName} already has a {financialYear.Label} engagement. " +
                    "A financial year is never re-used; create a different year.");
            }

            var overlapping = await (
                from engagement in _dbContext.Engagements
                join year in _dbContext.FinancialYears on engagement.FinancialYearId equals year.FinancialYearId
                where engagement.CompanyId == company.CompanyId
                      && string.Compare(year.PeriodStart, financialYear.PeriodEnd) <= 0
                      && string.Compare(year.PeriodEnd, financialYear.PeriodStart) >= 0
                select year.Label).FirstOrDefaultAsync(token).ConfigureAwait(false);
            if (overlapping is not null)
            {
                throw new ValidationException(
                    $"The period overlaps the existing engagement '{overlapping}' for this company.");
            }

            var newEngagement = Engagement.Create(
                Guid.NewGuid(),
                company.CompanyId,
                financialYear.FinancialYearId,
                command.CurrencyCode,
                command.MinorUnitScale,
                IClock.Format(_clock.UtcNow),
                _actor.UserId,
                command.Status);

            _dbContext.Engagements.Add(newEngagement);

            // The creator receives an explicit engagement membership. Account creation alone
            // never grants access to other engagements.
            _dbContext.EngagementMembers.Add(EngagementMember.Create(
                newEngagement.EngagementId, _actor.UserId, BuiltInRoles.PartnerId,
                IClock.Format(_clock.UtcNow), _actor.UserId));

            // Every engagement owns exactly one financial period from the moment it
            // exists: no trial balance or ledger row can ever be written without a
            // period context, and a period is never re-used across engagements.
            var financialPeriod = FinancialPeriodService.CreateForNewEngagement(
                newEngagement, financialYear, IClock.Format(_clock.UtcNow), _actor.UserId);
            _dbContext.FinancialPeriods.Add(financialPeriod);

            await _auditTrail.AppendAsync(
                    AuditEventType.EngagementCreated,
                    AuditEntityType.Engagement,
                    newEngagement.EngagementId.ToString("D"),
                    $"Engagement {financialYear.Label} created for {company.LegalName}.",
                    companyId: company.CompanyId,
                    engagementId: newEngagement.EngagementId,
                    details: AuditDetails.Empty()
                        .With("label", financialYear.Label)
                        .With("period_start", financialYear.PeriodStart)
                        .With("period_end", financialYear.PeriodEnd)
                        .With("status", newEngagement.Status)
                        .With("financial_period_id", financialPeriod.FinancialPeriodId.ToString("D")),
                    cancellationToken: token)
                .ConfigureAwait(false);

            if (command.PriorEngagementId is { } priorId)
            {
                await LinkPriorYearCoreAsync(newEngagement, financialYear, priorId, token).ConfigureAwait(false);
            }

            return newEngagement.EngagementId;
        }, cancellationToken);

    public Task LinkPriorYearAsync(Guid currentEngagementId, Guid priorEngagementId,
        CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            await _authorization.RequireAsync(currentEngagementId, Permissions.EditEngagement, token);
            var current = await LoadAsync(currentEngagementId, token).ConfigureAwait(false);
            var currentYear = await LoadYearAsync(current.FinancialYearId, token).ConfigureAwait(false);
            await LinkPriorYearCoreAsync(current, currentYear, priorEngagementId, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task LinkPriorYearCoreAsync(
        Engagement current,
        FinancialYear currentYear,
        Guid priorEngagementId,
        CancellationToken cancellationToken)
    {
        // The relationship would otherwise disclose/consume a year merely by id.
        await _authorization.RequireAsync(priorEngagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);

        var existing = await _dbContext.PriorYearRelationships
            .AnyAsync(r => r.CurrentEngagementId == current.EngagementId, cancellationToken)
            .ConfigureAwait(false);
        if (existing)
        {
            throw new ValidationException(
                "This financial year already has a prior-year relationship. The link is immutable so the " +
                "comparison lineage cannot change.");
        }

        var prior = await LoadAsync(priorEngagementId, cancellationToken).ConfigureAwait(false);
        var priorYear = await LoadYearAsync(prior.FinancialYearId, cancellationToken).ConfigureAwait(false);

        var relationship = PriorYearRelationship.Create(
            Guid.NewGuid(),
            current,
            currentYear,
            prior,
            priorYear,
            IClock.Format(_clock.UtcNow),
            _actor.UserId);

        _dbContext.PriorYearRelationships.Add(relationship);

        await _auditTrail.AppendAsync(
                AuditEventType.PriorYearLinked,
                AuditEntityType.PriorYearRelationship,
                relationship.RelationshipId.ToString("D"),
                $"Prior-year relationship created from {currentYear.Label} to {priorYear.Label} (read-only source).",
                companyId: current.CompanyId,
                engagementId: current.EngagementId,
                details: AuditDetails.Empty()
                    .With("prior_engagement_id", prior.EngagementId)
                    .With("prior_digest", prior.FinalizationDigest),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public Task ChangeStatusAsync(Guid engagementId, string newStatus, int? expectedRowVersion = null,
        CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            await _authorization.RequireAsync(engagementId, Permissions.EditEngagement, token);
            var engagement = await LoadAsync(engagementId, token).ConfigureAwait(false);
            var year = await LoadYearAsync(engagement.FinancialYearId, token).ConfigureAwait(false);
            engagement.EnsureOpenForEditing(year.Label);
            engagement.EnsureExpectedVersion(expectedRowVersion);

            var previousStatus = engagement.Status;
            engagement.ChangeStatus(newStatus);

            await _auditTrail.AppendAsync(
                    AuditEventType.EngagementStatusChanged,
                    AuditEntityType.Engagement,
                    engagement.EngagementId.ToString("D"),
                    $"Engagement {year.Label} status changed from {previousStatus} to {newStatus}.",
                    companyId: engagement.CompanyId,
                    engagementId: engagement.EngagementId,
                    details: AuditDetails.Empty().With("from", previousStatus).With("to", newStatus),
                    cancellationToken: token)
                .ConfigureAwait(false);
        }, cancellationToken);

    public async Task<EngagementSummary> GetAsync(Guid engagementId, CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken);
        var summary = await BuildSummaryQuery()
            .FirstOrDefaultAsync(e => e.EngagementId == engagementId, cancellationToken)
            .ConfigureAwait(false);
        return summary ?? throw new NotFoundException("That financial year does not exist in this workspace.");
    }

    public async Task<IReadOnlyList<EngagementSummary>> ListByCompanyAsync(
        Guid companyId,
        CancellationToken cancellationToken = default) =>
        await BuildSummaryQuery()
            .Where(e => e.CompanyId == companyId)
            .OrderByDescending(e => e.PeriodEnd)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<EngagementSummary>> ListRecentAsync(
        int limit = 10,
        CancellationToken cancellationToken = default) =>
        await BuildSummaryQuery()
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Only finalized, earlier engagements of the same company are eligible (ADR-003).</summary>
    public async Task<IReadOnlyList<PriorYearOption>> EligiblePriorYearsAsync(
        Guid companyId,
        DateOnly? currentPeriodEnd = null,
        CancellationToken cancellationToken = default)
    {
        await _companyAuthorization.RequireAccessAsync(companyId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var periodEnd = currentPeriodEnd is null ? null : FinancialYear.Format(currentPeriodEnd.Value);

        var query =
            from engagement in _dbContext.Engagements.AsNoTracking()
            join year in _dbContext.FinancialYears.AsNoTracking()
                on engagement.FinancialYearId equals year.FinancialYearId
            where engagement.CompanyId == companyId
                  && engagement.Status == EngagementStatus.Finalized
                  && _dbContext.EngagementMembers.Any(m => m.EngagementId == engagement.EngagementId
                      && m.UserId == _actor.UserId && m.Status == "ACTIVE"
                      && _dbContext.Users.Any(u => u.UserId == m.UserId && u.Status == "ACTIVE")
                      && _dbContext.RolePermissions.Any(p => p.RoleId == m.RoleId
                          && p.PermissionKey == Permissions.ViewEngagement))
                  && (periodEnd == null || string.Compare(year.PeriodEnd, periodEnd) < 0)
            orderby year.PeriodEnd descending
            select new PriorYearOption
            {
                EngagementId = engagement.EngagementId,
                Label = year.Label,
                PeriodEnd = year.PeriodEnd,
                FinalizedAtUtc = engagement.FinalizedAtUtc!,
            };

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<Engagement> LoadAsync(Guid engagementId, CancellationToken cancellationToken = default) =>
        await _dbContext.Engagements.FirstOrDefaultAsync(e => e.EngagementId == engagementId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new NotFoundException("That financial year does not exist in this workspace.");

    internal async Task<FinancialYear> LoadYearAsync(Guid financialYearId, CancellationToken cancellationToken = default) =>
        await _dbContext.FinancialYears
            .FirstOrDefaultAsync(y => y.FinancialYearId == financialYearId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new NotFoundException("That financial year definition is missing from this workspace.");

    private async Task<FinancialYear> EnsureFinancialYearAsync(
        string label,
        DateOnly periodStart,
        DateOnly periodEnd,
        CancellationToken cancellationToken)
    {
        var start = FinancialYear.Format(periodStart);
        var end = FinancialYear.Format(periodEnd);
        var trimmedLabel = (label ?? string.Empty).Trim();

        var existing = await _dbContext.FinancialYears
            .FirstOrDefaultAsync(y => y.Label == trimmedLabel && y.PeriodStart == start && y.PeriodEnd == end,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var created = FinancialYear.Create(
            Guid.NewGuid(), trimmedLabel, periodStart, periodEnd, IClock.Format(_clock.UtcNow));
        _dbContext.FinancialYears.Add(created);
        return created;
    }

    private IQueryable<EngagementSummary> BuildSummaryQuery() =>
        from engagement in _dbContext.Engagements.AsNoTracking()
        join year in _dbContext.FinancialYears.AsNoTracking()
            on engagement.FinancialYearId equals year.FinancialYearId
        join company in _dbContext.Companies.AsNoTracking()
            on engagement.CompanyId equals company.CompanyId
        where _dbContext.EngagementMembers.Any(m => m.EngagementId == engagement.EngagementId
            && m.UserId == _actor.UserId && m.Status == "ACTIVE"
            && _dbContext.Users.Any(u => u.UserId == m.UserId && u.Status == "ACTIVE")
            && _dbContext.RolePermissions.Any(p => p.RoleId == m.RoleId
                && p.PermissionKey == Permissions.ViewEngagement))
        select new EngagementSummary
        {
            EngagementId = engagement.EngagementId,
            CompanyId = company.CompanyId,
            CompanyLegalName = company.LegalName,
            CompanyShortName = company.ShortName,
            Label = year.Label,
            PeriodStart = year.PeriodStart,
            PeriodEnd = year.PeriodEnd,
            Status = engagement.Status,
            CurrencyCode = engagement.CurrencyCode,
            MinorUnitScale = engagement.MinorUnitScale,
            RowVersion = engagement.RowVersion,
            CreatedAtUtc = engagement.CreatedAtUtc,
            FinalizedAtUtc = engagement.FinalizedAtUtc,
            FinalizedByDisplayName = _dbContext.Users
                .Where(u => u.UserId == engagement.FinalizedBy)
                .Select(u => u.DisplayName)
                .FirstOrDefault(),
            FinalizationDigest = engagement.FinalizationDigest,
            PriorEngagementId = _dbContext.PriorYearRelationships
                .Where(r => r.CurrentEngagementId == engagement.EngagementId)
                .Select(r => (Guid?)r.PriorEngagementId)
                .FirstOrDefault(),
            PriorEngagementLabel = (
                from relationship in _dbContext.PriorYearRelationships
                join priorEngagement in _dbContext.Engagements
                    on relationship.PriorEngagementId equals priorEngagement.EngagementId
                join priorYear in _dbContext.FinancialYears
                    on priorEngagement.FinancialYearId equals priorYear.FinancialYearId
                where relationship.CurrentEngagementId == engagement.EngagementId
                select priorYear.Label).FirstOrDefault(),
            AccountCount = _dbContext.Accounts.Count(a => a.EngagementId == engagement.EngagementId),
        };
}
