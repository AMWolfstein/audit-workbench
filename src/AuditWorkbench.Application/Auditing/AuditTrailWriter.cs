using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Auditing;

/// <summary>
/// Appends audit events inside the caller's transaction (invariant 10). Events
/// are hash chained so later tampering is detectable.
/// </summary>
public sealed class AuditTrailWriter
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;

    public AuditTrailWriter(AuditWorkbenchDbContext dbContext, IClock clock, ICurrentActor actor)
    {
        _dbContext = dbContext;
        _clock = clock;
        _actor = actor;
    }

    public async Task<AuditEvent> AppendAsync(
        string eventType,
        string entityType,
        string entityId,
        string description,
        Guid? companyId = null,
        Guid? engagementId = null,
        AuditDetails? details = null,
        string outcome = AuditEventOutcome.Success,
        CancellationToken cancellationToken = default)
    {
        // Events appended earlier in the same unit of work are tracked but not yet
        // saved; they are always newer than anything already persisted.
        var last = _dbContext.AuditEvents.Local
                .OrderByDescending(e => e.SequenceNo)
                .FirstOrDefault()
            ?? await _dbContext.AuditEvents
                .OrderByDescending(e => e.SequenceNo)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

        var auditEvent = AuditEvent.Create(
            Guid.NewGuid(),
            (last?.SequenceNo ?? 0) + 1,
            IClock.Format(_clock.UtcNow),
            _actor.UserId,
            _actor.DisplayName,
            eventType,
            entityType,
            entityId,
            description,
            companyId,
            engagementId,
            (details ?? AuditDetails.Empty()).ToJson(),
            last?.EventHash,
            outcome);

        _dbContext.AuditEvents.Add(auditEvent);
        return auditEvent;
    }
}
