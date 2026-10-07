using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// The preserved source file of a TB/GL import. The bytes live in the managed
/// content-addressed attachment store; this row keeps the identity, the digest
/// and the detected structure so a client-supplied path is never trusted and the
/// import can be explained years later.
/// </summary>
public class FinancialUpload
{
    private FinancialUpload()
    {
    }

    public Guid UploadId { get; private set; }

    public Guid EngagementId { get; private set; }

    public string DatasetKind { get; private set; } = FinancialDatasetKind.TrialBalance;

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public string StorageLocation { get; private set; } = string.Empty;

    public string DetectedFormat { get; private set; } = "CSV";

    /// <summary>JSON of the detected structure (format, sheet, header row, columns, preview).</summary>
    public string DetectedStructure { get; private set; } = "{}";

    public string UploadedAtUtc { get; private set; } = string.Empty;

    public Guid UploadedBy { get; private set; }

    public static FinancialUpload Create(
        Guid uploadId,
        Guid engagementId,
        string datasetKind,
        string fileName,
        string contentType,
        long sizeBytes,
        string sha256,
        string storageLocation,
        string detectedFormat,
        string detectedStructure,
        string uploadedAtUtc,
        Guid uploadedBy)
    {
        if (!FinancialDatasetKind.IsValid(datasetKind))
        {
            throw new ValidationException("An uploaded financial file belongs to the trial balance or the general ledger.");
        }

        if (sizeBytes <= 0)
        {
            throw new ValidationException("The uploaded file is empty.");
        }

        var digest = (sha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (digest.Length != 64)
        {
            throw new ValidationException("The uploaded file digest is missing.");
        }

        if (string.IsNullOrWhiteSpace(storageLocation))
        {
            throw new ValidationException("The uploaded file was not stored.");
        }

        return new FinancialUpload
        {
            UploadId = uploadId,
            EngagementId = engagementId,
            DatasetKind = datasetKind,
            FileName = string.IsNullOrWhiteSpace(fileName) ? "upload" : fileName.Trim(),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
            SizeBytes = sizeBytes,
            Sha256 = digest,
            StorageLocation = storageLocation,
            DetectedFormat = detectedFormat,
            DetectedStructure = string.IsNullOrWhiteSpace(detectedStructure) ? "{}" : detectedStructure,
            UploadedAtUtc = uploadedAtUtc,
            UploadedBy = uploadedBy,
        };
    }
}
