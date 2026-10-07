using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;

namespace AuditWorkbench.Application.FinancialData.Imports;

/// <summary>Everything the GL pipeline needs to judge a row (no database access while streaming).</summary>
public sealed class GlImportContext
{
    public required Guid EngagementId { get; init; }

    public required Guid FinancialPeriodId { get; init; }

    public required string CurrencyCode { get; init; }

    public required int MinorUnitScale { get; init; }

    public required DateOnly PeriodStart { get; init; }

    public required DateOnly PeriodEnd { get; init; }

    /// <summary>Account master of the engagement, keyed by normalized account code.</summary>
    public required IReadOnlyDictionary<string, KnownAccount> AccountsByNormalizedCode { get; init; }

    /// <summary>Operator override: an unbalanced ledger extract is reported as a warning.</summary>
    public bool AllowUnbalanced { get; init; }
}

/// <summary>Journal-level metadata collected while streaming; written before its lines.</summary>
public sealed record GlJournalAttributes(
    string JournalIdentity,
    string IdentitySource,
    string? JournalNumber,
    string? JournalSource,
    string? PostingDate,
    string? Reference,
    string? Description,
    string? CurrencyCode,
    string? PreparedBy,
    string JournalHash,
    int LineCount,
    string? FirstTransactionDate);

/// <summary>A normalized ledger line that passed validation and can be committed.</summary>
public sealed record PreparedGlLine(
    int LineNo,
    int SourceRowNo,
    string JournalIdentity,
    string? SourceLineNo,
    string LineIdentity,
    string IdentitySource,
    string AccountCode,
    string? AccountName,
    Guid? AccountId,
    string TransactionDate,
    string? PostingDate,
    string Description,
    long DebitMinor,
    long CreditMinor,
    string? CurrencyCode,
    string? JournalSource,
    string? Reference,
    string? PreparedBy,
    bool IsOutOfPeriod,
    string LineHash,
    string ValueHash,
    string AttributeHash,
    string ExtraColumnsJson);

/// <summary>Result of one full validation pass over a GL file.</summary>
public sealed record GlValidationPass(
    ImportValidationReport Report,
    IReadOnlyList<GlJournalAttributes> Journals,
    IReadOnlyList<string> UnmatchedAccountCodes,
    int SourceRowCount);

/// <summary>
/// General-ledger import rules.
/// <para>
/// Transaction identity: when the source supplies a journal/document/voucher
/// number it is the identity. When it does not, consecutive lines that share the
/// same journal attributes (source, date, reference, description, currency,
/// preparer) form a derived journal whose identity is a deterministic hash of
/// those attributes; the line identity is then the line's ordinal in that
/// journal. Validation reports that derivation so the limitation is visible.
/// </para>
/// </summary>
public static class GeneralLedgerImportPipeline
{
    public static GlValidationPass Validate(
        IFinancialDatasetSource source,
        ITabularFileReader reader,
        ImportStructureInfo structure,
        ImportColumnMapping mapping,
        GlImportContext context,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new ImportValidationReport(FinancialDatasetKind.GeneralLedger);
        var journals = new Dictionary<string, JournalBuilder>(StringComparer.Ordinal);
        var unmatched = new SortedSet<string>(StringComparer.Ordinal);
        var state = new StreamingState();

        if (!mapping.Has(GlFields.JournalNumber))
        {
            report.AddWarning(ImportIssueCodes.DerivedTransactionIdentity,
                "No journal/document/voucher number column is mapped. Journal identity is derived from the journal " +
                "attributes and line identity from the line order; this keeps snapshots comparable but is less exact " +
                "than a client key. Map a journal number column when the export provides one.");
        }

        var sourceRowCount = RunPass(source, reader, structure, mapping, context, report, journals, unmatched, state,
            progress, cancellationToken, out var finalReport);

        if (!finalReport.IsBalanced)
        {
            var message =
                $"Total debits {finalReport.TotalDebitMinor} and total credits {finalReport.TotalCreditMinor} differ " +
                $"by {finalReport.DifferenceMinor} minor units.";
            report.AddWarning(ImportIssueCodes.UnbalancedLedger,
                context.AllowUnbalanced
                    ? message + " The ledger was imported as an explicitly confirmed unbalanced extract."
                    : message + " A ledger extract that does not balance is imported and flagged for review.");
        }

        return new GlValidationPass(
            report,
            journals.Values.Select(builder => builder.ToAttributes()).ToList(),
            unmatched.ToList(),
            sourceRowCount);
    }

