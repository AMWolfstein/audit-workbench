namespace AuditWorkbench.Domain.Common;

/// <summary>
/// The identity attributed to every mutation and audit event (ADR-013).
/// In the MVP this resolves to the single local, non-production account.
/// </summary>
public interface ICurrentActor
{
    Guid UserId { get; }

    string Username { get; }

    string DisplayName { get; }

    /// <summary>True while the workspace uses the unauthenticated local actor.</summary>
    bool IsLocalDemoIdentity { get; }
}
