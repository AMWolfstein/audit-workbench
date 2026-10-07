using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Application.FinancialData.Imports;

/// <summary>
/// Dependency-free streaming reader for the two shapes clients actually send:
/// delimited text (.csv/.txt) and Excel workbooks (.xlsx). Both are recognised
/// from the bytes, never from the file name, and both are read row by row so a
/// 350k-line ledger is never materialised in memory.
///
/// The reader reports what it found (format, delimiter, encoding, sheets) but it
/// does not decide the header row or the meaning of a column: that belongs to
/// <see cref="StructureDetector"/> and the operator's mapping.
/// </summary>
public sealed class TabularFileReader : ITabularFileReader
{
    private const int SampleBytes = 512 * 1024;

    private const int DelimiterProbeLines = 30;

    private static readonly char[] Delimiters = { ',', ';', '\t', '|' };

    /// <summary>Excel's built-in date, time and datetime formats.</summary>
    private static readonly HashSet<int> BuiltInDateFormats = new()
    {
        14, 15, 16, 17, 18, 19, 20, 21, 22,
        27, 28, 29, 30, 31, 32, 33, 34, 35, 36,
        45, 46, 47,
        50, 51, 52, 53, 54, 55, 56, 57, 58,
    };

    public FileFormatInfo Detect(IFinancialDatasetSource source, CancellationToken cancellationToken = default)
    {
        using var stream = OpenSeekable(source);
        var header = new byte[8];
        var read = stream.Read(header, 0, header.Length);
        stream.Position = 0;

        if (read >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
            (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07))
        {
            return InspectWorkbook(stream);
        }

        if (read >= 8 && header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0)
        {
            // OLE compound file: the legacy binary .xls. Converting it would need a
            // parser we are not going to add for one file format.
            return Unsupported();
        }

        var sample = new byte[Math.Min(SampleBytes, (int)Math.Min(source.SizeBytes, SampleBytes))];
        var sampled = stream.Read(sample, 0, sample.Length);
        if (sampled == 0)
        {
            return Unsupported();
        }

        if (!LooksLikeText(sample, sampled))
        {
            return Unsupported();
        }

        var encoding = DetectEncoding(sample, sampled);
        var text = Decode(sample, sampled, encoding);
        var delimiter = DetectDelimiter(text);
        return new FileFormatInfo(FileFormatInfo.Csv, "text/csv", Array.Empty<string>(), delimiter,
            encoding.WebName, true);
    }

    public TabularPreview Preview(IFinancialDatasetSource source, int maxRows = 25,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var format = Detect(source, cancellationToken);
        if (!format.IsSupported)
        {
            return new TabularPreview(format, null, Array.Empty<TabularRow>(), false);
        }

        var wanted = Math.Max(1, Math.Min(maxRows, FinancialImportLimits.MaxPreviewRows * 20));
        var rows = new List<TabularRow>(wanted + 1);
        var truncated = false;
        foreach (var row in ReadRows(source, cancellationToken))
        {
            if (rows.Count == wanted)
            {
                truncated = true;
                break;
            }

            rows.Add(row);
        }

        var sheet = format.FileFormat == FileFormatInfo.Excel ? FirstSheet(format) : null;
        return new TabularPreview(format, sheet, rows, truncated);
    }

    public IEnumerable<TabularRow> ReadRows(IFinancialDatasetSource source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var format = Detect(source, cancellationToken);
        switch (format.FileFormat)
        {
            case FileFormatInfo.Csv:
                using (var stream = OpenSeekable(source))
                {
                    foreach (var row in ReadDelimitedRows(stream, format.Delimiter ?? ",", format.EncodingName,
                                 cancellationToken))
                    {
                        yield return row;
                    }
                }

                break;
            case FileFormatInfo.Excel:
                using (var stream = OpenSeekable(source))
                {
                    foreach (var row in ReadWorkbookRows(stream, cancellationToken))
                    {
                        yield return row;
                    }
                }

                break;
            default:
                throw new ValidationException(
                    "The uploaded file is not a readable CSV file or Excel workbook. Export the data again and retry.");
        }
    }

