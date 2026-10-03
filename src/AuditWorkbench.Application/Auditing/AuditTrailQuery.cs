using AuditWorkbench.Domain.Auditing;
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
    private readonly AuditWorkbenchDbContext _dbContext;

    public AuditTrailQuery(AuditWorkbenchDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<AuditEventRow>> ListAsync(
        Guid? companyId = null,
        Guid? engagementId = null,
        string? eventType = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.AuditEvents.AsNoTracking().AsQueryable();

        if (companyId is not null)
        {
            query = query.Where(e => e.CompanyId == companyId);
        }

        if (engagementId is not null)
        {
            query = query.Where(e => e.EngagementId == engagementId);
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

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _dbContext.AuditEvents.CountAsync(cancellationToken);

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
    };
}
