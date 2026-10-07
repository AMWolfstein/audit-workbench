namespace AuditWorkbench.Domain.FinancialImports;

/// <summary>
/// The application uses one sign convention everywhere:
/// <list type="bullet">
///   <item><description>an <b>amount</b> is debit-positive: debit - credit;</description></item>
///   <item><description>a <b>TB balance</b> is debit-positive: debit - credit;</description></item>
///   <item><description>a <b>GL net</b> is debit-positive: sum(debit) - sum(credit);</description></item>
///   <item><description>a credit-balance account therefore has a negative value
///   (revenue of 12,500,000 credit is stored and displayed as -12,500,000).</description></item>
/// </list>
/// </summary>
public static class SignConvention
{
    public const string DebitPositive = "DEBIT_POSITIVE";

    public const string Description =
        "All amounts are debit-positive: balance = debit - credit and GL net = sum(debit) - sum(credit). " +
        "A credit balance is shown as a negative number.";

    public static long Balance(long debitMinor, long creditMinor) => debitMinor - creditMinor;
}

public static class ReconciliationStatus
{
    /// <summary>TB balance equals the GL net for the account.</summary>
    public const string Matched = "MATCHED";

    /// <summary>Both datasets contain the account but the balances differ.</summary>
    public const string Difference = "DIFFERENCE";

    /// <summary>The account is in the trial balance but has no ledger activity.</summary>
    public const string TbOnly = "TB_WITHOUT_GL";

    /// <summary>The account has ledger activity but is missing from the trial balance.</summary>
    public const string GlOnly = "GL_WITHOUT_TB";

    public static readonly IReadOnlyList<string> All = new[] { Matched, Difference, TbOnly, GlOnly };

    public static string Describe(string status) => status switch
    {
        Matched => "Matched",
        Difference => "Balance difference",
        TbOnly => "TB account with no GL activity",
        GlOnly => "GL account missing from TB",
        _ => status,
    };
}

/// <summary>One account of the TB &lt;-&gt; GL reconciliation, always debit-positive.</summary>
public sealed record ReconciliationRow(
    string AccountCode,
    string AccountName,
    string Status,
    long? TbBalanceMinor,
    long? TbDebitMinor,
    long? TbCreditMinor,
    long? GlDebitMinor,
    long? GlCreditMinor,
    long? GlNetMinor,
    int GlLineCount,
    int GlOutOfPeriodCount)
{
    public long DifferenceMinor => (TbBalanceMinor ?? 0) - (GlNetMinor ?? 0);

    public bool HasDifference => Status == ReconciliationStatus.Difference;
}

/// <summary>Totals of one reconciliation run between a TB version and a GL snapshot.</summary>
public sealed class ReconciliationSummary
{
    public required IReadOnlyList<ReconciliationRow> Rows { get; init; }

    public Guid? TbImportId { get; init; }

    public Guid? GlImportId { get; init; }

    public string? TbLabel { get; init; }

    public string? GlLabel { get; init; }

    public int MatchedCount => Rows.Count(row => row.Status == ReconciliationStatus.Matched);

    public int DifferenceCount => Rows.Count(row => row.Status == ReconciliationStatus.Difference);

    public int TbOnlyCount => Rows.Count(row => row.Status == ReconciliationStatus.TbOnly);

    public int GlOnlyCount => Rows.Count(row => row.Status == ReconciliationStatus.GlOnly);

    public long TbTotalDebitMinor => Rows.Sum(row => row.TbDebitMinor ?? 0);

    public long TbTotalCreditMinor => Rows.Sum(row => row.TbCreditMinor ?? 0);

    public long TbTotalBalanceMinor => Rows.Sum(row => row.TbBalanceMinor ?? 0);

    public long GlTotalDebitMinor => Rows.Sum(row => row.GlDebitMinor ?? 0);

    public long GlTotalCreditMinor => Rows.Sum(row => row.GlCreditMinor ?? 0);

    public long GlTotalNetMinor => Rows.Sum(row => row.GlNetMinor ?? 0);

    public long DifferenceMinor => TbTotalBalanceMinor - GlTotalNetMinor;

    public int GlOutOfPeriodLineCount => Rows.Sum(row => row.GlOutOfPeriodCount);

    public bool IsFullyReconciled => DifferenceCount == 0 && TbOnlyCount == 0 && GlOnlyCount == 0;

    /// <summary>Rows the operator must look at first: every difference is listed, nothing is hidden.</summary>
    public IReadOnlyList<ReconciliationRow> Exceptions => Rows
        .Where(row => row.Status != ReconciliationStatus.Matched)
        .OrderBy(row => RowOrder(row.Status))
        .ThenBy(row => row.AccountCode, StringComparer.Ordinal)
        .ToList();

    private static int RowOrder(string status) => status switch
    {
        ReconciliationStatus.Difference => 0,
        ReconciliationStatus.GlOnly => 1,
        ReconciliationStatus.TbOnly => 2,
        _ => 3,
    };
}
