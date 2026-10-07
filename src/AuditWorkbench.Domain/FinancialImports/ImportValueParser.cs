using System.Globalization;
using System.Text;

namespace AuditWorkbench.Domain.FinancialImports;

/// <summary>
/// Tolerant but deterministic parsing of client-supplied cells. Client files
/// contain "(1,234.56)", "1.234,56", "1 234.56", "31-Jan-2026", Excel serial
/// dates and stray currency symbols; every rule below is fixed so the same file
/// always produces the same import.
/// </summary>
public static class ImportValueParser
{
    private static readonly string[] CurrencySymbols = { "$", "€", "£", "¥", "₹", "kr", "CHF", "USD", "EUR", "GBP" };

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyyMMdd",
        "dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy",
        "d-M-yyyy", "d/M/yyyy", "d.M.yyyy",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy",
        "MMM-dd-yyyy", "MMM d, yyyy", "MMM d yyyy",
        "dd-MMM-yy", "d-MMM-yy", "MM/dd/yyyy", "M/d/yyyy",
    };

    public static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>Text cell normalization: trimmed, internal whitespace collapsed.</summary>
    public static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(" ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Parses a monetary cell into signed minor units.</summary>
    public static bool TryParseAmount(string? raw, int scale, out long minorUnits)
    {
        minorUnits = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim().Trim('"', '\'').Replace("\u00a0", string.Empty).Replace(" ", string.Empty);
        if (text.Length == 0)
        {
            return false;
        }

        var negative = false;
        if (text.Length > 2 && text[0] == '(' && text[^1] == ')')
        {
            negative = true;
            text = text[1..^1];
        }

        if (text.EndsWith("-", StringComparison.Ordinal))
        {
            negative = !negative;
            text = text[..^1];
        }

        foreach (var symbol in CurrencySymbols)
        {
            if (text.StartsWith(symbol, StringComparison.OrdinalIgnoreCase))
            {
                text = text[symbol.Length..];
            }

            if (text.EndsWith(symbol, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^symbol.Length];
            }
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text[0] == '+')
        {
            text = text[1..];
        }
        else if (text[0] == '-')
        {
            negative = !negative;
            text = text[1..];
        }

        if (text.Length == 0 || !text.All(c => char.IsDigit(c) || c == '.' || c == ','))
        {
            return false;
        }

        text = NormalizeDecimalSeparators(text, scale, out var valid);
        if (!valid)
        {
            return false;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var scaled = value * Pow10(scale);
        if (scaled != decimal.Truncate(scaled))
        {
            return false;
        }

        if (negative)
        {
            scaled = -scaled;
        }

        if (scaled > long.MaxValue || scaled < long.MinValue)
        {
            return false;
        }

        minorUnits = (long)scaled;
        return true;
    }

    /// <summary>
    /// Parses a date cell into an ISO-8601 date. Numbers are read as Excel serial
    /// dates (1900 date system), which is how spreadsheets store dates.
    /// </summary>
    public static bool TryParseDate(string? raw, out string isoDate)
    {
        isoDate = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = Text(raw);
        if (text.Length == 0)
        {
            return false;
        }

        // Excel serial dates: "46022" (and "46022.0"), never an 8-digit yyyyMMdd key.
        var numeric = text.Trim();
        if (numeric.Length is > 0 and <= 6 &&
            numeric.All(c => char.IsDigit(c) || c == '.') &&
            !(numeric.Length == 6 && numeric.All(char.IsDigit)))
        {
            if (decimal.TryParse(numeric, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var serial) &&
                serial >= 1m && serial <= 60000m)
            {
                var date = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified).AddDays((double)decimal.Truncate(serial));
                isoDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return true;
            }
        }

        var normalized = text.Replace("\u00a0", " ").Trim();

        // Deterministic disambiguation of ambiguous dd/MM vs MM/dd:
        // a component above 12 decides; otherwise day-first is assumed.
        var separator = normalized.Contains('/', StringComparison.Ordinal)
            ? '/'
            : normalized.Contains('-', StringComparison.Ordinal) && !LooksLikeIso(normalized)
                ? '-'
                : normalized.Contains('.', StringComparison.Ordinal) ? '.' : '\0';
        if (separator != '\0' && !LooksLikeIso(normalized))
        {
            var pieces = normalized.Split(separator, StringSplitOptions.TrimEntries);
            if (pieces.Length == 3 && pieces.All(p => p.Length > 0))
            {
                var first = ParseInt(pieces[0]);
                var second = ParseInt(pieces[1]);
                var third = ParseInt(pieces[2]);

                // yyyy followed by month and day (a separator other than '-' was used).
                if (first is > 1900 and < 2200 && second is >= 1 and <= 12 && third is >= 1 and <= 31)
                {
                    return TryCompose(first.Value, second.Value, third.Value, out isoDate);
                }

                // Day/month followed by a two-digit or four-digit year.
                if (first is >= 1 and <= 31 && second is >= 1 and <= 12 && third is >= 0 and <= 2199)
                {
                    var year = third.Value >= 100
                        ? third.Value
                        : third.Value >= 70
                            ? 1900 + third.Value
                            : 2000 + third.Value;

                    // A component above 12 decides the order; when both could be a
                    // month, day-first is the documented rule.
                    if (first.Value > 12 && second.Value <= 12)
                    {
                        return TryCompose(year, second.Value, first.Value, out isoDate);
                    }

                    if (second.Value > 12 && first.Value <= 12)
                    {
                        return TryCompose(year, first.Value, second.Value, out isoDate);
                    }

                    return TryCompose(year, second.Value, first.Value, out isoDate);
                }
            }
        }

        if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.None, out var parsed))
        {
            isoDate = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    /// <summary>Heuristic used only for structure detection (header row / format guessing).</summary>
    public static bool LooksNumeric(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return TryParseAmount(raw, 6, out _);
    }

    /// <summary>Heuristic used only for structure detection (a cell that reads as a date).</summary>
    public static bool LooksLikeDate(string? raw) => TryParseDate(raw, out _);

    private static string NormalizeDecimalSeparators(string text, int scale, out bool valid)
    {
        valid = true;
        var hasDot = text.Contains('.', StringComparison.Ordinal);
        var hasComma = text.Contains(',', StringComparison.Ordinal);
        if (!hasDot && !hasComma)
        {
            return text;
        }

        if (hasDot && hasComma)
        {
            // The rightmost separator is the decimal separator; the other is grouping.
            var lastDot = text.LastIndexOf('.');
            var lastComma = text.LastIndexOf(',');
            if (lastDot > lastComma)
            {
                return text.Replace(",", string.Empty, StringComparison.Ordinal);
            }

            return text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        }

        var separator = hasDot ? '.' : ',';
        var occurrences = text.Count(c => c == separator);
        if (occurrences > 1)
        {
            // Multiple separators are thousands grouping ("1.234.567").
            return text.Replace(separator.ToString(), string.Empty, StringComparison.Ordinal);
        }

        var decimals = text.Length - text.IndexOf(separator, StringComparison.Ordinal) - 1;
        if (decimals == 3 && scale < 3)
        {
            // "1,234" with a 2-decimal currency can only be thousands grouping.
            return text.Replace(separator.ToString(), string.Empty, StringComparison.Ordinal);
        }

        if (decimals > Math.Max(scale, 6))
        {
            valid = false;
            return text;
        }

        return text.Replace(separator, '.');
    }

    private static bool LooksLikeIso(string text) =>
        text.Length >= 10 && text[4] == '-' && text[7] == '-' &&
        text[..4].All(char.IsDigit) && text[5..7].All(char.IsDigit) && text[8..10].All(char.IsDigit);

    private static int? ParseInt(string piece) =>
        int.TryParse(piece, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool TryCompose(int year, int month, int day, out string isoDate)
    {
        isoDate = string.Empty;
        if (year is < 1900 or > 2200 || month is < 1 or > 12 || day is < 1 or > 31)
        {
            return false;
        }

        try
        {
            var date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
            isoDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

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
