using System.Text.Json;
using AuditWorkbench.Domain.FinancialImports;

namespace AuditWorkbench.Application.FinancialData.Imports;

/// <summary>Shared helpers for streaming import pipelines.</summary>
internal static class ImportRowSupport
{
    public const int MaxDescriptionLength = 512;

    public const int MaxNameLength = 200;

    /// <summary>
    /// Client trial balances and ledger extracts often end with a totals row.
    /// Those rows are recognized and skipped with an informational finding; they
    /// are never treated as data and never silently mistaken for an account.
    /// </summary>
    public static bool IsTotalsRow(TabularRow row, int accountCodeColumn)
    {
        var first = ImportValueParser.Text(row.Cell(accountCodeColumn));
        if (first.Length > 0 && !LooksLikeTotalsLabel(first))
        {
            // The account-code column carries a value: only treat it as a totals row
            // when the label is an obvious totals caption.
            return LooksLikeTotalsLabel(first) && row.Cells.Skip(accountCodeColumn + 1)
                .All(cell => ImportValueParser.IsBlank(cell) || ImportValueParser.LooksNumeric(cell));
        }

        return LooksLikeTotalsLabel(first) ||
               row.Cells.Any(cell => LooksLikeTotalsLabel(ImportValueParser.Text(cell))) &&
               row.Cells.All(cell => ImportValueParser.IsBlank(cell) || ImportValueParser.LooksNumeric(cell) ||
                                     LooksLikeTotalsLabel(ImportValueParser.Text(cell)));
    }

    private static bool LooksLikeTotalsLabel(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var text = value.Trim().ToLowerInvariant();
        return text is "total" or "totals" or "subtotal" or "sub-total" or "grand total" or "sum" or "cumulative"
            || text.StartsWith("total ", StringComparison.Ordinal)
            || text.StartsWith("subtotal", StringComparison.Ordinal)
            || text.StartsWith("grand total", StringComparison.Ordinal);
    }

    /// <summary>
    /// Preserves every unmapped client column of the row as JSON so a
    /// client-specific attribute never forces a schema change.
    /// </summary>
    public static string BuildExtraColumnsJson(ImportStructureInfo structure, TabularRow row,
        ImportColumnMapping mapping)
    {
        var extras = new Dictionary<string, string?>(StringComparer.Ordinal);
        var limit = Math.Min(structure.Headers.Count, row.Cells.Length);
        for (var index = 0; index < limit; index++)
        {
            if (mapping.FieldFor(index) is not null)
            {
                continue;
            }

            var value = row.Cell(index);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var header = index < structure.Headers.Count ? structure.Headers[index] : $"Column {index + 1}";
            extras[header] = value.Trim();
        }

        return extras.Count == 0 ? "{}" : JsonSerializer.Serialize(extras);
    }

    public static string Truncate(string? value, int maxLength, ImportValidationReport report, string field,
        int rowNumber)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        report.AddInfo(ImportIssueCodes.RowLimitExceeded,
            $"The {field} value on this row was longer than {maxLength} characters and was truncated for storage.",
            rowNumber);
        return text[..maxLength];
    }
}
