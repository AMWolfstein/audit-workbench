using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.Finalization;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Finalization;

/// <summary>
/// Finalization protocol (audit-year-lifecycle.md section 5): preflight,
/// explicit confirmation, then one transaction that writes the manifest,
/// transitions the status and appends the audit event. Finalized is terminal.
/// </summary>
public sealed class FinalizationService
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly EngagementService _engagements;
    private readonly SqlQueryExecutor _queries;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;
    private readonly EngagementAuthorizationService _authorization;

    public FinalizationService(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        EngagementService engagements,
        SqlQueryExecutor queries,
        IClock clock,
        ICurrentActor actor,
        EngagementAuthorizationService authorization)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _engagements = engagements;
        _queries = queries;
        _clock = clock;
        _actor = actor;
        _authorization = authorization;
    }

    public static string ConfirmationPhrase(EngagementSummary engagement) =>
        $"{engagement.CompanyShortName} {engagement.Label}";

    public async Task<FinalizationPreflight> PreflightAsync(
        Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var warnings = new List<string>();

        var engagement = await _engagements.GetAsync(engagementId, cancellationToken).ConfigureAwait(false);
        if (engagement.IsFinalized)
        {
            problems.Add("This financial year is already finalized.");
            return FinalizationPreflight.From(problems, warnings);
        }

        var accounts = await ManifestLinesAsync(engagementId, cancellationToken).ConfigureAwait(false);
        if (accounts.Count == 0)
        {
            problems.Add("The financial year has no accounts.");
        }

        var missing = accounts.Where(a => a.RevisionNo is null).Select(a => a.AccountCode).OrderBy(c => c).ToList();
        if (missing.Count > 0)
        {
            problems.Add($"These accounts have no recorded value: {string.Join(", ", missing)}.");
        }

        var orphaned = await (
            from value in _dbContext.FinancialData
            join account in _dbContext.Accounts on value.AccountId equals account.AccountId
            where value.EngagementId == engagementId && account.EngagementId != value.EngagementId
            select value.FinancialDataId).AnyAsync(cancellationToken).ConfigureAwait(false);
        if (orphaned)
        {
            problems.Add("Financial values reference accounts from another financial year.");
        }

        if (engagement.PriorEngagementId is { } priorId)
        {
            var prior = await _engagements.GetAsync(priorId, cancellationToken).ConfigureAwait(false);
            if (!prior.IsFinalized)
            {
                problems.Add("The linked prior financial year is no longer finalized.");
            }
        }
        else
        {
            warnings.Add("This financial year has no prior-year relationship. Comparisons will be unavailable.");
        }

        warnings.Add("Finalization cannot be undone in this version. Create a backup first if you need one.");

        return FinalizationPreflight.From(problems, warnings);
    }

    public Task<string> FinalizeAsync(
        Guid engagementId,
        string confirmationText,
        int? expectedRowVersion = null,
        CancellationToken cancellationToken = default) =>
        _unitOfWork.ExecuteAsync(async token =>
        {
            await _authorization.RequireAsync(engagementId, Permissions.FinalizeEngagement, token);
            var summary = await _engagements.GetAsync(engagementId, token).ConfigureAwait(false);
            var expectedPhrase = ConfirmationPhrase(summary);
            if (!string.Equals((confirmationText ?? string.Empty).Trim(), expectedPhrase, StringComparison.Ordinal))
            {
                throw new ValidationException($"Type '{expectedPhrase}' exactly to confirm finalization.");
            }

            var engagement = await _engagements.LoadAsync(engagementId, token).ConfigureAwait(false);
            var year = await _engagements.LoadYearAsync(engagement.FinancialYearId, token).ConfigureAwait(false);
            engagement.EnsureOpenForEditing(year.Label);
            engagement.EnsureExpectedVersion(expectedRowVersion);

            var preflight = await PreflightAsync(engagementId, token).ConfigureAwait(false);
            if (!preflight.CanFinalize)
            {
                throw new ValidationException(
                    "Finalization preflight failed: " + string.Join(" ", preflight.BlockingProblems));
            }

            var document = await BuildManifestDocumentAsync(engagementId, token).ConfigureAwait(false);
            var accountCount = (await ManifestLinesAsync(engagementId, token).ConfigureAwait(false)).Count;
            var now = IClock.Format(_clock.UtcNow);

            var manifest = FinalizationManifest.Create(
                Guid.NewGuid(), engagementId, document, accountCount, now, _actor.UserId);
            _dbContext.FinalizationManifests.Add(manifest);

            // The manifest row must be visible to the database trigger that
            // guards the DRAFT -> FINALIZED transition, so flush it first.
            await _dbContext.SaveChangesAsync(token).ConfigureAwait(false);

            engagement.MarkFinalized(now, _actor.UserId, manifest.RootDigest,
                FinalizationManifestBuilder.ManifestVersion);

            await _auditTrail.AppendAsync(
                    AuditEventType.EngagementFinalized,
                    AuditEntityType.Engagement,
                    engagementId.ToString("D"),
                    $"{year.Label} finalized for {summary.CompanyLegalName}; the financial year is now read-only.",
                    companyId: engagement.CompanyId,
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("root_digest", manifest.RootDigest)
                        .With("manifest_version", manifest.ManifestVersion)
                        .With("account_count", accountCount),
                    cancellationToken: token)
                .ConfigureAwait(false);

            return manifest.RootDigest;
        }, cancellationToken);

    /// <summary>Recomputes the digest from live data and compares it with the stored manifest.</summary>
    public async Task<bool> VerifyDigestAsync(Guid engagementId, CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        var engagement = await _engagements.LoadAsync(engagementId, cancellationToken).ConfigureAwait(false);
        if (!engagement.IsFinalized)
        {
            return false;
        }

        var manifest = await _dbContext.FinalizationManifests
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.EngagementId == engagementId, cancellationToken)
            .ConfigureAwait(false);
        if (manifest is null || !manifest.DigestMatchesContent())
        {
            return false;
        }

        var recomputed = await BuildManifestDocumentAsync(engagementId, cancellationToken).ConfigureAwait(false);
        return manifest.RootDigest == engagement.FinalizationDigest
               && string.Equals(recomputed, manifest.CanonicalContent, StringComparison.Ordinal);
    }

    public async Task<FinalizationManifest?> GetManifestAsync(Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        return await _dbContext.FinalizationManifests
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.EngagementId == engagementId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> BuildManifestDocumentAsync(Guid engagementId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        var engagement = await _engagements.LoadAsync(engagementId, cancellationToken).ConfigureAwait(false);
        var year = await _engagements.LoadYearAsync(engagement.FinancialYearId, cancellationToken)
            .ConfigureAwait(false);
        var company = await _dbContext.Companies
            .AsNoTracking()
            .FirstAsync(c => c.CompanyId == engagement.CompanyId, cancellationToken)
            .ConfigureAwait(false);

        var priorId = await _dbContext.PriorYearRelationships
            .AsNoTracking()
            .Where(r => r.CurrentEngagementId == engagementId)
            .Select(r => (Guid?)r.PriorEngagementId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        string? priorDigest = null;
        if (priorId is not null)
        {
            priorDigest = await _dbContext.Engagements
                .AsNoTracking()
                .Where(e => e.EngagementId == priorId)
                .Select(e => e.FinalizationDigest)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var accounts = await ManifestLinesAsync(engagementId, cancellationToken).ConfigureAwait(false);

        return FinalizationManifestBuilder.BuildDocument(new FinalizationManifestBuilder.ManifestInput
        {
            EngagementId = engagement.EngagementId,
            CompanyId = company.CompanyId,
            CompanyLegalName = company.LegalName,
            CompanyShortName = company.ShortName,
            FinancialYearId = year.FinancialYearId,
            FinancialYearLabel = year.Label,
            PeriodStart = year.PeriodStart,
            PeriodEnd = year.PeriodEnd,
            CurrencyCode = engagement.CurrencyCode,
            MinorUnitScale = engagement.MinorUnitScale,
            PriorEngagementId = priorId,
            PriorRootDigest = priorDigest,
            Accounts = accounts,
        });
    }

    private Task<IReadOnlyList<ManifestAccountLine>> ManifestLinesAsync(
        Guid engagementId,
        CancellationToken cancellationToken) =>
        _queries.QueryAsync(
            "finalization_scope.sql",
            new Dictionary<string, object?> { ["engagement_id"] = engagementId },
            reader => new ManifestAccountLine
            {
                AccountCode = reader.GetString(reader.GetOrdinal("account_code")),
                AccountName = reader.GetString(reader.GetOrdinal("account_name")),
                AccountType = reader.GetString(reader.GetOrdinal("account_type")),
                RevisionNo = SqlQueryExecutor.GetNullableInt32(reader, "revision_no"),
                AmountMinor = SqlQueryExecutor.GetNullableInt64(reader, "amount_minor"),
            },
            cancellationToken);
}
