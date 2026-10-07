using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AuditWorkbench.Domain.FinancialImports;

/// <summary>Blocking errors stop the import; warnings are recorded and visible; info is context.</summary>
public static class IssueSeverity
{
    public const string Error = "ERROR";
    public const string Warning = "WARNING";
    public const string Info = "INFO";

    public static readonly IReadOnlyList<string> All = new[] { Error, Warning, Info };
}

public static class ImportIssueCodes
{
    public const string MissingRequiredColumn = "MISSING_REQUIRED_COLUMN";
    public const string MissingValueColumn = "MISSING_VALUE_COLUMN";
    public const string EmptyFile = "EMPTY_FILE";
    public const string HeaderNotFound = "HEADER_NOT_FOUND";
    public const string UnknownColumn = "UNMAPPED_SOURCE_COLUMN";
    public const string NewAccountCreated = "NEW_ACCOUNT_ADDED_TO_MASTER";
    public const string MissingAccountCode = "MISSING_ACCOUNT_CODE";
    public const string InvalidAccountCode = "INVALID_ACCOUNT_CODE";
    public const string LongAccountCode = "ACCOUNT_CODE_TOO_LONG";
    public const string MissingAccountName = "MISSING_ACCOUNT_NAME";
    public const string AccountNameChanged = "ACCOUNT_NAME_DIFFERS_FROM_MASTER";
    public const string InvalidAmount = "INVALID_AMOUNT";
    public const string NegativeAmount = "NEGATIVE_DEBIT_OR_CREDIT";
    public const string BothDebitAndCredit = "BOTH_DEBIT_AND_CREDIT";
    public const string BalanceMismatch = "BALANCE_DOES_NOT_EQUAL_DEBIT_MINUS_CREDIT";
    public const string DuplicateAccountCode = "DUPLICATE_ACCOUNT_CODE";
    public const string DuplicateAccountCodeNormalized = "DUPLICATE_NORMALIZED_ACCOUNT_CODE";
    public const string UnsupportedCurrency = "UNSUPPORTED_CURRENCY";
    public const string UnbalancedTrialBalance = "UNBALANCED_TRIAL_BALANCE";
    public const string UnbalancedLedger = "UNBALANCED_GENERAL_LEDGER";
    public const string ZeroAmountRow = "ZERO_AMOUNT_ROW";
    public const string MissingTransactionDate = "MISSING_TRANSACTION_DATE";
    public const string InvalidTransactionDate = "INVALID_TRANSACTION_DATE";
    public const string InvalidPostingDate = "INVALID_POSTING_DATE";
    public const string OutOfPeriod = "TRANSACTION_OUTSIDE_PERIOD";
    public const string DuplicateTransaction = "DUPLICATE_TRANSACTION_IDENTITY";
    public const string DerivedTransactionIdentity = "DERIVED_TRANSACTION_IDENTITY";
    public const string InvalidAccountReference = "ACCOUNT_NOT_IN_TRIAL_BALANCE";
    public const string UnexpectedCurrency = "UNEXPECTED_CURRENCY";
    public const string MissingJournalIdentity = "MISSING_JOURNAL_IDENTITY";
    public const string RowLimitExceeded = "ROW_LIMIT_EXCEEDED";
    public const string DuplicateSourceFile = "DUPLICATE_SOURCE_FILE";
    public const string IssueOverflow = "FURTHER_ISSUES_NOT_LISTED";
}

/// <summary>One finding of a validation pass, addressable to a source row.</summary>
public sealed record ImportIssue(
    string Severity,
    string Code,
    string Message,
    int? RowNumber = null,
    string? Column = null);

/// <summary>
/// The validation report shown to the operator and stored on the import as
/// provenance. Only a capped number of individual issues is retained in the JSON
/// (the counts are always exact), so a file with 200k bad rows cannot bloat the
/// workspace.
/// </summary>
public sealed class ImportValidationReport
{
    public const int MaxRetainedIssues = 200;

    private readonly List<ImportIssue> _issues = new();

    private readonly Dictionary<string, int> _issueCounts = new(StringComparer.Ordinal);

    public ImportValidationReport(string datasetKind)
    {
        DatasetKind = datasetKind;
    }

    public string DatasetKind { get; }

    public int RowCount { get; private set; }

    public int ValidRowCount { get; private set; }

    public int WarningRowCount { get; private set; }

    public int ErrorRowCount { get; private set; }

    public int OutOfPeriodCount { get; private set; }

    public long TotalDebitMinor { get; private set; }

    public long TotalCreditMinor { get; private set; }

    public long TotalBalanceMinor { get; private set; }

