using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Companies;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Infrastructure.Persistence;
using AuditWorkbench.Infrastructure.Workspace;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Dashboard;

public sealed class DashboardView
{
    public required int CompanyCount { get; init; }

    public required int EngagementCount { get; init; }

    public required int OpenEngagementCount { get; init; }

    public required int FinalizedEngagementCount { get; init; }

    public required int AuditEventCount { get; init; }

    public required bool AuditChainValid { get; init; }

    public required IReadOnlyList<EngagementSummary> RecentEngagements { get; init; }

    public required IReadOnlyList<AuditEventRow> RecentEvents { get; init; }

    public required string WorkspaceRoot { get; init; }

    public required string SchemaVersion { get; init; }

    public required bool UsingLocalDemoIdentity { get; init; }

    public required bool DemoDataPresent { get; init; }
}

public sealed class DashboardService
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly EngagementService _engagements;
    private readonly AuditTrailQuery _auditTrail;
    private readonly WorkspacePaths _paths;
    private readonly ICurrentActor _actor;

    public DashboardService(
        AuditWorkbenchDbContext dbContext,
        EngagementService engagements,
        AuditTrailQuery auditTrail,
        WorkspacePaths paths,
        ICurrentActor actor)
    {
        _dbContext = dbContext;
        _engagements = engagements;
        _auditTrail = auditTrail;
        _paths = paths;
        _actor = actor;
    }

    public async Task<DashboardView> GetAsync(CancellationToken cancellationToken = default)
    {
        var finalized = await _dbContext.Engagements
            .CountAsync(e => e.Status == EngagementStatus.Finalized, cancellationToken)
            .ConfigureAwait(false);
        var total = await _dbContext.Engagements.CountAsync(cancellationToken).ConfigureAwait(false);

        return new DashboardView
        {
            CompanyCount = await _dbContext.Companies.CountAsync(cancellationToken).ConfigureAwait(false),
            EngagementCount = total,
            OpenEngagementCount = total - finalized,
            FinalizedEngagementCount = finalized,
            AuditEventCount = await _auditTrail.CountAsync(cancellationToken).ConfigureAwait(false),
            AuditChainValid = await _auditTrail.VerifyChainAsync(cancellationToken).ConfigureAwait(false),
            RecentEngagements = await _engagements.ListRecentAsync(5, cancellationToken).ConfigureAwait(false),
            RecentEvents = await _auditTrail.ListAsync(limit: 8, cancellationToken: cancellationToken)
                .ConfigureAwait(false),
            WorkspaceRoot = _paths.RootDirectory,
            SchemaVersion = SqlMigrationRunner.SchemaVersion,
            UsingLocalDemoIdentity = _actor.IsLocalDemoIdentity,
            DemoDataPresent = await _dbContext.Companies
                .AnyAsync(c => c.ShortName == "ABC-DEMO", cancellationToken).ConfigureAwait(false),
        };
    }
}
