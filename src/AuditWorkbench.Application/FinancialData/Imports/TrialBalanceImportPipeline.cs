using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;

namespace AuditWorkbench.Application.FinancialData.Imports;

/// <summary>An account master row already present in the engagement.</summary>
public sealed record KnownAccount(Guid AccountId, string AccountCode, string AccountName, string? AccountGroup);

/// <summary>An account that the TB import must create before its lines can reference it.</summary>
public sealed record NewAccountRequest(string AccountCode, string AccountName, string NormalizedCode, string? AccountGroup);

/// <summary>Everything the TB pipeline needs to judge a row (no database access while streaming).</summary>
public sealed class TbImportContext
{
    public required Guid EngagementId { get; init; }

    public required Guid FinancialPeriodId { get; init; }

    public required string CurrencyCode { get; init; }

    public required int MinorUnitScale { get; init; }

    public required DateOnly PeriodStart { get; init; }

    public required DateOnly PeriodEnd { get; init; }

    public required IReadOnlyDictionary<string, KnownAccount> AccountsByNormalizedCode { get; init; }

    /// <summary>Operator override: an unbalanced TB is reported as a warning instead of blocking.</summary>
    public bool AllowUnbalanced { get; init; }
}

/// <summary>A normalized trial-balance row that passed validation and can be committed.</summary>
public sealed record PreparedTbRow(
    int LineNo,
    int SourceRowNo,
    string AccountCode,
    string AccountName,
    string NormalizedCode,
    long DebitMinor,
    long CreditMinor,
    long BalanceMinor,
    string CurrencyCode,
    string? CostCenter,
    string? AccountGroup,
    string ExtraColumnsJson,
    string RowHash);

/// <summary>Result of one full validation pass over a TB file.</summary>
public sealed record TbValidationPass(
    ImportValidationReport Report,
    IReadOnlyList<NewAccountRequest> NewAccounts,
    int SourceRowCount);