    public string? FirstTransactionDate { get; private set; }

    public string? LastTransactionDate { get; private set; }

    public int IssueOverflowCount { get; private set; }

    public IReadOnlyList<ImportIssue> Issues => _issues;

    public IReadOnlyDictionary<string, int> IssueCounts => _issueCounts;

    public int DifferenceMinor => TotalDebitMinor - TotalCreditMinor;

    public bool IsBalanced => DifferenceMinor == 0;

    public bool HasErrors => ErrorRowCount > 0 || _issueCounts.ContainsKey(ImportIssueCodes.UnbalancedTrialBalance);

    public int TotalIssueCount => _issueCounts.Values.Sum();

    public string ValidationStatus => HasErrors
        ? Domain.FinancialData.DatasetValidationStatus.Rejected
        : _issueCounts.Count > 0
            ? Domain.FinancialData.DatasetValidationStatus.ValidWithWarnings
            : Domain.FinancialData.DatasetValidationStatus.Valid;

    /// <summary>Adds a finding. Row numbers are 1-based source row numbers when known.</summary>
    public void Add(string severity, string code, string message, int? rowNumber = null, string? column = null)
    {
        _issueCounts[code] = _issueCounts.TryGetValue(code, out var count) ? count + 1 : 1;
        if (_issues.Count < MaxRetainedIssues)
        {
            _issues.Add(new ImportIssue(severity, code, message, rowNumber, column));
        }
        else
        {
            IssueOverflowCount++;
        }
    }

    public void AddError(string code, string message, int? rowNumber = null, string? column = null) =>
        Add(IssueSeverity.Error, code, message, rowNumber, column);

    public void AddWarning(string code, string message, int? rowNumber = null, string? column = null) =>
        Add(IssueSeverity.Warning, code, message, rowNumber, column);

    public void AddInfo(string code, string message, int? rowNumber = null, string? column = null) =>
        Add(IssueSeverity.Info, code, message, rowNumber, column);

    /// <summary>Registers one source row and its verdict.</summary>
    public void RegisterRow(bool hasError, bool hasWarning, bool outOfPeriod, long? debitMinor, long? creditMinor,
        long? balanceMinor, string? transactionDate)
    {
        RowCount++;
        if (hasError)
        {
            ErrorRowCount++;
        }
        else
        {
            ValidRowCount++;
            if (hasWarning)
            {
                WarningRowCount++;
            }
        }

        if (outOfPeriod)
        {
            OutOfPeriodCount++;
        }

        TotalDebitMinor += debitMinor ?? 0;
        TotalCreditMinor += creditMinor ?? 0;
        TotalBalanceMinor += balanceMinor ?? (debitMinor ?? 0) - (creditMinor ?? 0);

        if (!string.IsNullOrWhiteSpace(transactionDate))
        {
            if (FirstTransactionDate is null || string.CompareOrdinal(transactionDate, FirstTransactionDate) < 0)
            {
                FirstTransactionDate = transactionDate;
            }

            if (LastTransactionDate is null || string.CompareOrdinal(transactionDate, LastTransactionDate) > 0)
            {
                LastTransactionDate = transactionDate;
            }
        }
    }

