using System.Globalization;
using System.Text.RegularExpressions;
using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Money;

/// <summary>
/// Exact monetary conversion between operator input and stored minor units
/// (ADR-009). Binary floating point is never used.
/// </summary>
public static class MoneyPolicy
{
    public const int MaxScale = 6;

    // Plain digits, or digits with commas only as valid thousands groups ("1,234,567.89").
    // A comma anywhere else ("1234,56", "12,34") is ambiguous with a decimal comma and is rejected.
    private static readonly Regex AmountPattern = new(
        @"^[+-]?(\d+|\d{1,3}(,\d{3})+)(\.\d+)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Parses operator input such as "850000000" or "-1,234.56" into minor units.</summary>
    public static long ParseToMinor(string? input, int scale)
    {
        if (scale is < 0 or > MaxScale)
        {
            throw new ValidationException($"Minor unit scale must be between 0 and {MaxScale}.");
        }

        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ValidationException("Amount is required.");
        }

        if (!AmountPattern.IsMatch(trimmed))
        {
            throw new ValidationException(
                $"'{input}' is not a valid amount. Use digits with an optional decimal point; " +
                "commas are accepted only as thousands separators (for example 1,234.56).");
        }

        var cleaned = trimmed.Replace(",", string.Empty);

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