/// <summary>
/// Trial-balance import rules. Validation is a pure streaming pass: it counts
/// every row, keeps a bounded sample of findings and never discards a row
/// silently - a row is either valid or reported with its source row number.
/// </summary>
public static class TrialBalanceImportPipeline
{
    public static TbValidationPass Validate(
        IFinancialDatasetSource source,
        ITabularFileReader reader,
        ImportStructureInfo structure,
        ImportColumnMapping mapping,
        TbImportContext context,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new ImportValidationReport(FinancialDatasetKind.TrialBalance);
        var seenRows = new Dictionary<string, int>(StringComparer.Ordinal);
        var newAccounts = new Dictionary<string, NewAccountRequest>(StringComparer.Ordinal);
        var sourceRowCount = 0;

        foreach (var row in reader.ReadRows(source, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceRowCount++;
            if (row.IsEmpty)
            {
                continue;
            }

            if (ImportRowSupport.IsBeforeData(structure, row))
            {
                continue;
            }

            var result = Evaluate(row, structure, mapping, context, report);
            if (result.Skip)
            {
                continue;
            }

            var duplicate = false;
            if (!result.HasError)
            {
                if (seenRows.TryGetValue(result.NormalizedCode, out var firstRow))
                {
                    report.AddError(ImportIssueCodes.DuplicateAccountCode,
                        $"Account {result.AccountCode} also appears on row {firstRow}. A trial balance must contain " +
                        "one line per account.", row.RowNumber, "Account code");
                    duplicate = true;
                }
                else
                {
                    seenRows[result.NormalizedCode] = row.RowNumber;
                    if (!context.AccountsByNormalizedCode.ContainsKey(result.NormalizedCode) &&
                        newAccounts.TryAdd(result.NormalizedCode, new NewAccountRequest(
                            result.AccountCode,
                            result.AccountName.Length == 0 ? result.AccountCode : result.AccountName,
                            result.NormalizedCode,
                            result.AccountGroup)))
                    {
                        report.AddInfo(ImportIssueCodes.NewAccountCreated,
                            $"Account {result.AccountCode} is new and will be added to the account master.",
                            row.RowNumber);
                    }
                }

                if (!duplicate && context.AccountsByNormalizedCode.TryGetValue(result.NormalizedCode, out var known) &&
                    known.AccountName.Length > 0 && result.AccountName.Length > 0 &&
                    !string.Equals(known.AccountName, result.AccountName, StringComparison.OrdinalIgnoreCase))
                {
                    report.AddInfo(ImportIssueCodes.AccountNameChanged,
                        $"Row {row.RowNumber}: the file names {result.AccountCode} '{result.AccountName}' while the " +
                        $"account master has '{known.AccountName}'. The imported line keeps the file value.",
                        row.RowNumber, "Account name");
                }
            }

            report.RegisterRow(result.HasError, result.HasWarning, outOfPeriod: false,
                result.DebitMinor, result.CreditMinor, result.BalanceMinor, transactionDate: null);

            if (progress is not null && sourceRowCount % FinancialImportLimits.InsertBatchSize == 0)
            {
                progress(sourceRowCount);
            }
        }

        if (!report.IsBalanced)
        {
            var message =
                $"Total debits {report.TotalDebitMinor} and total credits {report.TotalCreditMinor} differ by " +
                $"{report.DifferenceMinor} minor units.";
            if (context.AllowUnbalanced)
            {
                report.AddWarning(ImportIssueCodes.UnbalancedTrialBalance,
                    message + " The trial balance was imported as an explicitly confirmed unbalanced draft.");
            }
            else
            {
                report.Add(IssueSeverity.Error, ImportIssueCodes.UnbalancedTrialBalance,
                    message + " Correct the file, or confirm explicitly that you want to import an unbalanced draft.");
            }
        }

        progress?.Invoke(sourceRowCount);
        return new TbValidationPass(report, newAccounts.Values.ToList(), sourceRowCount);
    }

    /// <summary>
    /// Second streaming pass used inside the transaction: rows are re-evaluated and
    /// yielded for insertion. A blocking finding here aborts the whole import.
    /// </summary>
    public static IEnumerable<PreparedTbRow> PrepareRows(
        IFinancialDatasetSource source,
        ITabularFileReader reader,
        ImportStructureInfo structure,
        ImportColumnMapping mapping,
        TbImportContext context,
        CancellationToken cancellationToken = default)
    {
        // A dedicated report for this pass: it must not change the validation report
        // stored as provenance, but an unexpected error still stops the import.
        var report = new ImportValidationReport(FinancialDatasetKind.TrialBalance);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lineNo = 0;

        foreach (var row in reader.ReadRows(source, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.IsEmpty)
            {
                continue;
            }

            if (ImportRowSupport.IsBeforeData(structure, row))
            {
                continue;
            }

            var result = Evaluate(row, structure, mapping, context, report);
            if (result.Skip)
            {
                continue;
            }

            if (result.HasError)
            {
                throw new ValidationException(
                    $"Row {row.RowNumber} of '{source.FileName}' failed validation during the import pass. " +
                    "Nothing was written; correct the file and import it again.");
            }

            lineNo++;
            if (!seen.Add(result.NormalizedCode))
            {
                throw new ValidationException(
                    $"Account {result.AccountCode} appears more than once in '{source.FileName}'. Nothing was written.");
            }

            yield return new PreparedTbRow(
                lineNo,
                row.RowNumber,
                result.AccountCode,
                result.AccountName,
                result.NormalizedCode,
                result.DebitMinor,
                result.CreditMinor,
                result.BalanceMinor,
                result.CurrencyCode,
                result.CostCenter,
                result.AccountGroup,
                result.ExtraColumnsJson,
                result.RowHash);
        }
    }

    private static TbRowResult Evaluate(TabularRow row, ImportStructureInfo structure, ImportColumnMapping mapping,
        TbImportContext context, ImportValidationReport report)
    {
        var accountCodeColumn = mapping.ColumnIndex(TbFields.AccountCode) ?? 0;
        var accountCode = ImportValueParser.Text(mapping.Value(row.Cells, TbFields.AccountCode));

        if (accountCode.Length == 0)
        {
            if (row.Cells.All(ImportValueParser.IsBlank))
            {
                return TbRowResult.Skipped;
            }

            if (ImportRowSupport.IsTotalsRow(row, accountCodeColumn))
            {
                report.AddInfo(ImportIssueCodes.ZeroAmountRow,
                    $"Row {row.RowNumber} looks like a totals row and was not imported as data.", row.RowNumber);
                return TbRowResult.Skipped;
            }

            report.AddError(ImportIssueCodes.MissingAccountCode,
                "The row has no account code.", row.RowNumber, "Account code");
            return TbRowResult.Error();
        }

        if (ImportRowSupport.IsTotalsRow(row, accountCodeColumn))
        {
            report.AddInfo(ImportIssueCodes.ZeroAmountRow,
                $"Row {row.RowNumber} looks like a totals row and was not imported as data.", row.RowNumber);
            return TbRowResult.Skipped;
        }

        var result = new TbRowResult
        {
            AccountCode = accountCode,
            NormalizedCode = Account.NormalizeCode(accountCode),
            AccountName = ImportRowSupport.Truncate(mapping.Value(row.Cells, TbFields.AccountName),
                ImportRowSupport.MaxNameLength, report, "account name", row.RowNumber),
            CostCenter = Blank(mapping.Value(row.Cells, TbFields.CostCenter), 64),
            AccountGroup = Blank(mapping.Value(row.Cells, TbFields.AccountGroup), 64),
            CurrencyCode = context.CurrencyCode,
            ExtraColumnsJson = ImportRowSupport.BuildExtraColumnsJson(structure, row, mapping),
        };

        if (result.AccountCode.Length > 32)
        {
            report.AddError(ImportIssueCodes.LongAccountCode,
                $"Account code '{result.AccountCode}' is longer than the 32 characters this workspace accepts.",
                row.RowNumber, "Account code");
            result.HasError = true;
        }

        if (result.AccountName.Length == 0 && mapping.Has(TbFields.AccountName))
        {
            report.AddWarning(ImportIssueCodes.MissingAccountName,
                $"Row {row.RowNumber}: account {result.AccountCode} has no description in the file.",
                row.RowNumber, "Account name");
            result.HasWarning = true;
        }

        var debitRaw = mapping.Value(row.Cells, TbFields.Debit);
        var creditRaw = mapping.Value(row.Cells, TbFields.Credit);
        var balanceRaw = mapping.Value(row.Cells, TbFields.Balance);
        var hasDebit = !ImportValueParser.IsBlank(debitRaw);
        var hasCredit = !ImportValueParser.IsBlank(creditRaw);
        var hasBalance = !ImportValueParser.IsBlank(balanceRaw);

        if (!hasDebit && !hasCredit && !hasBalance)
        {
            report.AddError(ImportIssueCodes.MissingValueColumn,
                "The row has no debit, credit or balance value.", row.RowNumber);
            result.HasError = true;
            return result;
        }

        if (hasDebit)
        {
            if (!ImportValueParser.TryParseAmount(debitRaw, context.MinorUnitScale, out var debit))
            {
                report.AddError(ImportIssueCodes.InvalidAmount,
                    $"'{ImportValueParser.Text(debitRaw)}' is not a valid debit amount.", row.RowNumber, "Debit");
                result.HasError = true;
            }
            else if (debit < 0)
            {
                report.AddError(ImportIssueCodes.NegativeAmount,
                    $"Debit {ImportValueParser.Text(debitRaw)} on account {result.AccountCode} is negative; use the " +
                    "credit column instead.", row.RowNumber, "Debit");
                result.HasError = true;
            }
            else
            {
                result.DebitMinor = debit;
            }
        }

        if (hasCredit)
        {
            if (!ImportValueParser.TryParseAmount(creditRaw, context.MinorUnitScale, out var credit))
            {
                report.AddError(ImportIssueCodes.InvalidAmount,
                    $"'{ImportValueParser.Text(creditRaw)}' is not a valid credit amount.", row.RowNumber, "Credit");
                result.HasError = true;
            }
            else if (credit < 0)
            {
                report.AddError(ImportIssueCodes.NegativeAmount,
                    $"Credit {ImportValueParser.Text(creditRaw)} on account {result.AccountCode} is negative; use the " +
                    "debit column instead.", row.RowNumber, "Credit");
                result.HasError = true;
            }
            else
            {
                result.CreditMinor = credit;
            }
        }

        if (result.DebitMinor > 0 && result.CreditMinor > 0)
        {
            report.AddError(ImportIssueCodes.BothDebitAndCredit,
                $"Account {result.AccountCode} reports both a debit and a credit on one line; a trial-balance line " +
                "must be one-sided.", row.RowNumber);
            result.HasError = true;
        }

        result.BalanceMinor = result.DebitMinor - result.CreditMinor;

        if (hasBalance && ImportValueParser.TryParseAmount(balanceRaw, context.MinorUnitScale, out var balance))
        {
            if ((hasDebit || hasCredit) && balance != result.BalanceMinor)
            {
                report.AddWarning(ImportIssueCodes.BalanceMismatch,
                    $"Row {row.RowNumber} ({result.AccountCode}): the file balance '{ImportValueParser.Text(balanceRaw)}' " +
                    $"does not equal debit - credit. The imported balance is debit - credit in minor units " +
                    $"({result.BalanceMinor}); the file value is preserved in the validation report.",
                    row.RowNumber, "Balance");
                result.HasWarning = true;
            }
            else if (!hasDebit && !hasCredit)
            {
                // The file supplies only a signed balance: the sign decides the side.
                if (balance >= 0)
                {
                    result.DebitMinor = balance;
                }
                else
                {
                    result.CreditMinor = -balance;
                }

                result.BalanceMinor = balance;
            }
        }
        else if (hasBalance)
        {
            report.AddError(ImportIssueCodes.InvalidAmount,
                $"'{ImportValueParser.Text(balanceRaw)}' is not a valid balance amount.", row.RowNumber, "Balance");
            result.HasError = true;
        }

        var currencyRaw = mapping.Value(row.Cells, TbFields.Currency);
        if (!ImportValueParser.IsBlank(currencyRaw))
        {
            var currency = ImportValueParser.Text(currencyRaw).ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsLetter))
            {
                report.AddError(ImportIssueCodes.UnsupportedCurrency,
                    $"'{ImportValueParser.Text(currencyRaw)}' is not a three-letter currency code.",
                    row.RowNumber, "Currency");
                result.HasError = true;
            }
            else if (!string.Equals(currency, context.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                report.AddError(ImportIssueCodes.UnsupportedCurrency,
                    $"Row {row.RowNumber} is in {currency} but this engagement reports in {context.CurrencyCode}. " +
                    "Amounts are never converted silently; import a file in the engagement currency.",
                    row.RowNumber, "Currency");
                result.HasError = true;
            }
            else
            {
                result.CurrencyCode = currency;
            }
        }

        if (!result.HasError && result.DebitMinor == 0 && result.CreditMinor == 0 && result.BalanceMinor == 0)
        {
            report.AddInfo(ImportIssueCodes.ZeroAmountRow,
                $"Account {result.AccountCode} has no activity in this trial balance.", row.RowNumber);
        }

        result.RowHash = TransactionIdentity.RowHash(result.AccountCode, result.DebitMinor, result.CreditMinor,
            result.BalanceMinor, result.CurrencyCode);
        return result;
    }

    private static string? Blank(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class TbRowResult
    {
        public static readonly TbRowResult Skipped = new() { Skip = true };

        public bool Skip { get; private init; }

        public bool HasError { get; set; }

        public bool HasWarning { get; set; }

        public string AccountCode { get; init; } = string.Empty;

        public string AccountName { get; init; } = string.Empty;

        public string NormalizedCode { get; init; } = string.Empty;

        public string CurrencyCode { get; set; } = string.Empty;

        public string? CostCenter { get; init; }

        public string? AccountGroup { get; init; }

        public string ExtraColumnsJson { get; init; } = "{}";

        public long DebitMinor { get; set; }

        public long CreditMinor { get; set; }

        public long BalanceMinor { get; set; }

        public string RowHash { get; set; } = string.Empty;

        public static TbRowResult Error() => new() { HasError = true };
    }
}