    /// <summary>
    /// Second streaming pass used inside the transaction. Journal identity and
    /// line identity are recomputed with exactly the same rules, so the same file
    /// always produces the same identities.
    /// </summary>
    public static IEnumerable<PreparedGlLine> PrepareLines(
        IFinancialDatasetSource source,
        ITabularFileReader reader,
        ImportStructureInfo structure,
        ImportColumnMapping mapping,
        GlImportContext context,
        IReadOnlyDictionary<string, Guid> journalIds,
        CancellationToken cancellationToken = default)
    {
        var report = new ImportValidationReport(FinancialDatasetKind.GeneralLedger);
        var journals = new Dictionary<string, JournalBuilder>(StringComparer.Ordinal);
        var unmatched = new SortedSet<string>(StringComparer.Ordinal);
        var state = new StreamingState();
        var lineNo = 0;

        foreach (var row in reader.ReadRows(source, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.IsEmpty)
            {
                continue;
            }

            var result = Evaluate(row, structure, mapping, context, report, state, journals, unmatched);
            if (result.Skip)
            {
                continue;
            }

            if (result.HasError)
            {
                throw new Domain.Common.ValidationException(
                    $"Row {row.RowNumber} of '{source.FileName}' failed validation during the import pass. " +
                    "Nothing was written; correct the file and import it again.");
            }

            lineNo++;
            if (!journalIds.TryGetValue(result.JournalIdentity, out var journalId))
            {
                throw new Domain.Common.ValidationException(
                    $"Journal {result.JournalIdentity} of '{source.FileName}' was not identified during validation. " +
                    "Nothing was written.");
            }

            yield return new PreparedGlLine(
                lineNo,
                row.RowNumber,
                result.JournalIdentity,
                result.SourceLineNo,
                result.LineIdentity,
                result.LineIdentitySource,
                result.AccountCode,
                result.AccountName,
                result.AccountId,
                result.TransactionDate,
                result.PostingDate,
                result.Description,
                result.DebitMinor,
                result.CreditMinor,
                result.CurrencyCode,
                result.JournalSource,
                result.Reference,
                result.PreparedBy,
                result.IsOutOfPeriod,
                result.LineHash,
                result.ValueHash,
                result.AttributeHash,
                result.ExtraColumnsJson);
        }
    }

