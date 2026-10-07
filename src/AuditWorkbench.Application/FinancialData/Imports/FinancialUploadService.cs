using System.Text;
using System.Text.Json;
using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Domain.Storage;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.FinancialData.Imports;

/// <summary>Persisted source-file provenance of one upload.</summary>
public sealed record FinancialUploadRecord(
    Guid UploadId,
    Guid EngagementId,
    string DatasetKind,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string StorageLocation,
    string DetectedFormat,
    string DetectedStructure,
    string UploadedAtUtc,
    Guid UploadedBy);

/// <summary>An upload that is ready to be mapped, validated and imported.</summary>
public sealed record StoredUpload(
    FinancialUploadRecord Record,
    IFinancialDatasetSource Source,
    ImportStructureInfo Structure);

/// <summary>Upload summary shown on the mapping screen (headers, preview, suggestion).</summary>
public sealed record FinancialUploadSummary(
    FinancialUploadRecord Record,
    ImportStructureInfo Structure,
    ImportColumnMapping SuggestedMapping,
    IReadOnlyList<string> PriorImportsWithSameFile,
    bool IsRepeatSource);

/// <summary>
/// Handles the uploaded TB/GL source file: validates type/size, sniffs the real
/// format (never trusting the file name), stores the bytes in the managed
/// attachment store and records the provenance row.
/// </summary>
public sealed class FinancialUploadService
{
    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly FinancialPeriodService _periods;
    private readonly ITabularFileReader _reader;
    private readonly IFileStorage _storage;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;

