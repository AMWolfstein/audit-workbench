using System.Security.Cryptography;
using System.Text;

namespace AuditWorkbench.Domain.Auditing;

/// <summary>
/// An append-only record of one action. Events are written in the same
/// transaction as the mutation they describe (invariant 10) and are chained by
/// hash so later tampering is detectable.
/// </summary>
public class AuditEvent
{
    private AuditEvent()
    {
    }

    public Guid AuditEventId { get; private set; }

    public long SequenceNo { get; private set; }

    public string OccurredAtUtc { get; private set; } = string.Empty;

    public Guid ActorUserId { get; private set; }

    public string ActorDisplayName { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public string Outcome { get; private set; } = AuditEventOutcome.Success;

    public Guid? CompanyId { get; private set; }

    public Guid? EngagementId { get; private set; }

    public string EntityType { get; private set; } = string.Empty;

    public string EntityId { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>Minimal structured metadata. Never financial values or document content (NFR-14).</summary>
    public string DetailsJson { get; private set; } = "{}";

    public string? PreviousEventHash { get; private set; }

    public string EventHash { get; private set; } = string.Empty;

    public static AuditEvent Create(
        Guid auditEventId,
        long sequenceNo,
        string occurredAtUtc,
        Guid actorUserId,
        string actorDisplayName,
        string eventType,
        string entityType,
        string entityId,
        string description,
        Guid? companyId,
        Guid? engagementId,
        string detailsJson,
        string? previousEventHash,
        string outcome = AuditEventOutcome.Success)
    {
        var auditEvent = new AuditEvent
        {
            AuditEventId = auditEventId,
            SequenceNo = sequenceNo,
            OccurredAtUtc = occurredAtUtc,
            ActorUserId = actorUserId,
            ActorDisplayName = actorDisplayName,
            EventType = eventType,
            Outcome = outcome,
            CompanyId = companyId,
            EngagementId = engagementId,
            EntityType = entityType,
            EntityId = entityId,
            Description = description,
            DetailsJson = string.IsNullOrWhiteSpace(detailsJson) ? "{}" : detailsJson,
            PreviousEventHash = previousEventHash,
        };

        auditEvent.EventHash = ComputeHash(auditEvent);
        return auditEvent;
    }

    /// <summary>
    /// Canonical hash input. The format is shared with the verification harness
    /// (tools/verification/workspace.py) so either runtime can validate a chain.
    /// </summary>
    public static string CanonicalForm(AuditEvent e) => string.Join(
        "|",
        e.SequenceNo.ToString(),
        e.AuditEventId.ToString("D"),
        e.OccurredAtUtc,
        e.ActorUserId.ToString("D"),
        e.EventType,
        e.Outcome,
        e.CompanyId?.ToString("D") ?? "NONE",
        e.EngagementId?.ToString("D") ?? "NONE",
        e.EntityType,
        e.EntityId,
        e.Description,
        e.DetailsJson,
        e.PreviousEventHash ?? "GENESIS");

    public static string ComputeHash(AuditEvent e) => Sha256Hex(CanonicalForm(e));

    public bool HashMatches() => EventHash == ComputeHash(this);

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(new UTF8Encoding(false).GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
