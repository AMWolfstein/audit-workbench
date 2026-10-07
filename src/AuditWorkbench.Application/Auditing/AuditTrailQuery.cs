using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Auditing;

public sealed class AuditEventRow
{
    public required long SequenceNo { get; init; }

    public required string OccurredAtUtc { get; init; }

    public required string EventType { get; init; }

    public required string Outcome { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string Description { get; init; }

    public required Guid ActorUserId { get; init; }

    public required string ActorDisplayName { get; init; }

    public Guid? CompanyId { get; init; }

    public Guid? EngagementId { get; init; }

    public required string DetailsJson { get; init; }
}

public sealed class AuditTrailQuery
{
    private const int MaxLimit = 1000;

    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly EngagementAuthorizationService _authorization;

    public AuditTrailQuery(AuditWorkbenchDbContext dbContext, EngagementAuthorizationService authorization)
    {
        _dbContext = dbContext;
        _authorization = authorization;
    }

    /// <summary>
    /// Reads audit events. Query authorization is as important as command
    /// authorization (security-model.md section 5): an engagement-scoped read
    /// requires VIEW_AUDIT_TRAIL on that engagement, and unscoped reads only
    /// ever return events of engagements the actor may read under that
    /// permission; workspace-level events additionally require workspace privilege (ADR-024).
    /// </summary>
    public async Task<IReadOnlyList<AuditEventRow>> ListAsync(
        Guid? companyId = null,
        Guid? engagementId = null,
        string? eventType = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var query = _dbContext.AuditEvents.AsNoTracking().AsQueryable();

        if (engagementId is not null)
        {
            await _authorization.RequireAsync(engagementId.Value, Permissions.ViewAuditTrail, cancellationToken)
                .ConfigureAwait(false);
            query = query.Where(e => e.EngagementId == engagementId);
        }
        else
        {
            var permitted = (await _authorization
                .PermittedEngagementIdsAsync(Permissions.ViewAuditTrail, cancellationToken)
                .ConfigureAwait(false)).Select(id => (Guid?)id).ToList();
            // Workspace-level events (no engagement) are visible only to workspace-privileged actors.
            var includeWorkspaceEvents = await _authorization.HasWorkspacePrivilegeAsync(cancellationToken)
                .ConfigureAwait(false);
            query = includeWorkspaceEvents
                ? query.Where(e => e.EngagementId == null || permitted.Contains(e.EngagementId))
                : query.Where(e => e.EngagementId != null && permitted.Contains(e.EngagementId));
        }

        if (companyId is not null)
        {
            query = query.Where(e => e.CompanyId == companyId);
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            query = query.Where(e => e.EventType == eventType);
        }

        return await query
            .OrderByDescending(e => e.SequenceNo)
            .Take(limit)
            .Select(e => new AuditEventRow
            {
                SequenceNo = e.SequenceNo,
                OccurredAtUtc = e.OccurredAtUtc,
                EventType = e.EventType,
                Outcome = e.Outcome,
                EntityType = e.EntityType,
                EntityId = e.EntityId,
                Description = e.Description,
                ActorUserId = e.ActorUserId,
                ActorDisplayName = e.ActorDisplayName,
                CompanyId = e.CompanyId,
                EngagementId = e.EngagementId,
                DetailsJson = e.DetailsJson,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Recomputes the hash chain; false means the trail was altered out of band.</summary>
    public async Task<bool> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        // Chain verification reads every event hash. It exposes no event content, but is
        // still an audit-trail operation and therefore requires audit permission.
        var permitted = await _authorization
            .PermittedEngagementIdsAsync(Permissions.ViewAuditTrail, cancellationToken)
            .ConfigureAwait(false);
        if (permitted.Count == 0 &&
            !await _authorization.HasWorkspacePrivilegeAsync(cancellationToken).ConfigureAwait(false))
            throw new AuthorizationException("You do not have permission to verify the audit trail.");

        var events = await _dbContext.AuditEvents
            .AsNoTracking()
            .OrderBy(e => e.SequenceNo)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? previousHash = null;
        foreach (var auditEvent in events)
        {
            if (auditEvent.PreviousEventHash != previousHash || !auditEvent.HashMatches())
            {
                return false;
            }

            previousHash = auditEvent.EventHash;
        }

        return true;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var permitted = (await _authorization
            .PermittedEngagementIdsAsync(Permissions.ViewAuditTrail, cancellationToken)
            .ConfigureAwait(false)).Select(id => (Guid?)id).ToList();
        var includeWorkspace = await _authorization.HasWorkspacePrivilegeAsync(cancellationToken)
            .ConfigureAwait(false);
        return includeWorkspace
            ? await _dbContext.AuditEvents.CountAsync(
                e => e.EngagementId == null || permitted.Contains(e.EngagementId), cancellationToken)
            : await _dbContext.AuditEvents.CountAsync(
                e => e.EngagementId != null && permitted.Contains(e.EngagementId), cancellationToken);
    }

    public static IReadOnlyList<string> KnownEventTypes => new[]
    {
        AuditEventType.CompanyCreated,
        AuditEventType.EngagementCreated,
        AuditEventType.EngagementStatusChanged,
        AuditEventType.AccountCreated,
        AuditEventType.FinancialDataAdded,
        AuditEventType.FinancialDataChanged,
        AuditEventType.EngagementFinalized,
        AuditEventType.PriorYearLinked,
        AuditEventType.BackupCreated,
        AuditEventType.DemoDataSeeded,
        AuditEventType.EngagementMemberAdded,
        AuditEventType.EngagementMemberRoleChanged,
        AuditEventType.EngagementMemberSuspended,
        AuditEventType.EngagementMemberReactivated,
        AuditEventType.AssignmentCreated,
        AuditEventType.AssignmentReassigned,
        AuditEventType.AssignmentCompleted,
        AuditEventType.AssignmentCancelled,
        AuditEventType.UserCreated,
        AuditEventType.UserDeactivated,
        AuditEventType.UserReactivated,
    };
}
