using System.Globalization;
using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Money;

/// <summary>
/// Exact monetary conversion between operator input and stored minor units
/// (ADR-009). Binary floating point is never used.
/// </summary>
public static class MoneyPolicy
{
    public const int MaxScale = 6;

    /// <summary>Parses operator input such as "850000000" or "-1,234.56" into minor units.</summary>
    public static long ParseToMinor(string? input, int scale)
    {
        if (scale is < 0 or > MaxScale)
        {
            throw new ValidationException($"Minor unit scale must be between 0 and {MaxScale}.");
        }

        var cleaned = (input ?? string.Empty).Trim().Replace(",", string.Empty).Replace(" ", string.Empty);
        if (cleaned.Length == 0)
        {
            throw new ValidationException("Amount is required.");
        }

        if (!decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
        {
            throw new ValidationException($"'{input}' is not a valid amount.");
        }

        var scaled = value * Pow10(scale);
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ValidationException(
                $"'{input}' has more decimal places than this financial year allows ({scale}).");
        }

        if (scaled > long.MaxValue || scaled < long.MinValue)
        {
            throw new ValidationException("Amount exceeds the supported 64-bit minor-unit range.");
        }

        return (long)scaled;
    }

    /// <summary>Converts stored minor units into an exact decimal for display.</summary>
    public static decimal ToDecimal(long amountMinor, int scale) => amountMinor / Pow10(scale);

    public static string Format(long? amountMinor, int scale)
    {
        if (amountMinor is null)
        {
            return string.Empty;
        }

        return ToDecimal(amountMinor.Value, scale).ToString("N" + scale, CultureInfo.InvariantCulture);
    }

    /// <summary>Round-trips a stored value into the plain text shown in an edit field.</summary>
    public static string ToEditString(long amountMinor, int scale) =>
        ToDecimal(amountMinor, scale).ToString("F" + scale, CultureInfo.InvariantCulture);

    private static decimal Pow10(int scale)
    {
        decimal result = 1m;
        for (var i = 0; i < scale; i++)
        {
            result *= 10m;
        }

        return result;
    }
}
