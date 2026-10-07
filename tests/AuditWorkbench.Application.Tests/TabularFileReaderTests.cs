using System.IO.Compression;
using System.Text;
using AuditWorkbench.Application.FinancialData.Imports;
using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Application.Tests;

/// <summary>
/// The reader is the only place where untrusted client bytes become rows, so it is
/// tested against the shapes auditors actually receive: quoted separators, embedded
/// newlines, semicolon files with comma decimals, an Excel workbook whose dates are
/// serial numbers, and files that are not financial extracts at all.
/// </summary>
public sealed class TabularFileReaderTests
{
    private static readonly TabularFileReader Reader = new();

    private static BytesDatasetSource Csv(string content, string name = "tb.csv") =>
        new(name, Encoding.UTF8.GetBytes(content), "text/csv");

    [Fact]
    public void Csv_delimiter_is_sniffed_from_the_bytes_not_the_file_name()
    {
        var format = Reader.Detect(Csv("Account;Name;Debit\n1000;Cash;10\n", "ledger.xlsx"));

        Assert.True(format.IsSupported);
        Assert.Equal(FileFormatInfo.Csv, format.FileFormat);
        Assert.Equal(";", format.Delimiter);
    }

    [Fact]
    public void Quoted_fields_keep_separators_newlines_and_doubled_quotes()
    {
        var rows = Reader.ReadRows(Csv(
                "Code,Name,Note\n1000,\"Cash, petty\",\"line 1\nline 2\"\n2000,\"Say \"\"hi\"\"\",\n"))
            .ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal("Cash, petty", rows[1].Cell(1));
        Assert.Equal("line 1\nline 2", rows[1].Cell(2));
        Assert.Equal("Say \"hi\"", rows[2].Cell(1));
        Assert.Null(rows[2].Cell(2));
    }

    [Fact]
    public void Row_numbers_are_the_line_numbers_an_accountant_sees_in_excel()
    {
        var rows = Reader.ReadRows(Csv("Title\n\nAccount,Debit\n1000,10\n")).ToList();

        Assert.Equal(new[] { 1, 3, 4 }, rows.Select(r => r.RowNumber));
        Assert.Equal("Account", rows[1].Cell(0));
    }

    [Fact]
    public void Rows_stream_through_in_order_without_losing_the_last_one()
    {
        var row = "1000,Cash,10.00,0.00\n";
        var source = Csv("Account,Name,Debit,Credit\n" + string.Concat(Enumerable.Repeat(row, 50)));

        Assert.Equal(51, Reader.ReadRows(source).Count());
    }

    [Fact]
    public void A_binary_file_is_reported_as_unsupported_instead_of_producing_garbage_rows()
    {
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00 };
        var format = Reader.Detect(new BytesDatasetSource("ledger.xls", ole, "application/vnd.ms-excel"));

        Assert.False(format.IsSupported);
        Assert.Equal(FileFormatInfo.Unsupported, format.FileFormat);
    }

    [Fact]
    public void An_absurdly_long_cell_is_rejected_rather_than_stored()
    {
        var source = Csv("Account,Name\n1000," + new string('x', FinancialImportLimits.MaxCellLength + 1) + "\n");

        var error = Assert.Throws<ValidationException>(() => Reader.ReadRows(source).ToList());
        Assert.Contains("characters", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_workbook_is_read_with_shared_strings_missing_cells_and_real_dates()
    {
        var source = Workbook();

        var format = Reader.Detect(source);
        Assert.True(format.IsSupported);
        Assert.Equal(FileFormatInfo.Excel, format.FileFormat);
        Assert.Equal(new[] { "Ledger" }, format.Sheets);

        var rows = Reader.ReadRows(source).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Account", rows[0].Cell(0));
        Assert.Equal("Amount", rows[0].Cell(1));
        // A numeric cell stays numeric text: the parser, not the reader, decides scale.
        Assert.Equal("1234.5", rows[1].Cell(1));
        // A date-formatted serial number is shown the way the account system prints it.
        Assert.Equal("2026-01-01", rows[1].Cell(2));
        // A cell that starts in column C leaves A and B empty instead of shifting left.
        Assert.Null(rows[2].Cell(0));
        Assert.Null(rows[2].Cell(1));
        Assert.Equal("Trailing", rows[2].Cell(2));
    }

    [Fact]
    public void Preview_is_bounded_and_says_when_it_stopped_early()
    {
        var source = Csv("Account,Debit\n1000,1\n2000,2\n3000,3\n");

        var preview = Reader.Preview(source, maxRows: 2);

        Assert.Equal(2, preview.Rows.Count);
        Assert.True(preview.IsTruncated);
        Assert.Equal(FileFormatInfo.Csv, preview.Format.FileFormat);
    }

    /// <summary>A minimal OOXML workbook written by hand: one sheet, two shared strings.</summary>
    private static BytesDatasetSource Workbook()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(archive, "[Content_Types].xml",
                """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>""");
            Entry(archive, "xl/workbook.xml",
                """<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" """ +
                """xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""" +
                """<sheets><sheet name="Ledger" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Entry(archive, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""" +
                """<Relationship Id="rId1" Target="worksheets/sheet1.xml" """ +
                """Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/></Relationships>""");
            Entry(archive, "xl/sharedStrings.xml",
                """<?xml version="1.0"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
                """<si><t>Account</t></si><si><t>Posting date</t></si></sst>""");
            // Style 1 is the built-in-ish date format; style 0 is general.
            Entry(archive, "xl/styles.xml",
                """<?xml version="1.0"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
                """<numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy\-mm\-dd"/></numFmts>""" +
                """<cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="164"/></cellXfs></styleSheet>""");
            Entry(archive, "xl/worksheets/sheet1.xml",
                """<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""" +
                """<sheetData>""" +
                """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="inlineStr"><is><t>Amount</t></is></c>""" +
                """<c r="C1" t="s"><v>1</v></c></row>""" +
                """<row r="2"><c r="A2" t="s"><v>0</v></c><c r="B2"><v>1234.5</v></c>""" +
                """<c r="C2" s="1"><v>46023</v></c></row>""" +
                """<row r="3"><c r="C3" t="inlineStr"><is><t>Trailing</t></is></c></row>""" +
                """</sheetData></worksheet>""");
        }

        return new BytesDatasetSource("ledger.xlsx", buffer.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    private static void Entry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
