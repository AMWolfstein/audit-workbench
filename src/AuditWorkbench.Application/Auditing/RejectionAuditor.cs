using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.Auditing;

/// <summary>
/// Records refused protected writes (authorization failures and writes against a
/// finalized engagement). The refused command's transaction has already rolled
/// back, so the REJECTED event is appended in its own short transaction through the
/// normal writer: it reads the last committed event, so sequence_no stays contiguous
/// and previous_event_hash links to it. Details carry only the error code, never
/// values or the exception message. Recording is best effort and never throws, so
/// it cannot hide the original refusal.
/// </summary>
public sealed class RejectionAuditor
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly AuditTrailWriter _auditTrail;

    public RejectionAuditor(AuditWorkbenchDbContext dbContext, AuditTrailWriter auditTrail)
    {
        _dbContext = dbContext;
        _auditTrail = auditTrail;
    }

    public static bool ShouldRecord(Exception exception) =>
        exception is AuthorizationException or EngagementFinalizedException;

    /// <summary>Call only after the failed unit of work was rolled back and the tracker cleared.</summary>
    public async Task RecordAsync(Exception exception)
    {
        if (exception is not AuditWorkbenchException refusal || !ShouldRecord(exception))
        {
            return;
        }

        try
        {
            // The audit_event foreign key requires an existing engagement; a caller-supplied
            // id for an unknown engagement is recorded as a workspace-level event instead.
            Guid? engagementId = null;
            Guid? companyId = null;
            if (refusal.EngagementId is { } id)
            {
                var engagement = await _dbContext.Engagements.AsNoTracking()
                    .Where(e => e.EngagementId == id)
                    .Select(e => new { e.EngagementId, e.CompanyId })
                    .FirstOrDefaultAsync().ConfigureAwait(false);
                engagementId = engagement?.EngagementId;
                companyId = engagement?.CompanyId;
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
            await _auditTrail.AppendAsync(
                    AuditEventType.ProtectedWriteRejected,
                    engagementId is null ? AuditEntityType.Workspace : AuditEntityType.Engagement,
                    engagementId?.ToString("D") ?? "WORKSPACE",
                    $"A protected write was rejected ({refusal.Code}).",
                    companyId: companyId,
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("code", refusal.Code)
                        .With("reason", refusal is AuthorizationException ? "NOT_AUTHORIZED" : "ENGAGEMENT_FINALIZED"),
                    outcome: AuditEventOutcome.Rejected)
                .ConfigureAwait(false);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort by design (for example the actor has no application account, so the
            // audit_event actor foreign key cannot be satisfied). Never mask the real refusal.
            _dbContext.ChangeTracker.Clear();
        }
    }
}
