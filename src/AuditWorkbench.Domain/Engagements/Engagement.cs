using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.Engagements;

/// <summary>
/// The audit file for one company and one financial year: the isolation
/// boundary and the lock boundary (ADR-001). Its company and financial year are
/// identity-defining and can never be changed - a new year is a new engagement.
/// </summary>
public class Engagement
{
    private Engagement()
    {
    }

    public Guid EngagementId { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid FinancialYearId { get; private set; }

    public string Status { get; private set; } = EngagementStatus.Draft;

    public string CurrencyCode { get; private set; } = "USD";

    public int MinorUnitScale { get; private set; } = 2;

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public Guid CreatedBy { get; private set; }

    public string? FinalizedAtUtc { get; private set; }

    public Guid? FinalizedBy { get; private set; }

    public string? FinalizationDigest { get; private set; }

    public string? FinalizationManifestVersion { get; private set; }

    public int RowVersion { get; private set; } = 1;

    public bool IsOpen => EngagementStatus.IsOpen(Status);

    public bool IsFinalized => EngagementStatus.IsFinalized(Status);

    public static Engagement Create(
        Guid engagementId,
        Guid companyId,
        Guid financialYearId,
        string currencyCode,
        int minorUnitScale,
        string createdAtUtc,
        Guid createdBy,
        string status = EngagementStatus.Draft)
    {
        currencyCode = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        if (currencyCode.Length != 3 || !currencyCode.All(char.IsLetter))
        {
            throw new ValidationException("Currency must be a three-letter ISO 4217 code.");
        }

        if (minorUnitScale is < 0 or > 6)
        {
            throw new ValidationException("Minor unit scale must be between 0 and 6.");
        }

        if (!EngagementStatus.Open.Contains(status))
        {
            throw new ValidationException("A new engagement must start as DRAFT or IN_PROGRESS.");
        }

        return new Engagement
        {
            EngagementId = engagementId,
            CompanyId = companyId,
            FinancialYearId = financialYearId,
            Status = status,
            CurrencyCode = currencyCode,
            MinorUnitScale = minorUnitScale,
            CreatedAtUtc = createdAtUtc,
            CreatedBy = createdBy,
            RowVersion = 1,
        };
    }

    /// <summary>Guard used by every write command before it touches year-owned data.</summary>
    public void EnsureOpenForEditing(string financialYearLabel)
    {
        if (IsFinalized)
        {
            throw new EngagementFinalizedException(
                $"{financialYearLabel} was finalized on {FinalizedAtUtc} and is read-only. " +
                "Record the correction in the current financial year instead.")
            {
                EngagementId = EngagementId,
            };
        }
    }

    public void EnsureExpectedVersion(int? expectedRowVersion)
    {
        if (expectedRowVersion is not null && expectedRowVersion != RowVersion)
        {
            throw new ConcurrencyException(
                "This financial year changed in another window. Reload the page and try again.");
        }
    }

    public void ChangeStatus(string newStatus)
    {
        EngagementStatus.EnsureTransitionAllowed(Status, newStatus);
        if (newStatus == EngagementStatus.Finalized)
        {
            throw new ValidationException("Use the finalization command to finalize a financial year.");
        }

        Status = newStatus;
        RowVersion++;
    }

    /// <summary>
    /// Applied inside the finalization transaction only, after the manifest row
    /// has been written (the database also enforces this pairing).
    /// </summary>
    public void MarkFinalized(string finalizedAtUtc, Guid finalizedBy, string digest, string manifestVersion)
    {
        if (IsFinalized)
        {
            throw new EngagementFinalizedException("This financial year has already been finalized.");
        }

        EngagementStatus.EnsureTransitionAllowed(Status, EngagementStatus.Finalized);

        Status = EngagementStatus.Finalized;
        FinalizedAtUtc = finalizedAtUtc;
        FinalizedBy = finalizedBy;
        FinalizationDigest = digest;
        FinalizationManifestVersion = manifestVersion;
        RowVersion++;
    }
}
