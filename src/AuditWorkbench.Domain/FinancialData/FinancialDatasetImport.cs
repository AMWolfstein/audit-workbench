using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// One imported version of a client dataset (a TB version or a GL snapshot):
/// the unit of versioning, provenance and immutability.
/// <para>
/// The row is created DRAFT (before any line is written), validated, committed as
/// IMPORTED and may then be FINALIZED. It is never overwritten or re-pointed: a
/// later client file creates the next version and the earlier one becomes
/// SUPERSEDED but stays fully readable.
/// </para>
/// </summary>
public class FinancialDatasetImport
{
    private FinancialDatasetImport()
    {
    }

    public Guid ImportId { get; private set; }

    public Guid EngagementId { get; private set; }

    public Guid FinancialPeriodId { get; private set; }

    public string DatasetKind { get; private set; } = FinancialDatasetKind.TrialBalance;

    public int ImportNo { get; private set; }

    /// <summary>Operator-facing label such as "TB Import #2" or "GL Snapshot #1".</summary>
    public string SnapshotLabel { get; private set; } = string.Empty;

    public string Status { get; private set; } = DatasetImportStatus.Draft;

    public bool IsActive { get; private set; }

    public Guid? SupersededByImportId { get; private set; }

    /// <summary>Set when the operator deliberately imported a repeated source file as a new version.</summary>
    public Guid? RepeatOfImportId { get; private set; }

    public Guid SourceUploadId { get; private set; }

    public string SourceFileName { get; private set; } = string.Empty;

    public string SourceFileSha256 { get; private set; } = string.Empty;

    public long SourceFileSizeBytes { get; private set; }

    public int? SourceHeaderRowNo { get; private set; }

    public string? SourceSheetName { get; private set; }

    /// <summary>Last date the source dataset covers; for a GL snapshot this is the "through" date.</summary>
    public string? CoverageThroughDate { get; private set; }

    public string ColumnMappingJson { get; private set; } = "{}";

    /// <summary>Fingerprint of kind + period + source hash + mapping + measured totals.</summary>
    public string FingerprintHash { get; private set; } = string.Empty;

    public string ValidationJson { get; private set; } = "{}";

    public string ValidationStatus { get; private set; } = DatasetValidationStatus.NotRun;

    public int RowCount { get; private set; }

    public int ValidRowCount { get; private set; }

    public int WarningCount { get; private set; }

    public int ErrorCount { get; private set; }

    public int OutOfPeriodCount { get; private set; }

    public long TotalDebitMinor { get; private set; }

    public long TotalCreditMinor { get; private set; }

    public long DifferenceMinor { get; private set; }

    public bool IsBalanced { get; private set; }

    /// <summary>True when an unbalanced TB was imported after an explicit operator override.</summary>
    public bool UnbalancedOverride { get; private set; }

    public string ImportedAtUtc { get; private set; } = string.Empty;

    public Guid ImportedBy { get; private set; }

    public string? FinalizedAtUtc { get; private set; }

    public Guid? FinalizedBy { get; private set; }

    public int RowVersion { get; private set; } = 1;

    public bool IsFinalized => Status == DatasetImportStatus.Finalized;

    public bool HasRows => DatasetImportStatus.HasRows(Status);

    public static string Label(string datasetKind, int importNo) =>
        $"{FinancialDatasetKind.VersionNoun(datasetKind)} #{importNo}";

    public static FinancialDatasetImport Create(
        Guid importId,
        Guid engagementId,
        Guid financialPeriodId,
        string datasetKind,
        int importNo,
        Guid sourceUploadId,
        string sourceFileName,
        string sourceFileSha256,
        long sourceFileSizeBytes,
        int? sourceHeaderRowNo,
        string? sourceSheetName,
        string columnMappingJson,
        string fingerprintHash,
        string importedAtUtc,
        Guid importedBy,
        Guid? repeatOfImportId = null,
        string? coverageThroughDate = null)
    {
        if (!FinancialDatasetKind.IsValid(datasetKind))
        {
            throw new ValidationException("A dataset import must be a trial balance (TB) or general ledger (GL) import.");
        }

        if (importNo <= 0)
        {
            throw new ValidationException("The import number must be positive.");
        }

        if ((sourceFileSha256 ?? string.Empty).Length != 64)
        {
            throw new ValidationException("The source file digest is missing.");
        }

        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            throw new ValidationException("The source file name is required for provenance.");
        }

