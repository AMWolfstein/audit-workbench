using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Domain.FinancialData;

/// <summary>
/// One imported trial-balance line: immutable evidence of the client file.
/// <para>
/// Sign convention: <see cref="BalanceMinor"/> is debit-positive
/// (debit - credit). <see cref="DebitMinor"/> and <see cref="CreditMinor"/> are
/// non-negative source columns.
/// </para>
/// </summary>
public class TbLine
{
    private TbLine()
    {
    }

    public Guid TbLineId { get; private set; }

    public Guid ImportId { get; private set; }

    public Guid EngagementId { get; private set; }

    public Guid FinancialPeriodId { get; private set; }

    public Guid AccountId { get; private set; }

    public int LineNo { get; private set; }

    public int? SourceRowNo { get; private set; }

    public string AccountCode { get; private set; } = string.Empty;

    public string AccountName { get; private set; } = string.Empty;

    public string NormalizedCode { get; private set; } = string.Empty;

    public long DebitMinor { get; private set; }

    public long CreditMinor { get; private set; }

    public long BalanceMinor { get; private set; }

    public string CurrencyCode { get; private set; } = string.Empty;

    public string? CostCenter { get; private set; }

    public string? AccountGroup { get; private set; }

    /// <summary>Unmapped client-specific columns, preserved verbatim as JSON.</summary>
    public string ExtraColumnsJson { get; private set; } = "{}";

    public string RowHash { get; private set; } = string.Empty;

    public string CreatedAtUtc { get; private set; } = string.Empty;

    public static TbLine Create(
        Guid tbLineId,
        Guid importId,
        Guid engagementId,
        Guid financialPeriodId,
        Guid accountId,
        int lineNo,
        int? sourceRowNo,
        string accountCode,
        string accountName,
        string normalizedCode,
        long debitMinor,
        long creditMinor,
        long balanceMinor,
        string currencyCode,
        string? costCenter,
        string? accountGroup,
        string extraColumnsJson,
        string rowHash,
        string createdAtUtc)
    {
        if (lineNo <= 0)
        {
            throw new ValidationException("Trial-balance line numbers start at 1.");
        }

        if (debitMinor < 0 || creditMinor < 0)
        {
            throw new ValidationException(
                $"Debit and credit on {accountCode} must not be negative; use the opposite column for contra entries.");
        }

        if (string.IsNullOrWhiteSpace(accountCode))
        {
            throw new ValidationException("A trial-balance line requires an account code.");
        }

        if ((rowHash ?? string.Empty).Length != 64)
        {
            throw new ValidationException("A trial-balance line requires its row digest.");
        }

        return new TbLine
        {
            TbLineId = tbLineId,
            ImportId = importId,
            EngagementId = engagementId,
            FinancialPeriodId = financialPeriodId,
            AccountId = accountId,
            LineNo = lineNo,
            SourceRowNo = sourceRowNo,
            AccountCode = accountCode.Trim(),
            AccountName = (accountName ?? string.Empty).Trim(),
            NormalizedCode = normalizedCode,
            DebitMinor = debitMinor,
            CreditMinor = creditMinor,
            BalanceMinor = balanceMinor,
            CurrencyCode = currencyCode,
            CostCenter = Blank(costCenter),
            AccountGroup = Blank(accountGroup),
            ExtraColumnsJson = string.IsNullOrWhiteSpace(extraColumnsJson) ? "{}" : extraColumnsJson,
            RowHash = rowHash,
            CreatedAtUtc = createdAtUtc,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