    public FinancialUploadService(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        FinancialPeriodService periods,
        ITabularFileReader reader,
        IFileStorage storage,
        IClock clock,
        ICurrentActor actor)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _periods = periods;
        _reader = reader;
        _storage = storage;
        _clock = clock;
        _actor = actor;
    }

    public async Task<FinancialUploadSummary> UploadAsync(
        Guid engagementId,
        string datasetKind,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (!FinancialDatasetKind.IsValid(datasetKind))
        {
            throw new ValidationException("A financial import is either a trial balance (TB) or a general ledger (GL).");
        }

        await _periods.RequireOpenForImportsAsync(engagementId, FinancialDataPermissions.Import, cancellationToken)
            .ConfigureAwait(false);

        var safeName = SanitizeFileName(fileName);
        var bytes = await ReadAllAsync(content, cancellationToken).ConfigureAwait(false);
        FinancialImportLimits.EnsureWithinLimits(safeName, bytes.LongLength);

        var probe = new BytesDatasetSource(safeName, bytes,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        var format = _reader.Detect(probe, cancellationToken);
        if (!format.IsSupported)
        {
            throw new ValidationException(
                $"'{safeName}' is not a readable CSV or Excel (.xlsx) file. Export the data as CSV or XLSX and try again.");
        }

        var definitions = GlFields.For(datasetKind);
        var preview = _reader.Preview(probe, FinancialImportLimits.MaxPreviewRows, cancellationToken);
        var structure = StructureDetector.Detect(preview, definitions);
        var structureJson = JsonSerializer.Serialize(StoredStructureDto.From(structure));
        var now = IClock.Format(_clock.UtcNow);

        Guid uploadId = Guid.Empty;
        await _unitOfWork.ExecuteAsync(async token =>
        {
            StoredFile stored;
            await using (var uploadStream = new MemoryStream(bytes, writable: false))
            {
                stored = await _storage.PutAsync(engagementId, uploadStream, probe.ContentType, token)
                    .ConfigureAwait(false);
            }

            var upload = FinancialUpload.Create(
                Guid.NewGuid(), engagementId, datasetKind, safeName, probe.ContentType, probe.SizeBytes, probe.Sha256,
                stored.StorageLocation, format.FileFormat, structureJson, now, _actor.UserId);
            _dbContext.FinancialUploads.Add(upload);
            uploadId = upload.UploadId;

            await _auditTrail.AppendAsync(
                    AuditEventType.FinancialUploadReceived,
                    AuditEntityType.FinancialUpload,
                    upload.UploadId.ToString("D"),
                    $"{FinancialDatasetKind.DisplayName(datasetKind)} source file '{safeName}' received " +
                    $"({probe.SizeBytes} bytes, sha256 {probe.Sha256[..12]}...).",
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", datasetKind)
                        .With("file_name", safeName)
                        .With("file_size", probe.SizeBytes)
                        .With("file_sha256", probe.Sha256)
                        .With("format", format.FileFormat)
                        .With("header_row", structure.HeaderRowNumber),
                    cancellationToken: token)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return new FinancialUploadSummary(
            await LoadRecordAsync(engagementId, uploadId, cancellationToken).ConfigureAwait(false),
            structure,
            ImportColumnMapping.Suggest(definitions, structure.Headers),
            await PriorImportsWithSameFileAsync(engagementId, datasetKind, probe.Sha256, cancellationToken)
                .ConfigureAwait(false),
            false);
    }

    /// <summary>
    /// Authorized load of a stored upload, including its detected structure. The
    /// upload id is verified against the engagement: an id from another
    /// engagement is never resolvable.
    /// </summary>
    public async Task<StoredUpload> LoadAsync(Guid engagementId, string datasetKind, Guid uploadId,
        bool requireWritable = true, CancellationToken cancellationToken = default)
    {
        if (requireWritable)
        {
            await _periods.RequireOpenForImportsAsync(engagementId, FinancialDataPermissions.Import, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _periods.RequireOpenForImportsAsync(engagementId, FinancialDataPermissions.View, cancellationToken)
                .ConfigureAwait(false);
        }

        var record = await LoadRecordAsync(engagementId, uploadId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(datasetKind, record.DatasetKind, StringComparison.Ordinal))
        {
            throw new NotFoundException("That source file was uploaded for another dataset.");
        }

        var dto = JsonSerializer.Deserialize<StoredStructureDto>(record.DetectedStructure)
                  ?? throw new NotFoundException("The detected structure of that upload is missing.");
        var source = new StoredDatasetSource(_storage, record.StorageLocation, record.FileName, record.SizeBytes,
            record.Sha256, record.ContentType);
        return new StoredUpload(record, source, StoredStructureDto.ToStructure(dto));
    }

    public async Task<FinancialUploadSummary> GetSummaryAsync(Guid engagementId, string datasetKind, Guid uploadId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadRecordAsync(engagementId, uploadId, cancellationToken).ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<StoredStructureDto>(record.DetectedStructure)
                  ?? new StoredStructureDto();
        var structure = StoredStructureDto.ToStructure(dto);
        var definitions = GlFields.For(datasetKind);
        var priorImports = await PriorImportsWithSameFileAsync(engagementId, datasetKind, record.Sha256,
            cancellationToken).ConfigureAwait(false);
        return new FinancialUploadSummary(record, structure,
            ImportColumnMapping.Suggest(definitions, structure.Headers), priorImports, priorImports.Count > 0);
    }

    public async Task<FinancialUploadRecord> LoadRecordAsync(Guid engagementId, Guid uploadId,
        CancellationToken cancellationToken = default)
    {
        var upload = await _dbContext.FinancialUploads.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UploadId == uploadId && u.EngagementId == engagementId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("That uploaded source file does not exist in this engagement.");

        return new FinancialUploadRecord(upload.UploadId, upload.EngagementId, upload.DatasetKind, upload.FileName,
            upload.ContentType, upload.SizeBytes, upload.Sha256, upload.StorageLocation, upload.DetectedFormat,
            upload.DetectedStructure, upload.UploadedAtUtc, upload.UploadedBy);
    }

    /// <summary>Committed imports that came from exactly this file content.</summary>
    internal async Task<IReadOnlyList<string>> PriorImportsWithSameFileAsync(Guid engagementId, string datasetKind,
        string sha256, CancellationToken cancellationToken) =>
        await _dbContext.DatasetImports.AsNoTracking()
            .Where(i => i.EngagementId == engagementId && i.DatasetKind == datasetKind &&
                        i.SourceFileSha256 == sha256)
            .OrderBy(i => i.ImportNo)
            .Select(i => i.SnapshotLabel)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            return "upload";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsControl(character) || character is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : character);
        }

        var safe = builder.ToString();
        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > FinancialImportLimits.MaxFileSizeBytes)
            {
                throw new ValidationException(
                    $"The uploaded file is larger than the {FinancialImportLimits.MaxFileSizeBytes / (1024 * 1024)} MB " +
                    "limit for an import.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }
}

/// <summary>Serializable shape of the detected structure stored on the upload row.</summary>
public sealed class StoredStructureDto
{
    public string Format { get; set; } = FileFormatInfo.Csv;

    public string? Sheet { get; set; }

    public string? Delimiter { get; set; }

    public int HeaderRow { get; set; }

    public List<string> Headers { get; set; } = new();

    public List<StoredPreviewRowDto> Preview { get; set; } = new();

    public string? Warning { get; set; }

    public static StoredStructureDto From(ImportStructureInfo structure) => new()
    {
        Format = structure.FileFormat,
        Sheet = structure.SheetName,
        Delimiter = structure.Delimiter,
        HeaderRow = structure.HeaderRowNumber,
        Headers = structure.Headers.ToList(),
        Preview = structure.PreviewRows.Select(row => new StoredPreviewRowDto
        {
            Row = row.RowNumber,
            Cells = row.Cells.Select(cell => cell).ToList(),
        }).ToList(),
        Warning = structure.Warning,
    };

    public static ImportStructureInfo ToStructure(StoredStructureDto dto) => new(
        dto.Format,
        dto.Sheet,
        dto.Delimiter,
        dto.HeaderRow,
        dto.Headers,
        dto.Preview.Select(row => new TabularRow(row.Row, row.Cells.ToArray())).ToList(),
        dto.Preview.Count,
        0,
        dto.Warning);
}

public sealed class StoredPreviewRowDto
{
    public int Row { get; set; }

    public List<string?> Cells { get; set; } = new();
}
