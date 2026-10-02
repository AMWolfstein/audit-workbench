using System.Globalization;

namespace AuditWorkbench.Domain.Comparison;

/// <summary>
/// One line of the comparative view. The row is a projection of two
/// independent engagements; computing it never writes to either of them.
/// </summary>
public sealed class ComparativeRow
{
    public required string AccountCode { get; init; }

    public required string AccountName { get; init; }

    public long? PriorAmountMinor { get; init; }

    public long? CurrentAmountMinor { get; init; }

    public int? PriorRevisionNo { get; init; }

    public int? CurrentRevisionNo { get; init; }

    /// <summary>The account has no counterpart in the prior year.</summary>
    public bool IsNewAccount { get; init; }

    /// <summary>The account existed in the prior year but not in the current year.</summary>
    public bool IsMissingInCurrent { get; init; }

    public long? ChangeAmountMinor =>
        PriorAmountMinor is null || CurrentAmountMinor is null
            ? null
            : CurrentAmountMinor.Value - PriorAmountMinor.Value;

    /// <summary>
    /// change / |prior| * 100, or null when it is undefined. A zero prior amount
    /// yields N/A rather than a divide-by-zero or an invented percentage (FR-M09).
    /// </summary>
    public decimal? ChangePercent
    {
        get
        {
            var change = ChangeAmountMinor;
            if (change is null || PriorAmountMinor is null || PriorAmountMinor.Value == 0)
            {
                return null;
            }

            return change.Value / (decimal)Math.Abs(PriorAmountMinor.Value) * 100m;
        }
    }

    /// <summary>Display value rounded half-up to two decimals, or "N/A".</summary>
    public string ChangePercentDisplay =>
        ChangePercent is null
            ? "N/A"
            : Math.Round(ChangePercent.Value, 2, MidpointRounding.AwayFromZero)
                .ToString("0.00", CultureInfo.InvariantCulture);

    public string StatusLabel => (IsNewAccount, IsMissingInCurrent) switch
    {
        (true, false) => "New this year",
        (false, true) => "Not in current year",
        _ => string.Empty,
    };
}