        return new FinancialDatasetImport
        {
            ImportId = importId,
            EngagementId = engagementId,
            FinancialPeriodId = financialPeriodId,
            DatasetKind = datasetKind,
            ImportNo = importNo,
            SnapshotLabel = Label(datasetKind, importNo),
            Status = DatasetImportStatus.Draft,
            IsActive = false,
            SourceUploadId = sourceUploadId,
            SourceFileName = sourceFileName.Trim(),
            SourceFileSha256 = (sourceFileSha256 ?? string.Empty).ToLowerInvariant(),
            SourceFileSizeBytes = sourceFileSizeBytes,
            SourceHeaderRowNo = sourceHeaderRowNo,
            SourceSheetName = sourceSheetName,
            CoverageThroughDate = coverageThroughDate,
            ColumnMappingJson = string.IsNullOrWhiteSpace(columnMappingJson) ? "{}" : columnMappingJson,
            FingerprintHash = fingerprintHash,
            ImportedAtUtc = importedAtUtc,
            ImportedBy = importedBy,
            RepeatOfImportId = repeatOfImportId,
            RowVersion = 1,
        };
    }

    /// <summary>Records the validation report produced before the rows were written.</summary>
    public void MarkValidated(
        string validationStatus,
        string validationJson,
        int rowCount,
        int validRowCount,
        int warningCount,
        int errorCount,
        long totalDebitMinor,
        long totalCreditMinor,
        long differenceMinor,
        bool isBalanced,
        bool unbalancedOverride)
    {
        if (HasRows)
        {
            throw new ValidationException("The validation result of a committed import cannot be rewritten.");
        }

        if (!new[] { DatasetValidationStatus.Valid, DatasetValidationStatus.ValidWithWarnings, DatasetValidationStatus.Rejected }
                .Contains(validationStatus))
        {
            throw new ValidationException($"'{validationStatus}' is not a supported validation result.");
        }

        ValidationStatus = validationStatus;
        ValidationJson = validationJson ?? "{}";
        RowCount = rowCount;
        ValidRowCount = validRowCount;
        WarningCount = warningCount;
        ErrorCount = errorCount;
        TotalDebitMinor = totalDebitMinor;
        TotalCreditMinor = totalCreditMinor;
        DifferenceMinor = differenceMinor;
        IsBalanced = isBalanced;
        UnbalancedOverride = unbalancedOverride;
        Status = DatasetImportStatus.Validated;
        RowVersion++;
    }

    /// <summary>
    /// Commits the version. <paramref name="activate"/> also makes it the active
    /// version for its dataset kind (the caller must have deactivated the previous
    /// active version in the same transaction).
    /// </summary>
    public void MarkImported(
        int rowCount,
        int validRowCount,
        int warningCount,
        int errorCount,
        int outOfPeriodCount,
        long totalDebitMinor,
        long totalCreditMinor,
        long differenceMinor,
        bool isBalanced,
        bool unbalancedOverride,
        string? coverageThroughDate,
        bool activate)
    {
        if (HasRows)
        {
            throw new ValidationException("This dataset version is already committed.");
        }

        DatasetImportStatusTransition(Status, DatasetImportStatus.Imported);
        Status = DatasetImportStatus.Imported;
        RowCount = rowCount;
        ValidRowCount = validRowCount;
        WarningCount = warningCount;
        ErrorCount = errorCount;
        OutOfPeriodCount = outOfPeriodCount;
        TotalDebitMinor = totalDebitMinor;
        TotalCreditMinor = totalCreditMinor;
        DifferenceMinor = differenceMinor;
        IsBalanced = isBalanced;
        UnbalancedOverride = unbalancedOverride;
        ValidationStatus = errorCount > 0 ? DatasetValidationStatus.Rejected : ValidationStatus;
        if (CoverageThroughDate is null || coverageThroughDate is not null)
        {
            CoverageThroughDate = coverageThroughDate ?? CoverageThroughDate;
        }

        if (activate)
        {
            IsActive = true;
        }

        RowVersion++;
    }

    public void Activate()
    {
        if (!DatasetImportStatus.HasRows(Status))
        {
            throw new ValidationException("Only a committed import can become the active version.");
        }

        if (FinancialDatasetKind.IsValid(DatasetKind) && Status == DatasetImportStatus.Superseded)
        {
            throw new ValidationException("A superseded version cannot be reactivated.");
        }

        IsActive = true;
        RowVersion++;
    }

    public void Deactivate()
    {
        if (IsActive)
        {
            IsActive = false;
            RowVersion++;
        }
    }

    /// <summary>Marks this version as replaced by a later version of the same dataset.</summary>
    public void MarkSuperseded(Guid replacementImportId, int replacementImportNo)
    {
        if (Status == DatasetImportStatus.Superseded)
        {
            throw new ValidationException("This dataset version was already superseded.");
        }

        if (Status is not (DatasetImportStatus.Imported or DatasetImportStatus.Finalized))
        {
            throw new ValidationException("Only a committed version can be superseded by a later version.");
        }

        if (replacementImportNo <= ImportNo)
        {
            throw new ValidationException("A version can only be superseded by a later version of the same dataset.");
        }

        Status = DatasetImportStatus.Superseded;
        SupersededByImportId = replacementImportId;
        IsActive = false;
        RowVersion++;
    }

    public void MarkFinalized(string finalizedAtUtc, Guid finalizedBy)
    {
        if (Status == DatasetImportStatus.Finalized)
        {
            throw new ValidationException("This dataset version is already finalized.");
        }

        if (Status is not (DatasetImportStatus.Imported or DatasetImportStatus.Validated))
        {
            throw new ValidationException("Only a committed version can be finalized.");
        }

        if (Status == DatasetImportStatus.Validated)
        {
            throw new ValidationException(
                "Finalize the import after its rows have been committed; validation alone is not a dataset.");
        }

        if (!IsBalanced && DatasetKind == FinancialDatasetKind.TrialBalance)
        {
            throw new ValidationException(
                "An unbalanced trial balance cannot be finalized. Import a corrected file first.");
        }

        if (ErrorCount > 0)
        {
            throw new ValidationException("An import with blocking validation errors cannot be finalized.");
        }

        DatasetImportStatusTransition(Status, DatasetImportStatus.Finalized);
        Status = DatasetImportStatus.Finalized;
        FinalizedAtUtc = finalizedAtUtc;
        FinalizedBy = finalizedBy;
        RowVersion++;
    }

    public void EnsureExpectedVersion(int? expectedRowVersion)
    {
        if (expectedRowVersion is not null && expectedRowVersion != RowVersion)
        {
            throw new ConcurrencyException(
                "This import changed in another window. Reload the page and try again.");
        }
    }

    private static void DatasetImportStatusTransition(string from, string to)
    {
        if (!DatasetImportStatus.CanTransition(from, to))
        {
            throw new ValidationException($"An import cannot move from {from} to {to}.");
        }
    }
}