    public string ToJson()
    {
        var builder = new StringBuilder();
        builder.Append('{');
        builder.Append("\"dataset_kind\":").Append(JsonSerializer.Serialize(DatasetKind)).Append(',');
        builder.Append("\"rows\":").Append(RowCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"valid\":").Append(ValidRowCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"warnings\":").Append(WarningRowCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"errors\":").Append(ErrorRowCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"out_of_period\":").Append(OutOfPeriodCount.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"totals\":{");
        builder.Append("\"debit_minor\":").Append(TotalDebitMinor.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"credit_minor\":").Append(TotalCreditMinor.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"difference_minor\":").Append(DifferenceMinor.ToString(CultureInfo.InvariantCulture)).Append(',');
        builder.Append("\"balance_minor\":").Append(TotalBalanceMinor.ToString(CultureInfo.InvariantCulture)).Append('}').Append(',');
        builder.Append("\"balanced\":").Append(IsBalanced ? "true" : "false").Append(',');
        builder.Append("\"first_transaction_date\":").Append(JsonSerializer.Serialize(FirstTransactionDate)).Append(',');
        builder.Append("\"last_transaction_date\":").Append(JsonSerializer.Serialize(LastTransactionDate)).Append(',');
        builder.Append("\"validation_status\":").Append(JsonSerializer.Serialize(ValidationStatus)).Append(',');
        builder.Append("\"issue_counts\":{");
        var first = true;
        foreach (var (code, count) in _issueCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(JsonSerializer.Serialize(code)).Append(':').Append(count.ToString(CultureInfo.InvariantCulture));
            first = false;
        }

        builder.Append('}').Append(',');
        builder.Append("\"issues\":[");
        for (var i = 0; i < _issues.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var issue = _issues[i];
            builder.Append('{');
            builder.Append("\"severity\":").Append(JsonSerializer.Serialize(issue.Severity)).Append(',');
            builder.Append("\"code\":").Append(JsonSerializer.Serialize(issue.Code)).Append(',');
            builder.Append("\"message\":").Append(JsonSerializer.Serialize(issue.Message));
            if (issue.RowNumber is not null)
            {
                builder.Append(",\"row\":").Append(issue.RowNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (issue.Column is not null)
            {
                builder.Append(",\"column\":").Append(JsonSerializer.Serialize(issue.Column));
            }

            builder.Append('}');
        }

        builder.Append(']');
        if (IssueOverflowCount > 0)
        {
            builder.Append(",\"issues_omitted\":").Append(IssueOverflowCount.ToString(CultureInfo.InvariantCulture));
        }

        return builder.Append('}').ToString();
    }

    /// <summary>Re-reads a persisted report (import detail pages, tests).</summary>
    public static ImportValidationReportSnapshot Parse(string? json)
    {
        var snapshot = new ImportValidationReportSnapshot();
        if (string.IsNullOrWhiteSpace(json))
        {
            return snapshot;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            snapshot.RowCount = GetInt(root, "rows");
            snapshot.ValidRowCount = GetInt(root, "valid");
            snapshot.WarningRowCount = GetInt(root, "warnings");
            snapshot.ErrorRowCount = GetInt(root, "errors");
            snapshot.OutOfPeriodCount = GetInt(root, "out_of_period");
            snapshot.IsBalanced = root.TryGetProperty("balanced", out var balanced) && balanced.GetBoolean();
            if (root.TryGetProperty("first_transaction_date", out var first) && first.ValueKind == JsonValueKind.String)
            {
                snapshot.FirstTransactionDate = first.GetString();
            }

            if (root.TryGetProperty("last_transaction_date", out var last) && last.ValueKind == JsonValueKind.String)
            {
                snapshot.LastTransactionDate = last.GetString();
            }

            if (root.TryGetProperty("totals", out var totals))
            {
                snapshot.TotalDebitMinor = GetLong(totals, "debit_minor");
                snapshot.TotalCreditMinor = GetLong(totals, "credit_minor");
                snapshot.TotalBalanceMinor = GetLong(totals, "balance_minor");
            }

            if (root.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            {
                var list = new List<ImportIssue>();
                foreach (var issue in issues.EnumerateArray())
                {
                    list.Add(new ImportIssue(
                        issue.TryGetProperty("severity", out var severity) ? severity.GetString() ?? IssueSeverity.Info : IssueSeverity.Info,
                        issue.TryGetProperty("code", out var code) ? code.GetString() ?? string.Empty : string.Empty,
                        issue.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                        issue.TryGetProperty("row", out var row) && row.ValueKind == JsonValueKind.Number ? row.GetInt32() : null,
                        issue.TryGetProperty("column", out var column) && column.ValueKind == JsonValueKind.String ? column.GetString() : null));
                }

                snapshot.Issues = list;
            }

            if (root.TryGetProperty("issue_counts", out var counts) && counts.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var property in counts.EnumerateObject())
                {
                    map[property.Name] = property.Value.GetInt32();
                }

                snapshot.IssueCounts = map;
            }
        }
        catch (JsonException)
        {
            // A malformed historical report must never break a page.
            return new ImportValidationReportSnapshot();
        }

        return snapshot;
    }

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}

/// <summary>Read-only view of a persisted validation report.</summary>
public sealed class ImportValidationReportSnapshot
{
    public int RowCount { get; set; }

    public int ValidRowCount { get; set; }

    public int WarningRowCount { get; set; }

    public int ErrorRowCount { get; set; }

    public int OutOfPeriodCount { get; set; }

    public long TotalDebitMinor { get; set; }

    public long TotalCreditMinor { get; set; }

    public long TotalBalanceMinor { get; set; }

    public bool IsBalanced { get; set; }

    public string? FirstTransactionDate { get; set; }

    public string? LastTransactionDate { get; set; }

    public IReadOnlyList<ImportIssue> Issues { get; set; } = Array.Empty<ImportIssue>();

    public IReadOnlyDictionary<string, int> IssueCounts { get; set; } = new Dictionary<string, int>();

    public long DifferenceMinor => TotalDebitMinor - TotalCreditMinor;
}
