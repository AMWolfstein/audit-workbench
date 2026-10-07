using AuditWorkbench.Domain.Storage;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.FinancialData;

namespace AuditWorkbench.Application.FinancialData.Imports;

/// <summary>
/// A preserved source file that can be read more than once: validation streams the
/// file, the import pass streams it again inside the database transaction, and
/// nothing is held in memory in between.
/// </summary>
public interface IFinancialDatasetSource
{
    string FileName { get; }

    long SizeBytes { get; }

    string Sha256 { get; }

    string ContentType { get; }

    Stream OpenRead();
}

/// <summary>The uploaded file is small enough to be held in memory (tests, small TBs).</summary>
public sealed class BytesDatasetSource : IFinancialDatasetSource
{
    private readonly byte[] _content;

    public BytesDatasetSource(string fileName, byte[] content, string contentType = "application/octet-stream")
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        FileName = string.IsNullOrWhiteSpace(fileName) ? "upload" : Path.GetFileName(fileName);
        ContentType = contentType;
        Sha256 = AuditWorkbench.Domain.FinancialImports.TransactionIdentity.Sha256Hex(
            System.Text.Encoding.Latin1.GetString(content));
        SizeBytes = content.Length;
    }

    public string FileName { get; }

    public long SizeBytes { get; }

    public string Sha256 { get; }

    public string ContentType { get; }

    public Stream OpenRead() => new MemoryStream(_content, writable: false);

    /// <summary>The raw bytes, used by tests and by the storage writer.</summary>
    public byte[] ToArray() => _content;
}

/// <summary>
/// The uploaded file lives in the managed attachment store; imports re-open the
/// stored bytes instead of trusting a client path.
/// </summary>
public sealed class StoredDatasetSource : IFinancialDatasetSource
{
    private readonly IFileStorage _storage;

    public StoredDatasetSource(IFileStorage storage, string storageLocation, string fileName, long sizeBytes,
        string sha256, string contentType)
    {
        _storage = storage;
        StorageLocation = storageLocation;
        FileName = fileName;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        ContentType = contentType;
    }

    public string StorageLocation { get; }

    public string FileName { get; }

    public long SizeBytes { get; }

    public string Sha256 { get; }

    public string ContentType { get; }

    public Stream OpenRead() => _storage.OpenReadAsync(StorageLocation).GetAwaiter().GetResult();
}

/// <summary>One physical row of the source file. Row numbers are 1-based file rows.</summary>
public sealed record TabularRow(int RowNumber, string?[] Cells)
{
    public string? Cell(int index) => index >= 0 && index < Cells.Length ? Cells[index] : null;

    public bool IsEmpty => Cells.All(string.IsNullOrWhiteSpace);
}

/// <summary>Format information discovered by sniffing the bytes, not by trusting the file name.</summary>
public sealed record FileFormatInfo(
    string FileFormat,
    string ContentType,
    IReadOnlyList<string> Sheets,
    string? Delimiter,
    string EncodingName,
    bool IsSupported)
{
    public const string Csv = "CSV";
    public const string Excel = "XLSX";
    public const string Unsupported = "UNSUPPORTED";
}

/// <summary>A bounded read of the beginning of a file used for structure detection and preview.</summary>
public sealed record TabularPreview(
    FileFormatInfo Format,
    string? SheetName,
    IReadOnlyList<TabularRow> Rows,
    bool IsTruncated);

/// <summary>
/// Streaming tabular reader. Implementations must not load the whole file into
/// memory: a 350k-row ledger is read row by row.
/// </summary>
public interface ITabularFileReader
{
    FileFormatInfo Detect(IFinancialDatasetSource source, CancellationToken cancellationToken = default);

    TabularPreview Preview(IFinancialDatasetSource source, int maxRows = 25,
        CancellationToken cancellationToken = default);

    IEnumerable<TabularRow> ReadRows(IFinancialDatasetSource source, CancellationToken cancellationToken = default);
}

/// <summary>Limits applied to every uploaded financial file (NFR: file handling).</summary>
public static class FinancialImportLimits
{
    public const long MaxFileSizeBytes = 64L * 1024 * 1024;

    public const int MaxPreviewRows = 25;

    public const int MaxColumns = 256;

    public const int MaxRows = 5_000_000;

    public const int InsertBatchSize = 500;

    public const int MaxCellLength = 2000;

    public static readonly IReadOnlyList<string> AllowedExtensions = new[] { ".csv", ".txt", ".xlsx" };

    public static void EnsureWithinLimits(string fileName, long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            throw new ValidationException("The uploaded file is empty.");
        }

        if (sizeBytes > MaxFileSizeBytes)
        {
            throw new ValidationException(
                $"The uploaded file is {sizeBytes / (1024 * 1024)} MB; the limit for an import is " +
                $"{MaxFileSizeBytes / (1024 * 1024)} MB. Split the file or export a smaller period.");
        }

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            throw new ValidationException(
                "Unsupported file type. Import a comma/semicolon separated .csv file or an Excel .xlsx workbook.");
        }
    }
}

/// <summary>Detected structure of one TB/GL source file, as shown on the mapping screen.</summary>
public sealed record ImportStructureInfo(
    string FileFormat,
    string? SheetName,
    string? Delimiter,
    int HeaderRowNumber,
    IReadOnlyList<string> Headers,
    IReadOnlyList<TabularRow> PreviewRows,
    int PreviewRowCount,
    int ExtraColumnCount,
    string? Warning)
{
    public int ColumnCount => Headers.Count;
}

/// <summary>Result of mapping + validation, ready for the operator's decision.</summary>
public sealed record ImportValidationOutcome(
    string DatasetKind,
    ImportStructureInfo Structure,
    Domain.FinancialImports.ImportColumnMapping Mapping,
    Domain.FinancialImports.ImportValidationReport Report,
    string FingerprintHash)
{
    public bool CanImport => !Report.HasErrors;

    public bool RequiresUnbalancedOverride =>
        Report.DatasetKind == FinancialDatasetKind.TrialBalance && !Report.IsBalanced;
}

/// <summary>Summary of a committed import, returned to the caller (and the UI).</summary>
public sealed record ImportCommitResult(
    Guid ImportId,
    string DatasetKind,
    int ImportNo,
    string Label,
    int RowCount,
    int ValidRowCount,
    int ErrorCount,
    int WarningCount,
    int OutOfPeriodCount,
    long TotalDebitMinor,
    long TotalCreditMinor,
    long DifferenceMinor,
    bool IsBalanced,
    bool IsActive,
    string ValidationStatus);
