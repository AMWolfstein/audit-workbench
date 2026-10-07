using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// Lifecycle of the audit period of one engagement (migration 0007).
/// <para>
/// OPEN and IN_PROGRESS accept imports; FINALIZED and LOCKED do not. The database
/// enforces the same rule, so a period that is finalized in the UI can never be
/// overwritten by a later file - not even by a bug in the application layer.
/// </para>
/// </summary>
public static class FinancialPeriodStatus
{
    public const string Open = "OPEN";
    public const string InProgress = "IN_PROGRESS";
    public const string Finalized = "FINALIZED";
    public const string Locked = "LOCKED";

    public static readonly IReadOnlyList<string> All = new[] { Open, InProgress, Finalized, Locked };

    public static bool IsOpenForImports(string status) => status is Open or InProgress;

    public static bool IsClosed(string status) => status is Finalized or Locked;

    public static bool CanTransition(string from, string to) => (from, to) switch
    {
        (Open, Open or InProgress or Finalized or Locked) => true,
        (InProgress, InProgress or Finalized or Locked) => true,
        (Finalized, Finalized or Locked) => true,
        (Locked, Locked) => true,
        _ => false,
    };

    public static void EnsureTransitionAllowed(string from, string to)
    {
        if (!CanTransition(from, to))
        {
            throw new ValidationException(
                $"A financial period cannot move from {from} to {to}. A locked period stays locked.");
        }
    }

    /// <summary>Guards every import entry point with the period lock state.</summary>
    public static void EnsureOpenForImports(string status, string periodLabel)
    {
        if (!IsOpenForImports(status))
        {
            throw new ValidationException(
                $"The financial period {periodLabel} is {status} and no longer accepts imports. " +
                "Reopen the period (if it is not locked) or record a new version in a later period.");
        }
    }
}

/// <summary>
/// The audit period of one engagement: the reporting date plus the import lock
/// state. Fiscal year start/end stay on the financial year; the engagement stays
/// the isolation boundary (requirements section 2).
/// </summary>
public sealed class FinancialPeriod
{
    private FinancialPeriod()
    {
    }

    public Guid FinancialPeriodId { get; private set; }

    public Guid EngagementId { get; private set; }

    public Guid FinancialYearId { get; private set; }

    public string ReportingDate { get; private set; } = string.Empty;

    public string Status { get; private set; } = FinancialPeriodStatus.Open;

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public Guid CreatedBy { get; private set; }

    public string UpdatedAtUtc { get; private set; } = string.Empty;

    public int RowVersion { get; private set; } = 1;

    public bool IsOpenForImports => FinancialPeriodStatus.IsOpenForImports(Status);

    public static FinancialPeriod Create(
        Guid financialPeriodId,
        Guid engagementId,
        Guid financialYearId,
        string reportingDate,
        Guid createdBy,
        string createdAtUtc,
        string? status = null)
    {
        if (string.IsNullOrWhiteSpace(reportingDate) || !DateOnly.TryParse(reportingDate, out _))
        {
            throw new ValidationException("The reporting date of a financial period is required (yyyy-MM-dd).");
        }

        var initialStatus = string.IsNullOrWhiteSpace(status) ? FinancialPeriodStatus.Open : status;
        if (!FinancialPeriodStatus.All.Contains(initialStatus))
        {
            throw new ValidationException($"'{status}' is not a financial period status.");
        }

        return new FinancialPeriod
        {
            FinancialPeriodId = financialPeriodId,
            EngagementId = engagementId,
            FinancialYearId = financialYearId,
            ReportingDate = reportingDate.Trim(),
            Status = initialStatus,
            CreatedAtUtc = createdAtUtc,
            CreatedBy = createdBy,
            UpdatedAtUtc = createdAtUtc,
            RowVersion = 1,
        };
    }

    /// <summary>The reporting date must fall inside the fiscal year it belongs to.</summary>
    public void EnsureReportingDateWithin(string periodStart, string periodEnd)
    {
        if (!DateOnly.TryParse(ReportingDate, out var reporting) ||
            !DateOnly.TryParse(periodStart, out var start) ||
            !DateOnly.TryParse(periodEnd, out var end))
        {
            throw new ValidationException("The reporting date or the financial year dates could not be read.");
        }

        if (reporting < start || reporting > end)
        {
            throw new ValidationException(
                $"The reporting date {ReportingDate} is outside the financial year {periodStart} to {periodEnd}.");
        }
    }

    public void ChangeStatus(string status, string updatedAtUtc)
    {
        if (!FinancialPeriodStatus.All.Contains(status))
        {
            throw new ValidationException($"'{status}' is not a financial period status.");
        }

        FinancialPeriodStatus.EnsureTransitionAllowed(Status, status);
        if (status == Status)
        {
            return;
        }

        Status = status;
        UpdatedAtUtc = updatedAtUtc;
        RowVersion++;
    }

    public void ChangeReportingDate(string reportingDate, string updatedAtUtc)
    {
        if (FinancialPeriodStatus.IsClosed(Status))
        {
            throw new ValidationException(
                "The reporting date of a finalized or locked period cannot change; the reported figures are agreed.");
        }

        if (string.IsNullOrWhiteSpace(reportingDate) || !DateOnly.TryParse(reportingDate, out _))
        {
            throw new ValidationException("The reporting date is required (yyyy-MM-dd).");
        }

        ReportingDate = reportingDate.Trim();
        UpdatedAtUtc = updatedAtUtc;
        RowVersion++;
    }

    public void EnsureOpenForImports()
    {
        FinancialPeriodStatus.EnsureOpenForImports(Status, ReportingDate);
    }

    public void EnsureExpectedVersion(int? expectedRowVersion)
    {
        if (expectedRowVersion is not null && expectedRowVersion != RowVersion)
        {
            throw new ConcurrencyException(
                "This financial period changed in another window. Reload the page and try again.");
        }
    }
}
