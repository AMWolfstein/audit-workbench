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

    /// <summary>Must be established server-side by the configured authentication adapter.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Authentication adapter name (Local, Windows, OIDC, etc.); never trusted from command input.</summary>
    string AuthenticationMethod { get; }

    /// <summary>True only for the explicitly non-production local development identity.</summary>
    bool IsLocalDemoIdentity { get; }
}
