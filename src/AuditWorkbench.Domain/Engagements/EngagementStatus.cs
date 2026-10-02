using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Engagements;

/// <summary>
/// Engagement lifecycle states.
/// <para>
/// DRAFT and IN_PROGRESS are both *open* (editable) states; FINALIZED is
/// terminal (ADR-002). IN_PROGRESS carries no additional rule in the MVP: it
/// exists so a preparer can signal that fieldwork has started without weakening
/// the lock boundary. See ADR-018.
/// </para>
/// </summary>
public static class EngagementStatus
{
    public const string Draft = "DRAFT";
    public const string InProgress = "IN_PROGRESS";
    public const string Finalized = "FINALIZED";

    public static readonly IReadOnlyList<string> All = new[] { Draft, InProgress, Finalized };

    public static readonly IReadOnlyList<string> Open = new[] { Draft, InProgress };

    public static bool IsOpen(string status) => status is Draft or InProgress;

    public static bool IsFinalized(string status) => status == Finalized;

    /// <summary>Allowed transitions. Finalization uses its own command and is not reversible.</summary>
    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Draft, Draft) => true,
        (Draft, InProgress) => true,
        (Draft, Finalized) => true,
        (InProgress, InProgress) => true,
        (InProgress, Finalized) => true,
        _ => false,
    };

    public static void EnsureTransitionAllowed(string from, string to)
    {
        if (!All.Contains(to))
        {
            throw new ValidationException($"'{to}' is not a valid engagement status.");
        }

        if (IsFinalized(from))
        {
            throw new EngagementFinalizedException("A finalized financial year is read-only and cannot be reopened.");
        }

        if (!CanTransition(from, to))
        {
            throw new ValidationException($"An engagement cannot move from {from} to {to}.");
        }
    }
}
