using System.Text.Json;
using AuditWorkbench.Domain.FinancialImports;

namespace AuditWorkbench.Application.FinancialData.Imports;

/// <summary>
/// Finds the header row and the usable columns of a previewed file. Client files
/// frequently carry a title row, a company name, blank rows and totals above the
/// real header, so the header is detected instead of assumed.
/// </summary>
public static class StructureDetector
{
    public static ImportStructureInfo Detect(
        TabularPreview preview,
        IReadOnlyList<ImportFieldDefinition> definitions,
        string? warning = null)
    {
        if (preview.Rows.Count == 0)
        {
            return new ImportStructureInfo(preview.Format.FileFormat, preview.SheetName, preview.Format.Delimiter,
                0, Array.Empty<string>(), Array.Empty<TabularRow>(), 0, 0,
                "The file contains no readable rows.");
        }

        var headerIndex = FindHeaderRow(preview.Rows, definitions);
        if (headerIndex < 0)
        {
            // No recognizable header: the operator still gets a preview and an
            // explicit warning, and can map columns by position.
            var fallbackWidth = preview.Rows.Max(row => row.Cells.Length);
            return new ImportStructureInfo(
                preview.Format.FileFormat,
                preview.SheetName,
                preview.Format.Delimiter,
                0,
                Enumerable.Range(1, fallbackWidth).Select(i => $"Column {i}").ToList(),
                preview.Rows,
                preview.Rows.Count,
                0,
                "No header row could be identified. Map columns by position and check the preview carefully.");
        }

        var headerRow = preview.Rows[headerIndex];
        var headers = BuildHeaders(headerRow);
        var previewRows = preview.Rows.Skip(headerIndex + 1).ToList();
        var suggested = ImportColumnMapping.Suggest(definitions, headers);
        var extraColumns = Math.Max(0, headers.Count - suggested.MappedFields.Count);

        return new ImportStructureInfo(
            preview.Format.FileFormat,
            preview.SheetName,
            preview.Format.Delimiter,
            headerRow.RowNumber,
            headers,
            previewRows,
            previewRows.Count,
            extraColumns,
            warning);
    }

    /// <summary>Duplicate header names are made unique so the mapping stays unambiguous.</summary>
    private static IReadOnlyList<string> BuildHeaders(TabularRow headerRow)
    {
        var headers = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headerRow.Cells.Length; index++)
        {
            var header = (headerRow.Cell(index) ?? string.Empty).Trim();
            if (header.Length == 0)
            {
                header = $"Column {index + 1}";
            }

            if (seen.TryGetValue(header, out var count))
            {
                seen[header] = count + 1;
                header = $"{header} ({count + 1})";
            }
            else
            {
                seen[header] = 1;
            }

            headers.Add(header);
        }

        return headers;
    }

    /// <summary>
    /// The header row is the row within the first rows whose cells best match the
    /// field aliases; ties are broken by the earliest row.
    /// </summary>
    private static int FindHeaderRow(IReadOnlyList<TabularRow> rows, IReadOnlyList<ImportFieldDefinition> definitions)
    {
        var limit = Math.Min(rows.Count, 20);
        var bestIndex = -1;
        var bestScore = 0;
        var bestWidth = 0;

        for (var index = 0; index < limit; index++)
        {
            var row = rows[index];
            var score = 0;
            foreach (var cell in row.Cells)
            {
                var value = ImportColumnMapping.NormalizeHeader(cell);
                if (value.Length == 0)
                {
                    continue;
                }

                if (definitions.Any(definition =>
                        definition.Aliases.Any(alias => ImportColumnMapping.NormalizeHeader(alias) == value)))
                {
                    score += 10;
                }
                else if (!ImportValueParser.LooksNumeric(cell) && !ImportValueParser.LooksLikeDate(cell))
                {
                    score += 1;
                }
            }

            var width = row.Cells.Count(cell => !string.IsNullOrWhiteSpace(cell));
            if (score > bestScore || (score == bestScore && score > 0 && width > bestWidth))
            {
                bestScore = score;
                bestIndex = index;
                bestWidth = width;
            }
        }

        // Require a plausible header: at least one alias match plus another text cell.
        return bestScore >= 11 ? bestIndex : bestIndex >= 0 && bestScore >= 2 ? bestIndex : -1;
    }

    /// <summary>
    /// Serializes the detected structure for the upload record, so the mapping
    /// screen and the provenance keep what the detector actually saw.
    /// </summary>
    public static string ToJson(ImportStructureInfo structure, ImportColumnMapping mapping)
    {
        var payload = new Dictionary<string, object?>
        {
            ["format"] = structure.FileFormat,
            ["sheet"] = structure.SheetName,
            ["delimiter"] = structure.Delimiter,
            ["header_row"] = structure.HeaderRowNumber,
            ["columns"] = StructureDetectorExtensions.Columns(structure),
            ["suggested_mapping"] = JsonSerializer.Deserialize<Dictionary<string, int>>(mapping.ToJson()),
            ["warning"] = structure.Warning,
        };

        return JsonSerializer.Serialize(payload);
    }
}

internal static class StructureDetectorExtensions
{
    public static IReadOnlyList<object> Columns(this ImportStructureInfo structure) => structure.Headers
        .Select((header, index) => (object)new Dictionary<string, object?> { ["index"] = index, ["name"] = header })
        .ToList();
}