    // ------------------------------------------------------------------ source

    private static Stream OpenSeekable(IFinancialDatasetSource source)
    {
        var stream = source.OpenRead();
        if (stream.CanSeek)
        {
            return stream;
        }

        // The workbook reader needs random access; anything else is small enough to
        // stage in memory once instead of keeping the request stream open twice.
        var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        stream.Dispose();
        buffer.Position = 0;
        return buffer;
    }

    // ------------------------------------------------------------------ delimited text

    private static bool LooksLikeText(byte[] sample, int length)
    {
        var control = 0;
        for (var i = 0; i < length; i++)
        {
            var b = sample[i];
            if (b == 0)
            {
                return false;
            }

            if (b < 0x09 || (b > 0x0D && b < 0x20))
            {
                control++;
            }
        }

        return control <= length / 20;
    }

    private static Encoding DetectEncoding(byte[] sample, int length)
    {
        if (length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
        {
            return new UTF8Encoding(false);
        }

        if (length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(sample, 0, length);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            // Windows-125x exports (Arabic, Central European) are still common in
            // accounting systems; Latin-1 never fails and covers that byte range.
            return Encoding.Latin1;
        }
    }

    private static string Decode(byte[] sample, int length, Encoding encoding)
    {
        var offset = encoding.CodePage == Encoding.UTF8.CodePage && length >= 3 &&
                     sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF
            ? 3
            : 0;
        return encoding.GetString(sample, offset, Math.Max(0, length - offset));
    }

    /// <summary>
    /// Picks the delimiter the file is most consistent about: the candidate that
    /// splits the most probe lines into the same number of fields wins. Counting is
    /// quote aware so a semicolon inside a quoted description cannot win.
    /// </summary>
    internal static string DetectDelimiter(string text)
    {
        var lines = text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(DelimiterProbeLines)
            .ToList();
        if (lines.Count == 0)
        {
            return ",";
        }

        var best = ",";
        var bestScore = -1.0;
        foreach (var candidate in Delimiters)
        {
            var counts = lines.Select(line => CountOutsideQuotes(line, candidate)).ToList();
            var modal = counts.GroupBy(count => count).OrderByDescending(group => group.Count())
                .ThenByDescending(group => group.Key).First();
            if (modal.Key == 0)
            {
                continue;
            }

            var consistency = (double)modal.Count() / counts.Count;
            var score = consistency * 100 + Math.Min(modal.Key, 20);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate.ToString();
            }
        }

        return best;
    }

    private static int CountOutsideQuotes(string line, char candidate)
    {
        var count = 0;
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == candidate && !quoted)
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<TabularRow> ReadDelimitedRows(Stream stream, string delimiter, string encodingName,
        CancellationToken cancellationToken)
    {
        var separator = string.IsNullOrEmpty(delimiter) ? ',' : delimiter[0];
        var encoding = ResolveEncoding(encodingName);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true,
            bufferSize: 65536, leaveOpen: true);

