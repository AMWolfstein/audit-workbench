using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

public static class ImportJobStatus
{
    public const string Queued = "QUEUED";
    public const string Processing = "PROCESSING";
    public const string Validating = "VALIDATING";
    public const string Importing = "IMPORTING";
    public const string Reconciling = "RECONCILING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Queued, Processing, Validating, Importing, Reconciling, Completed, Failed, Cancelled,
    };

    public static bool IsTerminal(string status) =>
        status is Completed or Failed or Cancelled;

    /// <summary>Operator-facing stage name shown while an import is running.</summary>
    public static string DisplayName(string status) => status switch
    {
        Queued => "Queued",
        Processing => "Reading the source file",
        Validating => "Validating rows",
        Importing => "Writing rows",
        Reconciling => "Reconciling",
        Completed => "Completed",
        Failed => "Failed",
        Cancelled => "Cancelled",
        _ => status,
    };

    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Queued, Queued) => true,
        (Queued, Processing) => true,
        (Queued, Cancelled) => true,
        (Queued, Failed) => true,
        (Processing, Processing) => true,
        (Processing, Validating) => true,
        (Processing, Failed) => true,
        (Processing, Cancelled) => true,
        (Validating, Validating) => true,
        (Validating, Importing) => true,
        (Validating, Failed) => true,
        (Validating, Cancelled) => true,
        (Importing, Importing) => true,
        (Importing, Reconciling) => true,
        (Importing, Failed) => true,
        (Importing, Cancelled) => true,
        (Reconciling, Reconciling) => true,
        (Reconciling, Completed) => true,
        (Reconciling, Failed) => true,
        (Reconciling, Cancelled) => true,
        (Completed, Completed) => true,
        (Failed, Failed) => true,
        (Cancelled, Cancelled) => true,
        _ => false,
    };
}

/// <summary>
/// Observable progress/status of one TB/GL import request. The row is written in
/// its own small transactions so an operator (or a page refresh) can see what a
/// long-running import is doing, while the dataset itself is written in one
/// transaction that either commits completely or rolls back completely.
/// </summary>
public class ImportJob
{
    private ImportJob()
    {
    }

    public Guid JobId { get; private set; }

    public Guid EngagementId { get; private set; }

    public Guid FinancialPeriodId { get; private set; }

    public string DatasetKind { get; private set; } = FinancialDatasetKind.TrialBalance;

    public string Status { get; private set; } = ImportJobStatus.Queued;

    public string Stage { get; private set; } = ImportJobStatus.Queued;

    public Guid? UploadId { get; private set; }

    public Guid? ImportId { get; private set; }

    /// <summary>The column mapping the operator confirmed for this job (JSON).</summary>
    public string ColumnMappingJson { get; private set; } = "{}";

    public int AttemptNo { get; private set; } = 1;

    public bool AllowUnbalanced { get; private set; }

    public bool AllowRepeat { get; private set; }

    public bool CancelRequested { get; private set; }

    public int ProcessedRows { get; private set; }

    public int TotalRows { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public string? ErrorCode { get; private set; }

    public string RequestedAtUtc { get; private set; } = string.Empty;

    public Guid RequestedBy { get; private set; }

    public string? StartedAtUtc { get; private set; }

    public string? CompletedAtUtc { get; private set; }

    public bool IsTerminal => ImportJobStatus.IsTerminal(Status);

    public static ImportJob Create(
        Guid jobId,
        Guid engagementId,
        Guid financialPeriodId,
        string datasetKind,
        Guid? uploadId,
        string requestedAtUtc,
        Guid requestedBy,
        bool allowUnbalanced,
        bool allowRepeat,
        string? columnMappingJson = null)
    {
        if (!FinancialDatasetKind.IsValid(datasetKind))
        {
            throw new ValidationException("An import job must target the trial balance or the general ledger.");
        }

        return new ImportJob
        {
            JobId = jobId,
            EngagementId = engagementId,
            FinancialPeriodId = financialPeriodId,
            DatasetKind = datasetKind,
            Status = ImportJobStatus.Queued,
            Stage = ImportJobStatus.Queued,
            UploadId = uploadId,
            ColumnMappingJson = string.IsNullOrWhiteSpace(columnMappingJson) ? "{}" : columnMappingJson,
            AllowUnbalanced = allowUnbalanced,
            AllowRepeat = allowRepeat,
            RequestedAtUtc = requestedAtUtc,
            RequestedBy = requestedBy,
        };
    }

    public void Start(string startedAtUtc)
    {
        Transition(ImportJobStatus.Processing);
        Stage = ImportJobStatus.Processing;
        StartedAtUtc = startedAtUtc;
        Message = "Reading the source file and detecting its structure.";
    }

    public void BeginValidation(string startedAtUtc, int totalRows)
    {
        Transition(ImportJobStatus.Validating);
        Stage = ImportJobStatus.Validating;
        StartedAtUtc ??= startedAtUtc;
        TotalRows = totalRows;
        Message = "Validating rows against the period and the account master.";
    }

    public void ReportProgress(int processedRows, string message)
    {
        ProcessedRows = processedRows;
        Message = message;
    }

    public void BeginImport(int totalRows)
    {
        Transition(ImportJobStatus.Importing);
        Stage = ImportJobStatus.Importing;
        TotalRows = totalRows;
        ProcessedRows = 0;
        Message = "Writing rows inside one database transaction.";
    }

    public void BeginReconciliation(string message)
    {
        Transition(ImportJobStatus.Reconciling);
        Stage = ImportJobStatus.Reconciling;
        Message = message;
    }

    public void Complete(Guid importId, string message, string completedAtUtc)
    {
        Transition(ImportJobStatus.Completed);
        Stage = ImportJobStatus.Completed;
        ImportId = importId;
        Message = message;
        CompletedAtUtc = completedAtUtc;
    }

    public void Fail(string errorCode, string message, string completedAtUtc)
    {
        if (IsTerminal && Status != ImportJobStatus.Failed)
        {
            return;
        }

        Transition(ImportJobStatus.Failed);
        Stage = ImportJobStatus.Failed;
        ErrorCode = errorCode;
        Message = message;
        CompletedAtUtc = completedAtUtc;
    }

    public void Cancel(string message, string completedAtUtc)
    {
        if (IsTerminal)
        {
            return;
        }

        Transition(ImportJobStatus.Cancelled);
        Stage = ImportJobStatus.Cancelled;
        Message = message;
        CompletedAtUtc = completedAtUtc;
    }

    public void RequestCancellation()
    {
        if (IsTerminal)
        {
            throw new ValidationException("This import has already finished.");
        }

        CancelRequested = true;
    }

    private void Transition(string to)
    {
        if (!ImportJobStatus.CanTransition(Status, to))
        {
            throw new ValidationException($"An import job cannot move from {Status} to {to}.");
        }

        Status = to;
    }
}