    private static int RunPass(
        IFinancialDatasetSource source,
        ITabularFileReader reader,
        ImportStructureInfo structure,
        ImportColumnMapping mapping,
        GlImportContext context,
        ImportValidationReport report,
        Dictionary<string, JournalBuilder> journals,
        SortedSet<string> unmatched,
        StreamingState state,
        Action<int>? progress,
        CancellationToken cancellationToken,
        out ImportValidationReport finalReport)
    {
        var sourceRowCount = 0;
        foreach (var row in reader.ReadRows(source, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceRowCount++;
            if (row.IsEmpty)
            {
                continue;
            }

            var result = Evaluate(row, structure, mapping, context, report, state, journals, unmatched);
            if (result.Skip)
            {
                continue;
            }

            report.RegisterRow(result.HasError, result.HasWarning, result.IsOutOfPeriod, result.DebitMinor,
                result.CreditMinor, balanceMinor: null, transactionDate: result.TransactionDate);

            if (!result.HasError)
            {
                if (!journals.TryGetValue(result.JournalIdentity, out var builder))
                {
                    builder = new JournalBuilder(result);
                    journals[result.JournalIdentity] = builder;
                }

                builder.AddLine(result.TransactionDate, result.JournalHash);
            }

            if (progress is not null && sourceRowCount % FinancialImportLimits.InsertBatchSize == 0)
            {
                progress(sourceRowCount);
            }
        }

        progress?.Invoke(sourceRowCount);
        finalReport = report;
        return sourceRowCount;
    }

    private static GlRowResult Evaluate(TabularRow row, ImportStructureInfo structure, ImportColumnMapping mapping,
        GlImportContext context, ImportValidationReport report, StreamingState state,
        Dictionary<string, JournalBuilder> journals, SortedSet<string> unmatched)
    {
        var accountCode = ImportValueParser.Text(mapping.Value(row.Cells, GlFields.AccountCode));
        if (accountCode.Length == 0)
        {
            if (row.Cells.All(ImportValueParser.IsBlank))
            {
                return GlRowResult.Skipped;
            }

            if (ImportRowSupport.IsTotalsRow(row, mapping.ColumnIndex(GlFields.AccountCode) ?? 0))
            {
                report.AddInfo(ImportIssueCodes.ZeroAmountRow,
                    $"Row {row.RowNumber} looks like a totals row and was not imported as data.", row.RowNumber);
                return GlRowResult.Skipped;
            }

            report.AddError(ImportIssueCodes.MissingAccountCode, "The row has no account code.", row.RowNumber,
                "Account code");
            state.CloseDerivedJournalRun();
            return GlRowResult.Error();
        }

        if (ImportRowSupport.IsTotalsRow(row, mapping.ColumnIndex(GlFields.AccountCode) ?? 0))
        {
            report.AddInfo(ImportIssueCodes.ZeroAmountRow,
                $"Row {row.RowNumber} looks like a totals row and was not imported as data.", row.RowNumber);
            return GlRowResult.Skipped;
        }

        var result = new GlRowResult
        {
            AccountCode = accountCode,
            NormalizedCode = Account.NormalizeCode(accountCode),
            AccountName = Blank(mapping.Value(row.Cells, GlFields.AccountName), ImportRowSupport.MaxNameLength),
            JournalSource = Blank(mapping.Value(row.Cells, GlFields.JournalSource), 64),
            Reference = Blank(mapping.Value(row.Cells, GlFields.Reference), 120),
            Description = ImportRowSupport.Truncate(mapping.Value(row.Cells, GlFields.Description),
                ImportRowSupport.MaxDescriptionLength, report, "description", row.RowNumber),
            PreparedBy = Blank(mapping.Value(row.Cells, GlFields.Preparer), 120),
            SourceLineNo = Blank(mapping.Value(row.Cells, GlFields.LineNumber), 40),
            ExtraColumnsJson = ImportRowSupport.BuildExtraColumnsJson(structure, row, mapping),
        };

        var postDateRaw = mapping.Value(row.Cells, GlFields.PostingDate);
        var dateRaw = mapping.Value(row.Cells, GlFields.TransactionDate);
        if (ImportValueParser.IsBlank(dateRaw) && !ImportValueParser.IsBlank(postDateRaw))
        {
            dateRaw = postDateRaw;
        }

        if (ImportValueParser.IsBlank(dateRaw))
        {
            report.AddError(ImportIssueCodes.MissingTransactionDate,
                "The row has no transaction date and no posting date.", row.RowNumber, "Transaction date");
            result.HasError = true;
            state.CloseDerivedJournalRun();
            return result;
        }

        if (!ImportValueParser.TryParseDate(dateRaw, out var transactionDate))
        {
            report.AddError(ImportIssueCodes.InvalidTransactionDate,
                $"'{ImportValueParser.Text(dateRaw)}' is not a readable date.", row.RowNumber, "Transaction date");
            result.HasError = true;
            state.CloseDerivedJournalRun();
            return result;
        }

        result.TransactionDate = transactionDate;

        if (!ImportValueParser.IsBlank(postDateRaw))
        {
            if (ImportValueParser.TryParseDate(postDateRaw, out var postingDate))
            {
                result.PostingDate = postingDate;
            }
            else
            {
                report.AddWarning(ImportIssueCodes.InvalidPostingDate,
                    $"Row {row.RowNumber}: posting date '{ImportValueParser.Text(postDateRaw)}' is not a readable date " +
                    "and was left empty on the imported line.", row.RowNumber, "Posting date");
                result.HasWarning = true;
            }
        }

        var periodStart = context.PeriodStart.ToString("yyyy-MM-dd");
        var periodEnd = context.PeriodEnd.ToString("yyyy-MM-dd");
        if (string.CompareOrdinal(transactionDate, periodStart) < 0 ||
            string.CompareOrdinal(transactionDate, periodEnd) > 0)
        {
            result.IsOutOfPeriod = true;
            report.AddWarning(ImportIssueCodes.OutOfPeriod,
                $"Row {row.RowNumber}: transaction date {transactionDate} is outside the financial period " +
                $"{periodStart} to {periodEnd}. The transaction is imported and flagged; it is never moved or deleted.",
                row.RowNumber, "Transaction date");
            result.HasWarning = true;
        }

        var debitRaw = mapping.Value(row.Cells, GlFields.Debit);
        var creditRaw = mapping.Value(row.Cells, GlFields.Credit);
        var amountRaw = mapping.Value(row.Cells, GlFields.Amount);
        var hasDebit = !ImportValueParser.IsBlank(debitRaw);
        var hasCredit = !ImportValueParser.IsBlank(creditRaw);
        var hasAmount = !ImportValueParser.IsBlank(amountRaw);

        if (!hasDebit && !hasCredit && !hasAmount)
        {
            report.AddError(ImportIssueCodes.MissingValueColumn,
                "The row has no debit, credit or amount value.", row.RowNumber);
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
                    $"Debit {ImportValueParser.Text(debitRaw)} is negative; use the credit column instead.",
                    row.RowNumber, "Debit");
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
                    $"Credit {ImportValueParser.Text(creditRaw)} is negative; use the debit column instead.",
                    row.RowNumber, "Credit");
                result.HasError = true;
            }
            else
            {
                result.CreditMinor = credit;
            }
        }

        if (!hasDebit && !hasCredit && hasAmount)
        {
            // Signed amount column: debit-positive, exactly as documented.
            if (!ImportValueParser.TryParseAmount(amountRaw, context.MinorUnitScale, out var amount))
            {
                report.AddError(ImportIssueCodes.InvalidAmount,
                    $"'{ImportValueParser.Text(amountRaw)}' is not a valid amount.", row.RowNumber, "Amount");
                result.HasError = true;
            }
            else if (amount >= 0)
            {
                result.DebitMinor = amount;
            }
            else
            {
                result.CreditMinor = -amount;
            }
        }

        if (result.DebitMinor > 0 && result.CreditMinor > 0)
        {
            report.AddError(ImportIssueCodes.BothDebitAndCredit,
                $"Row {row.RowNumber} reports both a debit and a credit; a ledger line must be one-sided.",
                row.RowNumber);
            result.HasError = true;
        }

        var currencyRaw = mapping.Value(row.Cells, GlFields.Currency);
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
                    "Amounts are never converted silently; import an extract in the engagement currency.",
                    row.RowNumber, "Currency");
                result.HasError = true;
            }
            else
            {
                result.CurrencyCode = currency;
            }
        }

        // -- transaction identity ----------------------------------------------
        var journalNumber = Blank(mapping.Value(row.Cells, GlFields.JournalNumber), 120);
        if (journalNumber is not null)
        {
            result.JournalIdentity = TransactionIdentity.FromSourceKey(journalNumber);
            result.IdentitySource = ImportIdentitySource.Source;
            result.JournalNumber = journalNumber;
            state.CloseDerivedJournalRun();
        }
        else
        {
            var attributes = new[]
            {
                result.JournalSource, result.TransactionDate, result.PostingDate, result.Reference, result.Description,
                result.CurrencyCode, result.PreparedBy,
            };
            result.JournalIdentity = state.DerivedJournalIdentity(attributes);
            result.IdentitySource = ImportIdentitySource.Derived;
        }

        // -- line identity ------------------------------------------------------
        var ordinal = state.NextLineOrdinal(result.JournalIdentity);
        if (result.SourceLineNo is not null)
        {
            result.LineIdentity = TransactionIdentity.FromSourceLineKey(result.SourceLineNo);
            result.LineIdentitySource = ImportIdentitySource.Source;
        }
        else
        {
            result.LineIdentity = TransactionIdentity.DeriveLineKey(ordinal);
            result.LineIdentitySource = ImportIdentitySource.Derived;
        }

        if (result.LineIdentitySource == ImportIdentitySource.Source &&
            !state.RegisterLineIdentity(result.JournalIdentity, result.LineIdentity))
        {
            report.AddError(ImportIssueCodes.DuplicateTransaction,
                $"Journal {result.JournalNumber ?? result.JournalIdentity} already contains line " +
                $"{result.SourceLineNo} in this file. A transaction identity must be unique inside one import.",
                row.RowNumber, "Line number");
            result.HasError = true;
            return result;
        }

        // -- account link -------------------------------------------------------
        if (context.AccountsByNormalizedCode.TryGetValue(result.NormalizedCode, out var known))
        {
            result.AccountId = known.AccountId;
            if (result.AccountName is null && known.AccountName.Length > 0)
            {
                result.AccountName = known.AccountName;
            }
        }
        else
        {
            unmatched.Add(result.AccountCode);
            if (context.AccountsByNormalizedCode.Count > 0)
            {
                report.AddWarning(ImportIssueCodes.InvalidAccountReference,
                    $"Row {row.RowNumber}: account {result.AccountCode} is not in the trial balance of this period. " +
                    "The line keeps its source code without an account link and is reported in the TB/GL " +
                    "reconciliation as a ledger account missing from the TB.",
                    row.RowNumber, "Account code");
                result.HasWarning = true;
            }
        }

        // -- digests ------------------------------------------------------------
        result.AttributeHash = TransactionIdentity.AttributeHash(
            result.AccountCode, result.TransactionDate, result.PostingDate, result.JournalSource, result.Reference,
            result.CurrencyCode, result.Description, result.PreparedBy);
        result.ValueHash = TransactionIdentity.ValueHash(result.DebitMinor, result.CreditMinor);
        result.LineHash = TransactionIdentity.HashParts(new[]
        {
            result.JournalIdentity, result.LineIdentity, result.AttributeHash, result.ValueHash,
        });
        result.JournalHash = TransactionIdentity.HashParts(new[]
        {
            result.JournalNumber, result.JournalSource, result.PostingDate, result.Reference, result.Description,
            result.CurrencyCode, result.PreparedBy,
        });

        if (journals.TryGetValue(result.JournalIdentity, out var existing) &&
            !existing.MatchesJournalHash(result.JournalHash) &&
            state.MarkJournalAttributeInconsistency(result.JournalIdentity))
        {
            report.AddWarning(ImportIssueCodes.DuplicateTransaction,
                $"Journal {result.JournalNumber ?? result.JournalIdentity} appears with different journal-level " +
                "attributes (posting date, reference, source or description). Check the export for merged or " +
                "duplicated journal numbers.", row.RowNumber);
            result.HasWarning = true;
        }

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

    /// <summary>
    /// Streaming identity state. Memory is bounded by the number of journals (and
    /// by the number of client line numbers), never by the number of rows.
    /// </summary>
    private sealed class StreamingState
    {
        private readonly Dictionary<string, int> _derivedTwinOrdinals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _lineOrdinals = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sourceLineIdentities = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inconsistentJournals = new(StringComparer.Ordinal);

        private bool _derivedRunOpen;
        private string? _derivedTupleKey;
        private string? _derivedJournalIdentity;

        public void CloseDerivedJournalRun()
        {
            _derivedRunOpen = false;
            _derivedTupleKey = null;
            _derivedJournalIdentity = null;
        }

        public string DerivedJournalIdentity(IReadOnlyList<string?> attributes)
        {
            var tupleKey = string.Join('\u001f', attributes.Select(value => value ?? string.Empty));
            if (_derivedRunOpen && string.Equals(_derivedTupleKey, tupleKey, StringComparison.Ordinal))
            {
                return _derivedJournalIdentity!;
            }

            _derivedTwinOrdinals.TryGetValue(tupleKey, out var twinOrdinal);
            twinOrdinal++;
            _derivedTwinOrdinals[tupleKey] = twinOrdinal;

            _derivedRunOpen = true;
            _derivedTupleKey = tupleKey;
            _derivedJournalIdentity = TransactionIdentity.DeriveJournalKey(attributes, twinOrdinal);
            return _derivedJournalIdentity;
        }

        public int NextLineOrdinal(string journalIdentity)
        {
            _lineOrdinals.TryGetValue(journalIdentity, out var ordinal);
            ordinal++;
            _lineOrdinals[journalIdentity] = ordinal;
            return ordinal;
        }

        public bool RegisterLineIdentity(string journalIdentity, string lineIdentity) =>
            _sourceLineIdentities.Add(journalIdentity + '\u001f' + lineIdentity);

        public bool MarkJournalAttributeInconsistency(string journalIdentity) =>
            _inconsistentJournals.Add(journalIdentity);
    }

    private sealed class JournalBuilder
    {
        private readonly GlRowResult _first;

        public JournalBuilder(GlRowResult first)
        {
            _first = first;
            FirstTransactionDate = first.TransactionDate;
            JournalHash = first.JournalHash;
        }

        public int LineCount { get; private set; }

        public string? FirstTransactionDate { get; private set; }

        public string JournalHash { get; private set; }

        public void AddLine(string transactionDate, string journalHash)
        {
            LineCount++;
            if (FirstTransactionDate is null || string.CompareOrdinal(transactionDate, FirstTransactionDate) < 0)
            {
                FirstTransactionDate = transactionDate;
            }

            if (LineCount == 1)
            {
                JournalHash = journalHash;
            }
        }

        public bool MatchesJournalHash(string journalHash) =>
            string.Equals(JournalHash, journalHash, StringComparison.Ordinal);

        public GlJournalAttributes ToAttributes() => new(
            _first.JournalIdentity,
            _first.IdentitySource,
            _first.JournalNumber,
            _first.JournalSource,
            _first.PostingDate,
            _first.Reference,
            _first.Description,
            _first.CurrencyCode,
            _first.PreparedBy,
            JournalHash,
            LineCount,
            FirstTransactionDate);
    }

    private sealed class GlRowResult
    {
        public static readonly GlRowResult Skipped = new() { Skip = true };

        public bool Skip { get; private init; }

        public bool HasError { get; set; }

        public bool HasWarning { get; set; }

        public string AccountCode { get; init; } = string.Empty;

        public string NormalizedCode { get; init; } = string.Empty;

        public string? AccountName { get; set; }

        public Guid? AccountId { get; set; }

        public string? SourceLineNo { get; init; }

        public string LineIdentity { get; set; } = string.Empty;

        public string LineIdentitySource { get; set; } = ImportIdentitySource.Derived;

        public string JournalIdentity { get; set; } = string.Empty;

        public string IdentitySource { get; set; } = ImportIdentitySource.Derived;

        public string? JournalNumber { get; set; }

        public string? JournalSource { get; init; }

        public string TransactionDate { get; set; } = string.Empty;

        public string? PostingDate { get; set; }

        public string Description { get; init; } = string.Empty;

        public long DebitMinor { get; set; }

        public long CreditMinor { get; set; }

        public string? CurrencyCode { get; set; }

        public string? Reference { get; init; }

        public string? PreparedBy { get; init; }

        public bool IsOutOfPeriod { get; set; }

        public string LineHash { get; set; } = string.Empty;

        public string ValueHash { get; set; } = string.Empty;

        public string AttributeHash { get; set; } = string.Empty;

        public string JournalHash { get; set; } = string.Empty;

        public string ExtraColumnsJson { get; init; } = "{}";

        public static GlRowResult Error() => new() { HasError = true };
    }
}