        var buffer = new char[65536];
        var field = new StringBuilder();
        var cells = new List<string?>();
        var line = 1;
        var rowStartLine = 1;
        var rowDirty = false;
        var inQuotes = false;
        var quotePending = false;
        var skipLineFeed = false;
        long rowCount = 0;
        var first = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (first)
                {
                    first = false;
                    if (ch == '\uFEFF')
                    {
                        continue;
                    }
                }

                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (ch == '\n')
                    {
                        continue;
                    }
                }

                if (inQuotes)
                {
                    if (quotePending)
                    {
                        quotePending = false;
                        if (ch == '"')
                        {
                            field.Append('"');
                            continue;
                        }

                        inQuotes = false;
                    }
                    else if (ch == '"')
                    {
                        quotePending = true;
                        continue;
                    }
                    else
                    {
                        if (ch == '\n')
                        {
                            line++;
                        }

                        field.Append(ch);
                        continue;
                    }
                }

                if (ch == separator)
                {
                    cells.Add(Text(field));
                    rowDirty = true;
                    continue;
                }

                if (ch == '\r')
                {
                    EndRow(cells, field, ref rowStartLine, ref rowDirty, line, rowCount, out var row);
                    if (row is not null)
                    {
                        rowCount++;
                        CheckRow(row);
                        yield return row;
                    }

                    skipLineFeed = true;
                    line++;
                    continue;
                }

                if (ch == '\n')
                {
                    EndRow(cells, field, ref rowStartLine, ref rowDirty, line, rowCount, out var row);
                    if (row is not null)
                    {
                        rowCount++;
                        CheckRow(row);
                        yield return row;
                    }

                    line++;
                    continue;
                }

                if (ch == '"' && field.Length == 0)
                {
                    inQuotes = true;
                    rowDirty = true;
                    continue;
                }

                field.Append(ch);
                rowDirty = true;
            }
        }

        if (rowDirty || cells.Count > 0 || field.Length > 0)
        {
            EndRow(cells, field, ref rowStartLine, ref rowDirty, line, rowCount, out var row);
            if (row is not null)
            {
                rowCount++;
                CheckRow(row);
                yield return row;
            }
        }
    }

    private static void EndRow(List<string?> cells, StringBuilder field, ref int rowStartLine, ref bool rowDirty,
        int line, long rowCount, out TabularRow? row)
    {
        if (!rowDirty && cells.Count == 0 && field.Length == 0)
        {
            row = null;
            rowStartLine = line + 1;
            return;
        }

        if (rowCount >= FinancialImportLimits.MaxRows)
        {
            throw new ValidationException(
                $"The file contains more rows than the {FinancialImportLimits.MaxRows:N0} row import limit.");
        }

        cells.Add(Text(field));
        row = new TabularRow(rowStartLine, cells.ToArray());
        cells.Clear();
        rowStartLine = line + 1;
        rowDirty = false;
    }

    private static string? Text(StringBuilder field)
    {
        var value = field.ToString();
        field.Clear();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static Encoding ResolveEncoding(string name)
    {
        try
        {
            return Encoding.GetEncoding(string.IsNullOrWhiteSpace(name) ? "utf-8" : name);
        }
        catch (ArgumentException)
        {
            return new UTF8Encoding(false);
        }
    }

    // ------------------------------------------------------------------ workbook

    private static FileFormatInfo Unsupported() =>
        new(FileFormatInfo.Unsupported, "application/octet-stream", Array.Empty<string>(), null, "UTF-8", false);

    private static string? FirstSheet(FileFormatInfo format) =>
        format.Sheets.Count > 0 ? format.Sheets[0] : null;

    private static FileFormatInfo InspectWorkbook(Stream stream)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.GetEntry("[Content_Types].xml") is null || archive.GetEntry("xl/workbook.xml") is null)
            {
                return Unsupported();
            }

            var sheets = ReadSheetIndex(archive).Select(sheet => sheet.Name).ToList();
            if (sheets.Count == 0)
            {
                return Unsupported();
            }

            return new FileFormatInfo(FileFormatInfo.Excel,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", sheets, null, "UTF-8", true);
        }
        catch (InvalidDataException)
        {
            return Unsupported();
        }
        catch (XmlException)
        {
            return Unsupported();
        }
    }

    private static List<(string Name, string EntryPath)> ReadSheetIndex(ZipArchive archive)
    {
        var result = new List<(string Name, string EntryPath)>();
        var relationships = ReadRelationships(archive);
        using var stream = archive.GetEntry("xl/workbook.xml")!.Open();
        using var reader = XmlReader.Create(stream, ReaderSettings());
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet")
            {
                continue;
            }

            var name = reader.GetAttribute("name") ?? $"Sheet {result.Count + 1}";
            var id = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            var target = id is not null && relationships.TryGetValue(id, out var resolved) ? resolved : null;
            if (target is null)
            {
                continue;
            }

            if (archive.GetEntry(target) is not null)
            {
                result.Add((name, target));
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive archive)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry is null)
        {
            return result;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, ReaderSettings());
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship")
            {
                continue;
            }

            var id = reader.GetAttribute("Id");
            var target = reader.GetAttribute("Target");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            target = target!.Replace('\\', '/');
            var normalized = target.StartsWith('/') ? target.TrimStart('/')
                : target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target
                : $"xl/{target}";
            result[id!] = normalized;
        }

        return result;
    }

    private static IEnumerable<TabularRow> ReadWorkbookRows(Stream stream, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var sheets = ReadSheetIndex(archive);
        if (sheets.Count == 0)
        {
            throw new ValidationException("The workbook contains no readable sheet.");
        }

        var shared = ReadSharedStrings(archive);
        var dateStyles = ReadDateStyles(archive);
        using var sheetStream = archive.GetEntry(sheets[0].EntryPath)!.Open();
        using var reader = XmlReader.Create(sheetStream, ReaderSettings());
        long rowCount = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
            {
                continue;
            }

            rowCount++;
            if (rowCount > FinancialImportLimits.MaxRows)
            {
                throw new ValidationException(
                    $"The workbook contains more rows than the {FinancialImportLimits.MaxRows:N0} row import limit.");
            }

            yield return ReadWorkbookRow(reader, shared, dateStyles, rowCount);
        }
    }

    private static TabularRow ReadWorkbookRow(XmlReader row, List<string> shared, List<bool> dateStyles,
        long fallbackRowNumber)
    {
        var rowNumber = int.TryParse(row.GetAttribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var declared) && declared > 0
            ? declared
            : (int)Math.Min(fallbackRowNumber, int.MaxValue);
        var cells = new List<string?>();
        if (row.IsEmptyElement)
        {
            return new TabularRow(rowNumber, Array.Empty<string?>());
        }

        using var rowReader = row.ReadSubtree();
        rowReader.Read();
        while (rowReader.Read())
        {
            if (rowReader.NodeType != XmlNodeType.Element || rowReader.LocalName != "c")
            {
                continue;
            }

            var reference = rowReader.GetAttribute("r");
            var column = reference is null ? cells.Count : ColumnIndex(reference);
            if (column >= FinancialImportLimits.MaxColumns)
            {
                throw new ValidationException(
                    $"The workbook row {rowNumber} reaches column {reference}: a financial extract cannot have more " +
                    $"than {FinancialImportLimits.MaxColumns} columns.");
            }

            var style = int.TryParse(rowReader.GetAttribute("s"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var styleIndex)
                ? styleIndex
                : -1;
            var value = ReadCell(rowReader, shared, dateStyles, style);
            while (cells.Count < column)
            {
                cells.Add(null);
            }

            if (cells.Count == column)
            {
                cells.Add(value);
            }
            else
            {
                cells[column] = value;
            }
        }

        while (cells.Count > 0 && cells[^1] is null)
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return new TabularRow(rowNumber, cells.ToArray());
    }

    private static string? ReadCell(XmlReader cell, List<string> shared, List<bool> dateStyles, int styleIndex)
    {
        var type = cell.GetAttribute("t");
        var raw = (string?)null;
        var inline = (string?)null;
        if (!cell.IsEmptyElement)
        {
            using var cellReader = cell.ReadSubtree();
            cellReader.Read();
            while (cellReader.Read())
            {
                if (cellReader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (cellReader.LocalName == "v")
                {
                    raw = cellReader.ReadElementContentAsString();
                }
                else if (cellReader.LocalName == "is")
                {
                    inline = ReadInlineString(cellReader);
                }
            }
        }

        string? value = type switch
        {
            "s" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                    index >= 0 && index < shared.Count
                ? shared[index]
                : null,
            "inlineStr" => inline,
            "str" => raw,
            "b" => raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw,
            "d" => raw,
            _ => Numeric(raw, styleIndex, dateStyles),
        };

        if (value is not null && value.Length > FinancialImportLimits.MaxCellLength)
        {
            throw new ValidationException(
                $"A cell of the workbook holds {value.Length} characters, which usually means the file is not a " +
                "delimitable financial extract. Check the file and export it again.");
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// A numeric cell may really be a date: Excel stores dates as serial numbers and
    /// hides that behind a date number format. The raw number is kept whenever the
    /// style does not prove a date, so nothing is silently reinterpreted.
    /// </summary>
    private static string? Numeric(string? raw, int styleIndex, List<bool> dateStyles)
    {
        if (raw is null)
        {
            return null;
        }

        if (styleIndex < 0 || styleIndex >= dateStyles.Count || !dateStyles[styleIndex])
        {
            return raw;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) ||
            serial < 1 || serial > 2958465)
        {
            return raw;
        }

        try
        {
            var value = DateTime.FromOADate(serial);
            return value.TimeOfDay == TimeSpan.Zero
                ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return raw;
        }
    }

    private static string ReadInlineString(XmlReader element)
    {
        var text = new StringBuilder();
        using var reader = element.ReadSubtree();
        reader.Read();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                text.Append(reader.ReadElementContentAsString());
            }
        }

        return text.ToString();
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var result = new List<string>();
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return result;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, ReaderSettings());
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                result.Add(ReadInlineString(reader));
            }
        }

        return result;
    }

    /// <summary>Style index to "this number is a date" lookup, from xl/styles.xml.</summary>
    private static List<bool> ReadDateStyles(ZipArchive archive)
    {
        var result = new List<bool>();
        var entry = archive.GetEntry("xl/styles.xml");
        if (entry is null)
        {
            return result;
        }

        var custom = new Dictionary<int, string>();
        var cellFormats = new List<int>();
        using (var stream = entry.Open())
        using (var reader = XmlReader.Create(stream, ReaderSettings()))
        {
            var inCellFormats = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "numFmt")
                {
                    if (int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var id))
                    {
                        custom[id] = reader.GetAttribute("formatCode") ?? string.Empty;
                    }
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "cellXfs")
                {
                    inCellFormats = true;
                    if (reader.IsEmptyElement)
                    {
                        inCellFormats = false;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
                {
                    inCellFormats = false;
                }
                else if (inCellFormats && reader.NodeType == XmlNodeType.Element && reader.LocalName == "xf")
                {
                    cellFormats.Add(int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var id)
                        ? id
                        : 0);
                }
            }
        }

        foreach (var format in cellFormats)
        {
            result.Add(IsDateFormat(format, custom));
        }

        return result;
    }

    internal static bool IsDateFormat(int numFmtId, IReadOnlyDictionary<int, string> customFormats)
    {
        if (BuiltInDateFormats.Contains(numFmtId))
        {
            return true;
        }

        if (!customFormats.TryGetValue(numFmtId, out var code) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        // Strip literals, colours and conditions before looking for date tokens, so
        // "#,##0;[Red](#,##0)" and 'day' quoted in a label cannot win.
        var probe = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (ch == '\\' || ch == '_' || ch == '*')
            {
                i++;
                continue;
            }

            if (ch == '[')
            {
                while (i < code.Length && code[i] != ']')
                {
                    i++;
                }

                continue;
            }

            if (!quoted)
            {
                probe.Append(char.ToLowerInvariant(ch));
            }
        }

        var value = probe.ToString();
        return value.Contains('y') || value.Contains('d') ||
               (value.Contains('m') && (value.Contains('h') || value.Contains(':') || value.Contains('s')));
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                index = index * 26 + (ch - 'A' + 1);
            }
            else if (ch is >= 'a' and <= 'z')
            {
                index = index * 26 + (ch - 'a' + 1);
            }
            else
            {
                break;
            }
        }

        return index > 0 ? index - 1 : 0;
    }

    private static void CheckRow(TabularRow row)
    {
        if (row.Cells.Length > FinancialImportLimits.MaxColumns)
        {
            throw new ValidationException(
                $"Row {row.RowNumber} of the file holds {row.Cells.Length} columns; a financial extract cannot have " +
                $"more than {FinancialImportLimits.MaxColumns}. Check the delimiter and export the file again.");
        }

        for (var i = 0; i < row.Cells.Length; i++)
        {
            var value = row.Cells[i];
            if (value is not null && value.Length > FinancialImportLimits.MaxCellLength)
            {
                throw new ValidationException(
                    $"Row {row.RowNumber}, column {i + 1} holds {value.Length} characters, which usually means the " +
                    "file is not a delimited financial extract. Check the file and export it again.");
            }
        }
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        XmlResolver = null,
    };
}
