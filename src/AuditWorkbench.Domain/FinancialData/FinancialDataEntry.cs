using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// One append-only revision of one account's value inside one engagement
/// (ADR-008). Corrections insert revision N+1; rows are never updated.
/// </summary>
public class FinancialDataEntry
{
    private FinancialDataEntry()
    {
    }

    public Guid FinancialDataId { get; private set; }

    public Guid EngagementId { get; private set; }

    public Guid AccountId { get; private set; }

    public int RevisionNo { get; private set; }

    /// <summary>Exact signed 64-bit minor units (ADR-009). Never floating point.</summary>
    public long AmountMinor { get; private set; }

    public string CurrencyCode { get; private set; } = string.Empty;

    public Guid? SupersedesId { get; private set; }

    public string? CorrectionReason { get; private set; }

    public string RecordedAtUtc { get; private set; } = string.Empty;

    public Guid RecordedBy { get; private set; }

    public static FinancialDataEntry CreateRevision(
        Guid financialDataId,
        Account account,
        FinancialDataEntry? previous,
        long amountMinor,
        string currencyCode,
        string? correctionReason,
        string recordedAtUtc,
        Guid recordedBy)
    {
        if (previous is not null && previous.AccountId != account.AccountId)
        {
            throw new ValidationException("A revision must supersede a value of the same account.");
        }

        if (previous is not null && previous.EngagementId != account.EngagementId)
        {
            throw new ValidationException("A revision must stay inside the same engagement.");
        }

        var revisionNo = previous is null ? 1 : previous.RevisionNo + 1;
        if (revisionNo > 1 && string.IsNullOrWhiteSpace(correctionReason))
        {
            correctionReason = "Draft correction";
        }

        return new FinancialDataEntry
        {
            FinancialDataId = financialDataId,
            EngagementId = account.EngagementId,
            AccountId = account.AccountId,
            RevisionNo = revisionNo,
            AmountMinor = amountMinor,
            CurrencyCode = currencyCode,
            SupersedesId = previous?.FinancialDataId,
            CorrectionReason = revisionNo == 1 ? null : correctionReason,
            RecordedAtUtc = recordedAtUtc,
            RecordedBy = recordedBy,
        };
    }
}
