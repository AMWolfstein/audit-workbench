using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

public static class MaterialityStatus
{
    public const string Draft = "DRAFT";
    public const string Approved = "APPROVED";
    public const string Superseded = "SUPERSEDED";

    public static readonly IReadOnlyList<string> All = new[] { Draft, Approved, Superseded };
}

/// <summary>
/// Materiality foundation: the engagement's thresholds as an append-only version
/// chain. This phase deliberately stores no formula and computes nothing - the
/// auditor records the amount and the basis, and later phases may suggest values.
/// </summary>
public class MaterialityRecord
{
    private MaterialityRecord()
    {
    }

    public Guid MaterialityId { get; private set; }

    public Guid EngagementId { get; private set; }

    public Guid FinancialPeriodId { get; private set; }

    public int VersionNo { get; private set; }

    public string Status { get; private set; } = MaterialityStatus.Draft;

    public long OverallMaterialityMinor { get; private set; }

    public long? PerformanceMaterialityMinor { get; private set; }

    public long? ClearlyTrivialThresholdMinor { get; private set; }

    public string CurrencyCode { get; private set; } = string.Empty;

    public string BasisNote { get; private set; } = string.Empty;

    public string DeterminedAtUtc { get; private set; } = string.Empty;

    public Guid DeterminedBy { get; private set; }

    public string? ApprovedAtUtc { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public Guid? SupersedesId { get; private set; }

    public int RowVersion { get; private set; } = 1;

    public static MaterialityRecord CreateVersion(
        Guid materialityId,
        Guid engagementId,
        Guid financialPeriodId,
        int versionNo,
        long overallMaterialityMinor,
        long? performanceMaterialityMinor,
        long? clearlyTrivialThresholdMinor,
        string currencyCode,
        string basisNote,
        string determinedAtUtc,
        Guid determinedBy,
        Guid? previousMaterialityId)
    {
        if (versionNo <= 0)
        {
            throw new ValidationException("Materiality versions start at 1.");
        }

        if ((versionNo == 1) != (previousMaterialityId is null))
        {
            throw new ValidationException("A new materiality version must supersede the previous version.");
        }

        if (overallMaterialityMinor <= 0)
        {
            throw new ValidationException("Overall materiality must be a positive amount.");
        }

        if (performanceMaterialityMinor is <= 0)
        {
            throw new ValidationException("Performance materiality must be a positive amount when provided.");
        }

        if (clearlyTrivialThresholdMinor is <= 0)
        {
            throw new ValidationException("The clearly trivial threshold must be a positive amount when provided.");
        }

        if (performanceMaterialityMinor is { } performance && performance > overallMaterialityMinor)
        {
            throw new ValidationException("Performance materiality should not exceed overall materiality.");
        }

        if (clearlyTrivialThresholdMinor is { } trivial && performanceMaterialityMinor is { } performanceLimit
            && trivial > performanceLimit)
        {
            throw new ValidationException("The clearly trivial threshold should not exceed performance materiality.");
        }

        currencyCode = (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
        if (currencyCode.Length != 3)
        {
            throw new ValidationException("Materiality requires a three-letter currency code.");
        }

        return new MaterialityRecord
        {
            MaterialityId = materialityId,
            EngagementId = engagementId,
            FinancialPeriodId = financialPeriodId,
            VersionNo = versionNo,
            Status = MaterialityStatus.Draft,
            OverallMaterialityMinor = overallMaterialityMinor,
            PerformanceMaterialityMinor = performanceMaterialityMinor,
            ClearlyTrivialThresholdMinor = clearlyTrivialThresholdMinor,
            CurrencyCode = currencyCode,
            BasisNote = (basisNote ?? string.Empty).Trim(),
            DeterminedAtUtc = determinedAtUtc,
            DeterminedBy = determinedBy,
            SupersedesId = previousMaterialityId,
            RowVersion = 1,
        };
    }

    public void Approve(string approvedAtUtc, Guid approvedBy)
    {
        if (Status == MaterialityStatus.Superseded)
        {
            throw new ValidationException("A superseded materiality version cannot be approved.");
        }

        Status = MaterialityStatus.Approved;
        ApprovedAtUtc = approvedAtUtc;
        ApprovedBy = approvedBy;
        RowVersion++;
    }

    public void MarkSuperseded()
    {
        if (Status == MaterialityStatus.Superseded)
        {
            return;
        }

        Status = MaterialityStatus.Superseded;
        RowVersion++;
    }
}
