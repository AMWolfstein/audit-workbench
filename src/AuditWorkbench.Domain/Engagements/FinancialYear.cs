using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Engagements;

/// <summary>
/// Reference data describing a reporting period. Dates are authoritative; the
/// label is for display. A definition in use is never re-dated.
/// </summary>
public class FinancialYear
{
    private FinancialYear()
    {
    }

    public Guid FinancialYearId { get; private set; }

    public string Label { get; private set; } = string.Empty;

    /// <summary>ISO-8601 date (yyyy-MM-dd).</summary>
    public string PeriodStart { get; private set; } = string.Empty;

    /// <summary>ISO-8601 date (yyyy-MM-dd).</summary>
    public string PeriodEnd { get; private set; } = string.Empty;

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public static FinancialYear Create(
        Guid financialYearId,
        string label,
        DateOnly periodStart,
        DateOnly periodEnd,
        string createdAtUtc)
    {
        label = (label ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            throw new ValidationException("Financial year label is required.");
        }

        if (periodStart > periodEnd)
        {
            throw new ValidationException("Period start must not be after period end.");
        }

        return new FinancialYear
        {
            FinancialYearId = financialYearId,
            Label = label,
            PeriodStart = Format(periodStart),
            PeriodEnd = Format(periodEnd),
            CreatedAtUtc = createdAtUtc,
        };
    }

    public static string Format(DateOnly value) => value.ToString("yyyy-MM-dd");

    public DateOnly Start => DateOnly.Parse(PeriodStart);

    public DateOnly End => DateOnly.Parse(PeriodEnd);

    public bool Overlaps(DateOnly start, DateOnly end) => Start <= end && End >= start;
}
