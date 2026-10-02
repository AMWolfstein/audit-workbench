using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Engagements;

/// <summary>
/// One-way, immutable reference from a current engagement to one finalized,
/// earlier engagement of the same company (ADR-003). The prior year gains no
/// mutable state from being referenced.
/// </summary>
public class PriorYearRelationship
{
    private PriorYearRelationship()
    {
    }

    public Guid RelationshipId { get; private set; }

    public Guid CurrentEngagementId { get; private set; }

    public Guid PriorEngagementId { get; private set; }

    public string LinkedAtUtc { get; private set; } = string.Empty;

    public Guid LinkedBy { get; private set; }

    /// <summary>
    /// Validates every eligibility rule before the row is created. The same
    /// rules are repeated by database triggers as defence in depth.
    /// </summary>
    public static PriorYearRelationship Create(
        Guid relationshipId,
        Engagement current,
        FinancialYear currentYear,
        Engagement prior,
        FinancialYear priorYear,
        string linkedAtUtc,
        Guid linkedBy)
    {
        if (current.EngagementId == prior.EngagementId)
        {
            throw new ValidationException("An engagement cannot be its own prior year.");
        }

        if (current.CompanyId != prior.CompanyId)
        {
            throw new ValidationException("The prior engagement must belong to the same company.");
        }

        if (!prior.IsFinalized)
        {
            throw new ValidationException(
                $"Only a finalized financial year can be selected as the prior year. {priorYear.Label} is {prior.Status}.");
        }

        if (current.IsFinalized)
        {
            throw new EngagementFinalizedException("A finalized financial year cannot gain a prior-year link.");
        }

        if (priorYear.End >= currentYear.End)
        {
            throw new ValidationException(
                $"The prior period ({priorYear.Label}) must end before the current period ({currentYear.Label}).");
        }

        return new PriorYearRelationship
        {
            RelationshipId = relationshipId,
            CurrentEngagementId = current.EngagementId,
            PriorEngagementId = prior.EngagementId,
            LinkedAtUtc = linkedAtUtc,
            LinkedBy = linkedBy,
        };
    }

    /// <summary>A gap between the periods is allowed but is surfaced as a warning.</summary>
    public static string? PeriodGapWarning(FinancialYear priorYear, FinancialYear currentYear)
    {
        var expectedStart = priorYear.End.AddDays(1);
        return currentYear.Start == expectedStart
            ? null
            : $"Note: {currentYear.Label} does not start on the day after {priorYear.Label} ends " +
              $"({priorYear.PeriodEnd} -> {currentYear.PeriodStart}). The comparison is still valid.";
    }
}
