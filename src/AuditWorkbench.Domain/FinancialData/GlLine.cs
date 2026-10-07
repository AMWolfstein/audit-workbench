using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// One imported general-ledger line.
/// <para>
/// Sign convention: <see cref="AmountMinor"/> is debit-positive
/// (debit - credit); <see cref="DebitMinor"/> / <see cref="CreditMinor"/> are the
/// non-negative source columns.
/// </para>
/// <para>
/// Three digests make the future roll-forward a pure comparison:
/// <see cref="LineHash"/> covers identity + attributes + values,
/// <see cref="ValueHash"/> covers the monetary values only, and
/// <see cref="AttributeHash"/> covers account, date, description and references.
/// </para>
/// </summary>
public class GlLine
{
    private GlLine()
    {
    }

    public Guid GlLineId { get; private set; }

    public Guid GlJournalId { get; private set; }

    public Guid ImportId { get; private set; }

    public Guid EngagementId { get; private set; }

    public Guid FinancialPeriodId { get; private set; }

    public int LineNo { get; private set; }

    /// <summary>The client's line identity within its journal (source key or ordinal).</summary>
    public string LineIdentity { get; private set; } = string.Empty;

    public string IdentitySource { get; private set; } = ImportIdentitySource.Source;

    public string? SourceLineNo { get; private set; }

    /// <summary>Resolved account master row; null when the code is not in this engagement's TB.</summary>
    public Guid? AccountId { get; private set; }

    public string AccountCode { get; private set; } = string.Empty;

    public string? AccountName { get; private set; }

    public string TransactionDate { get; private set; } = string.Empty;

    public string? PostingDate { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public long DebitMinor { get; private set; }

    public long CreditMinor { get; private set; }

    public long AmountMinor { get; private set; }

    public string? CurrencyCode { get; private set; }

    public string? JournalSource { get; private set; }

    public string? Reference { get; private set; }

    public string? PreparedBy { get; private set; }

    /// <summary>True when the transaction date falls outside the financial period (flagged, never moved).</summary>
    public bool IsOutOfPeriod { get; private set; }

    public string LineHash { get; private set; } = string.Empty;

    public string ValueHash { get; private set; } = string.Empty;

    public string AttributeHash { get; private set; } = string.Empty;

    public string ExtraColumnsJson { get; private set; } = "{}";

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public static GlLine Create(
        Guid glLineId,
        Guid glJournalId,
        Guid importId,
        Guid engagementId,
        Guid financialPeriodId,
        int lineNo,
        string lineIdentity,
        string identitySource,
        string? sourceLineNo,
        Guid? accountId,
        string accountCode,
        string? accountName,
        string transactionDate,
        string? postingDate,
        string description,
        long debitMinor,
        long creditMinor,
        string? currencyCode,
        string? journalSource,
        string? reference,
        string? preparedBy,
        bool isOutOfPeriod,
        string lineHash,
        string valueHash,
        string attributeHash,
        string extraColumnsJson,
        string createdAtUtc)
    {
        if (lineNo <= 0)
        {
            throw new ValidationException("General-ledger line numbers start at 1.");
        }

        if (debitMinor < 0 || creditMinor < 0)
        {
            throw new ValidationException(
                $"Debit and credit on {accountCode} must not be negative; use the opposite column for contra entries.");
        }

        if (string.IsNullOrWhiteSpace(accountCode))
        {
            throw new ValidationException("A general-ledger line requires an account code.");
        }

        if (string.IsNullOrWhiteSpace(lineIdentity))
        {
            throw new ValidationException("A general-ledger line requires an identity.");
        }

        if (identitySource is not (ImportIdentitySource.Source or ImportIdentitySource.Derived))
        {
            throw new ValidationException($"'{identitySource}' is not a supported identity source.");
        }

        if (string.IsNullOrWhiteSpace(transactionDate))
        {
            throw new ValidationException("A general-ledger line requires a transaction date.");
        }

        return new GlLine
        {
            GlLineId = glLineId,
            GlJournalId = glJournalId,
            ImportId = importId,
            EngagementId = engagementId,
            FinancialPeriodId = financialPeriodId,
            LineNo = lineNo,
            LineIdentity = lineIdentity.Trim(),
            IdentitySource = identitySource,
            SourceLineNo = Blank(sourceLineNo),
            AccountId = accountId,
            AccountCode = accountCode.Trim(),
            AccountName = Blank(accountName),
            TransactionDate = transactionDate.Trim(),
            PostingDate = Blank(postingDate),
            Description = (description ?? string.Empty).Trim(),
            DebitMinor = debitMinor,
            CreditMinor = creditMinor,
            AmountMinor = debitMinor - creditMinor,
            CurrencyCode = Blank(currencyCode),
            JournalSource = Blank(journalSource),
            Reference = Blank(reference),
            PreparedBy = Blank(preparedBy),
            IsOutOfPeriod = isOutOfPeriod,
            LineHash = lineHash,
            ValueHash = valueHash,
            AttributeHash = attributeHash,
            ExtraColumnsJson = string.IsNullOrWhiteSpace(extraColumnsJson) ? "{}" : extraColumnsJson,
            CreatedAtUtc = createdAtUtc,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
