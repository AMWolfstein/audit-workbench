using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// One imported journal (entry) of a GL snapshot. A journal groups its lines and
/// carries the client transaction identity used by the future roll-forward.
/// <para>
/// <see cref="JournalIdentity"/> is the <em>client's</em> identity: either the
/// mapped source key (journal/document/voucher number) or a deterministic
/// composite fingerprint (see <c>TransactionIdentity</c>). The database primary
/// key identifies the stored row and is never the transaction identity.
/// </para>
/// </summary>
public class GlJournal
{
    private GlJournal()
    {
    }

    public Guid GlJournalId { get; private set; }

    public Guid ImportId { get; private set; }

    public Guid EngagementId { get; private set; }

    public Guid FinancialPeriodId { get; private set; }

    public string JournalIdentity { get; private set; } = string.Empty;

    public string IdentitySource { get; private set; } = ImportIdentitySource.Source;

    public string? JournalNumber { get; private set; }

    public string? JournalSource { get; private set; }

    public string? PostingDate { get; private set; }

    public string? Reference { get; private set; }

    public string? Description { get; private set; }

    public string? CurrencyCode { get; private set; }

    public string? PreparedBy { get; private set; }

    public int LineCount { get; private set; }

    public string JournalHash { get; private set; } = string.Empty;

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public static GlJournal Create(
        Guid glJournalId,
        Guid importId,
        Guid engagementId,
        Guid financialPeriodId,
        string journalIdentity,
        string identitySource,
        string? journalNumber,
        string? journalSource,
        string? postingDate,
        string? reference,
        string? description,
        string? currencyCode,
        string? preparedBy,
        string journalHash,
        string createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(journalIdentity))
        {
            throw new ValidationException("A general-ledger entry requires an identity.");
        }

        if (identitySource is not (ImportIdentitySource.Source or ImportIdentitySource.Derived))
        {
            throw new ValidationException($"'{identitySource}' is not a supported identity source.");
        }

        var digest = journalHash ?? string.Empty;
        if (digest.Length != 64)
        {
            throw new ValidationException("A general-ledger entry requires its entry digest.");
        }

        return new GlJournal
        {
            GlJournalId = glJournalId,
            ImportId = importId,
            EngagementId = engagementId,
            FinancialPeriodId = financialPeriodId,
            JournalIdentity = journalIdentity.Trim(),
            IdentitySource = identitySource,
            JournalNumber = Blank(journalNumber),
            JournalSource = Blank(journalSource),
            PostingDate = Blank(postingDate),
            Reference = Blank(reference),
            Description = Blank(description),
            CurrencyCode = Blank(currencyCode),
            PreparedBy = Blank(preparedBy),
            JournalHash = digest,
            CreatedAtUtc = createdAtUtc,
        };
    }

    internal void SetLineCount(int lineCount)
    {
        if (lineCount < 0)
        {
            throw new ValidationException("Line count must not be negative.");
        }

        LineCount = lineCount;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
