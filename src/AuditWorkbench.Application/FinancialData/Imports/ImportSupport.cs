using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;

namespace AuditWorkbench.Application.FinancialData.Imports;

/// <summary>Mapping-level checks and operator-facing summaries shared by the importers.</summary>
internal static class ImportSupport
{
    /// <summary>Placeholder fingerprint used before a validation pass has measured the file.</summary>
    public static string PendingFingerprint() => new('0', 64);

    public static void EnsureMappingIsUsable(ImportValidationReport report, ImportStructureInfo structure,
        ImportColumnMapping mapping, string datasetKind)
    {
        var definitions = GlFields.For(datasetKind);
        if (structure.HeaderRowNumber == 0 && structure.ColumnCount == 0)
        {
            report.AddError(ImportIssueCodes.EmptyFile, "The file contains no readable header or data rows.");
            return;
        }

        foreach (var field in definitions.Where(definition => definition.Required))
        {
            if (!mapping.Has(field.Key))
            {
                report.AddError(ImportIssueCodes.MissingRequiredColumn,
                    $"No source column is mapped to '{field.DisplayName}'. Map it and validate again.");
            }
        }

        if (datasetKind == FinancialDatasetKind.TrialBalance)
        {
            if (!mapping.Has(TbFields.Debit) && !mapping.Has(TbFields.Credit) && !mapping.Has(TbFields.Balance))
            {
                report.AddError(ImportIssueCodes.MissingValueColumn,
                    "Map at least one of Debit, Credit or Balance; without a value column there is nothing to import.");
            }
        }
        else
        {
            if (!mapping.Has(GlFields.Debit) && !mapping.Has(GlFields.Credit) && !mapping.Has(GlFields.Amount))
            {
                report.AddError(ImportIssueCodes.MissingValueColumn,
                    "Map at least one of Debit, Credit or Amount (signed); without a value column there is nothing " +
                    "to import.");
            }

            if (!mapping.Has(GlFields.TransactionDate) && !mapping.Has(GlFields.PostingDate))
            {
                report.AddError(ImportIssueCodes.MissingRequiredColumn,
                    "Map a Transaction date column (or a Posting date column) so period boundaries can be checked.");
            }
        }

        foreach (var pair in mapping.Columns)
        {
            if (pair.Value >= structure.ColumnCount)
            {
                report.AddError(ImportIssueCodes.UnknownColumn,
                    $"The mapping for '{DisplayName(definitions, pair.Key)}' points at column {pair.Value + 1}, " +
                    $"but the file has {structure.ColumnCount} columns.");
            }
        }
    }

    public static string DescribeBlockingErrors(ImportValidationReport report)
    {
        var errors = report.Issues
            .Where(issue => issue.Severity == IssueSeverity.Error)
            .Take(3)
            .Select(issue => issue.RowNumber is { } row ? $"row {row}: {issue.Message}" : issue.Message)
            .ToList();

        var detail = errors.Count == 0
            ? "the file did not pass validation"
            : string.Join(" ", errors);
        return $"The file was not imported: {detail} " +
               $"({report.ErrorRowCount} row(s) with blocking findings; nothing was written).";
    }

    public static string DisplayName(IReadOnlyList<ImportFieldDefinition> definitions, string key) =>
        definitions.FirstOrDefault(definition => definition.Key == key)?.DisplayName ?? key;
}
