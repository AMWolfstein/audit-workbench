using System.Globalization;
using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.FinancialData.Imports;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Infrastructure.FinancialData;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.FinancialData;

/// <summary>Paged general-ledger line search.</summary>
public sealed record GlLineQuery(
    Guid ImportId,
    string? TransactionIdentity = null,
    string? AccountCode = null,
    string? DateFrom = null,
    string? DateTo = null,
    long? MinAmountMinor = null,
    long? MaxAmountMinor = null,
    string? Description = null,
    string? JournalSource = null,
    string? Reference = null,
    bool OutOfPeriodOnly = false,
    int Page = 1,
    int PageSize = 50);

public sealed record GlLineRow(
    Guid GlLineId,
    Guid GlJournalId,
    int LineNo,
    int? SourceRowNo,
    string JournalIdentity,
    string? JournalNumber,
    string LineIdentity,
    string? SourceLineNo,
    string AccountCode,
    string? AccountName,
    string TransactionDate,
    string? PostingDate,
    string Description,
    long DebitMinor,
    long CreditMinor,
    long AmountMinor,
    string? CurrencyCode,
    string? JournalSource,
    string? Reference,
    string? PreparedBy,
    bool IsOutOfPeriod,
    Guid? AuditAreaId,
    long TotalCount);

/// <summary>
/// General-ledger import workflow. Each import is a snapshot of the ledger
/// through a date; snapshots are never overwritten, which is what makes the
/// future period-extension comparison possible.
/// </summary>
public sealed class GeneralLedgerImportService
{
    private const string Kind = FinancialDatasetKind.GeneralLedger;

    private readonly AuditWorkbenchDbContext _dbContext;
    private readonly UnitOfWork _unitOfWork;
    private readonly AuditTrailWriter _auditTrail;
    private readonly FinancialPeriodService _periods;
    private readonly FinancialUploadService _uploads;
    private readonly DatasetImportService _imports;
    private readonly EngagementService _engagements;
    private readonly ITabularFileReader _reader;
    private readonly BulkInserter _bulkInserter;
    private readonly SqlQueryExecutor _queries;
    private readonly IClock _clock;
    private readonly ICurrentActor _actor;

    public GeneralLedgerImportService(
        AuditWorkbenchDbContext dbContext,
        UnitOfWork unitOfWork,
        AuditTrailWriter auditTrail,
        FinancialPeriodService periods,
        FinancialUploadService uploads,
        DatasetImportService imports,
        EngagementService engagements,
        ITabularFileReader reader,
        BulkInserter bulkInserter,
        SqlQueryExecutor queries,
        IClock clock,
        ICurrentActor actor)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _periods = periods;
        _uploads = uploads;
        _imports = imports;
        _engagements = engagements;
        _reader = reader;
        _bulkInserter = bulkInserter;
        _queries = queries;
        _clock = clock;
        _actor = actor;
    }

    public async Task<ImportValidationOutcome> ValidateAsync(
        Guid engagementId,
        Guid uploadId,
        ImportColumnMapping mapping,
        bool allowUnbalanced,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var period = await _periods
            .RequireOpenForImportsAsync(engagementId, FinancialDataPermissions.Import, cancellationToken)
            .ConfigureAwait(false);
        var upload = await _uploads.LoadAsync(engagementId, Kind, uploadId, requireWritable: true,
            cancellationToken).ConfigureAwait(false);
        var context = await BuildContextAsync(engagementId, period, allowUnbalanced, cancellationToken)
            .ConfigureAwait(false);

        var mappingIssues = new ImportValidationReport(Kind);
        ImportSupport.EnsureMappingIsUsable(mappingIssues, upload.Structure, mapping, Kind);
        if (mappingIssues.HasErrors)
        {
            return new ImportValidationOutcome(Kind, upload.Structure, mapping, mappingIssues,
                ImportSupport.PendingFingerprint());
        }

        var pass = GeneralLedgerImportPipeline.Validate(upload.Source, _reader, upload.Structure, mapping, context,
            progress, cancellationToken);
        foreach (var code in pass.UnmatchedAccountCodes.Take(25))
        {
            pass.Report.AddWarning(ImportIssueCodes.InvalidAccountReference,
                $"Ledger account {code} has no counterpart in the trial balance of this period; the reconciliation " +
                "will list it as a ledger account missing from the TB.");
        }

        var fingerprint = TransactionIdentity.Fingerprint(Kind, engagementId, period.FinancialPeriodId,
            upload.Record.Sha256, mapping.ToJson(), pass.Report.TotalDebitMinor, pass.Report.TotalCreditMinor,
            pass.Report.RowCount);
        return new ImportValidationOutcome(Kind, upload.Structure, mapping, pass.Report, fingerprint);
    }

    public async Task<ImportCommitResult> ImportAsync(
        Guid engagementId,
        Guid uploadId,
        ImportColumnMapping mapping,
        bool allowUnbalanced,
        bool allowRepeat,
        ImportValidationOutcome? precomputedOutcome = null,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The operator's validation pass is reused when the job already produced it
        // for exactly this file and mapping; the transaction still re-validates the
        // stored bytes row by row before anything is written.
        var outcome = precomputedOutcome ?? await ValidateAsync(engagementId, uploadId, mapping, allowUnbalanced,
            progress, cancellationToken).ConfigureAwait(false);
        if (outcome.Report.HasErrors)
        {
            await RecordRefusedImportAsync(engagementId, uploadId, outcome, cancellationToken)
                .ConfigureAwait(false);
            throw new ValidationException(ImportSupport.DescribeBlockingErrors(outcome.Report));
        }

        return await _unitOfWork.ExecuteAsync(async token =>
        {
            var period = await _periods
                .RequireOpenForImportsAsync(engagementId, FinancialDataPermissions.Import, token)
                .ConfigureAwait(false);
            var upload = await _uploads.LoadAsync(engagementId, Kind, uploadId, requireWritable: true, token)
                .ConfigureAwait(false);
            var engagement = await _engagements.LoadAsync(engagementId, token).ConfigureAwait(false);
            var year = await _engagements.LoadYearAsync(engagement.FinancialYearId, token).ConfigureAwait(false);
            var context = await BuildContextAsync(engagementId, period, allowUnbalanced, token).ConfigureAwait(false);

            await _auditTrail.AppendAsync(
                    AuditEventType.GlImportStarted,
                    AuditEntityType.DatasetImport,
                    upload.Record.UploadId.ToString("D"),
                    $"{FinancialDatasetKind.DisplayName(Kind)} import from '{upload.Record.FileName}' started " +
                    $"for {year.Label}.",
                    companyId: engagement.CompanyId,
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", Kind)
                        .With("file_name", upload.Record.FileName)
                        .With("file_sha256", upload.Record.Sha256),
                    cancellationToken: token)
                .ConfigureAwait(false);

            var previousWithSameFile = await _dbContext.DatasetImports.AsNoTracking()
                .Where(i => i.EngagementId == engagementId && i.DatasetKind == Kind &&
                            i.SourceFileSha256 == upload.Record.Sha256)
                .OrderByDescending(i => i.ImportNo)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (previousWithSameFile is not null && !allowRepeat)
            {
                throw new ValidationException(
                    $"The file '{upload.Record.FileName}' was already imported as " +
                    $"{previousWithSameFile.SnapshotLabel} on {previousWithSameFile.ImportedAtUtc}. " +
                    "Confirm that you want to import the same file as a new snapshot, or upload the extended file.");
            }

            // Re-validate while streaming inside the transaction: a file that changed
            // after the first pass cannot be committed.
            var pass = GeneralLedgerImportPipeline.Validate(upload.Source, _reader, upload.Structure, mapping, context,
                progress: null, token);
            if (pass.Report.HasErrors)
            {
                throw new ValidationException(ImportSupport.DescribeBlockingErrors(pass.Report));
            }

            var importNo = await _imports.NextImportNumberAsync(engagementId, Kind, token).ConfigureAwait(false);
            var coverageThrough = pass.Report.LastTransactionDate ?? year.PeriodEnd;
            var header = FinancialDatasetImport.Create(
                Guid.NewGuid(),
                engagementId,
                period.FinancialPeriodId,
                Kind,
                importNo,
                upload.Record.UploadId,
                upload.Record.FileName,
                upload.Record.Sha256,
                upload.Record.SizeBytes,
                upload.Structure.HeaderRowNumber == 0 ? null : upload.Structure.HeaderRowNumber,
                upload.Structure.SheetName,
                mapping.ToJson(),
                outcome.FingerprintHash,
                IClock.Format(_clock.UtcNow),
                _actor.UserId,
                previousWithSameFile?.ImportId,
                coverageThrough);
            _dbContext.DatasetImports.Add(header);
            await _dbContext.SaveChangesAsync(token).ConfigureAwait(false);

            await _imports.SupersedeActiveAsync(engagementId, Kind, header.ImportId, header.ImportNo, token)
                .ConfigureAwait(false);
            await _dbContext.SaveChangesAsync(token).ConfigureAwait(false);

            header.MarkValidated(
                pass.Report.ValidationStatus,
                pass.Report.ToJson(),
                pass.Report.RowCount,
                pass.Report.ValidRowCount,
                pass.Report.WarningRowCount,
                pass.Report.ErrorRowCount,
                pass.Report.TotalDebitMinor,
                pass.Report.TotalCreditMinor,
                pass.Report.DifferenceMinor,
                pass.Report.IsBalanced,
                false);
            await _dbContext.SaveChangesAsync(token).ConfigureAwait(false);

            var journalIds = await WriteJournalsAsync(header, pass.Journals, token).ConfigureAwait(false);
            var written = await WriteLinesAsync(header, upload, mapping, context, journalIds, token)
                .ConfigureAwait(false);

            header.MarkImported(
                pass.Report.RowCount,
                pass.Report.ValidRowCount,
                pass.Report.WarningRowCount,
                pass.Report.ErrorRowCount,
                pass.Report.OutOfPeriodCount,
                pass.Report.TotalDebitMinor,
                pass.Report.TotalCreditMinor,
                pass.Report.DifferenceMinor,
                pass.Report.IsBalanced,
                false,
                coverageThrough,
                activate: true);
            await _dbContext.SaveChangesAsync(token).ConfigureAwait(false);

            await _auditTrail.AppendAsync(
                    AuditEventType.DatasetActivated,
                    AuditEntityType.DatasetImport,
                    header.ImportId.ToString("D"),
                    $"{header.SnapshotLabel} is now the active general ledger snapshot of {year.Label}.",
                    companyId: engagement.CompanyId,
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", Kind)
                        .With("import_no", header.ImportNo)
                        .With("coverage_through", coverageThrough),
                    cancellationToken: token)
                .ConfigureAwait(false);

            await _auditTrail.AppendAsync(
                    AuditEventType.GlImportCompleted,
                    AuditEntityType.DatasetImport,
                    header.ImportId.ToString("D"),
                    $"General ledger {header.SnapshotLabel} imported from '{upload.Record.FileName}' " +
                    $"({pass.Report.RowCount} rows through {coverageThrough}, debits {pass.Report.TotalDebitMinor}, " +
                    $"credits {pass.Report.TotalCreditMinor}, out of period {pass.Report.OutOfPeriodCount}).",
                    companyId: engagement.CompanyId,
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", Kind)
                        .With("import_no", header.ImportNo)
                        .With("file_name", upload.Record.FileName)
                        .With("file_sha256", upload.Record.Sha256)
                        .With("rows", pass.Report.RowCount)
                        .With("journals", pass.Journals.Count)
                        .With("errors", pass.Report.ErrorRowCount)
                        .With("warnings", pass.Report.WarningRowCount)
                        .With("out_of_period", pass.Report.OutOfPeriodCount)
                        .With("coverage_through", coverageThrough)
                        .With("unmatched_accounts", pass.UnmatchedAccountCodes.Count)
                        .With("repeat_of_import_id", previousWithSameFile?.ImportId)
                        .With("lines_written", written),
                    cancellationToken: token)
                .ConfigureAwait(false);

            return new ImportCommitResult(
                header.ImportId,
                Kind,
                header.ImportNo,
                header.SnapshotLabel,
                pass.Report.RowCount,
                pass.Report.ValidRowCount,
                pass.Report.ErrorRowCount,
                pass.Report.WarningRowCount,
                pass.Report.OutOfPeriodCount,
                pass.Report.TotalDebitMinor,
                pass.Report.TotalCreditMinor,
                pass.Report.DifferenceMinor,
                pass.Report.IsBalanced,
                header.IsActive,
                pass.Report.ValidationStatus);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GlLineRow>> SearchAsync(Guid engagementId, GlLineQuery query,
        CancellationToken cancellationToken = default)
    {
        var import = await _imports.LoadTrackedAsync(engagementId, query.ImportId, Kind, requireWritable: false,
            Permissions.ViewEngagement, cancellationToken).ConfigureAwait(false);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var page = Math.Max(1, query.Page);
        return await _queries.QueryAsync(
            "gl_search.sql",
            new Dictionary<string, object?>
            {
                ["import_id"] = import.ImportId,
                ["transaction_id"] = Like(query.TransactionIdentity),
                ["account_code"] = Like(query.AccountCode),
                ["date_from"] = NormalizeDate(query.DateFrom),
                ["date_to"] = NormalizeDate(query.DateTo),
                ["min_amount"] = query.MinAmountMinor,
                ["max_amount"] = query.MaxAmountMinor,
                ["description"] = LikeLower(query.Description),
                ["journal_source"] = LikeLower(query.JournalSource),
                ["reference"] = LikeLower(query.Reference),
                ["out_of_period_only"] = query.OutOfPeriodOnly ? 1 : 0,
                ["page_size"] = pageSize,
                ["offset"] = (page - 1) * pageSize,
            },
            MapLine,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The exact list of out-of-period transactions of one snapshot.</summary>
    public async Task<IReadOnlyList<GlLineRow>> OutOfPeriodAsync(Guid engagementId, Guid importId, int page = 1,
        int pageSize = 100, CancellationToken cancellationToken = default)
    {
        var import = await _imports.LoadTrackedAsync(engagementId, importId, Kind, requireWritable: false,
            Permissions.ViewEngagement, cancellationToken).ConfigureAwait(false);
        var size = Math.Clamp(pageSize, 1, 500);
        return await _queries.QueryAsync(
            "gl_out_of_period.sql",
            new Dictionary<string, object?>
            {
                ["import_id"] = import.ImportId,
                ["page_size"] = size,
                ["offset"] = (Math.Max(1, page) - 1) * size,
            },
            MapLine,
            cancellationToken).ConfigureAwait(false);
    }

    private static GlLineRow MapLine(System.Data.Common.DbDataReader reader) => new(
        SqlQueryExecutor.GetNullableGuid(reader, "gl_line_id")!.Value,
        SqlQueryExecutor.GetNullableGuid(reader, "gl_journal_id")!.Value,
        Convert.ToInt32(reader.GetValue(reader.GetOrdinal("line_no"))),
        SqlQueryExecutor.GetNullableInt32(reader, "source_row_no"),
        reader.GetString(reader.GetOrdinal("journal_identity")),
        SqlQueryExecutor.GetNullableString(reader, "journal_number"),
        reader.GetString(reader.GetOrdinal("line_identity")),
        SqlQueryExecutor.GetNullableString(reader, "source_line_no"),
        reader.GetString(reader.GetOrdinal("account_code")),
        SqlQueryExecutor.GetNullableString(reader, "account_name"),
        reader.GetString(reader.GetOrdinal("transaction_date")),
        SqlQueryExecutor.GetNullableString(reader, "posting_date"),
        SqlQueryExecutor.GetNullableString(reader, "description") ?? string.Empty,
        Convert.ToInt64(reader.GetValue(reader.GetOrdinal("debit_minor"))),
        Convert.ToInt64(reader.GetValue(reader.GetOrdinal("credit_minor"))),
        Convert.ToInt64(reader.GetValue(reader.GetOrdinal("amount_minor"))),
        SqlQueryExecutor.GetNullableString(reader, "currency_code"),
        SqlQueryExecutor.GetNullableString(reader, "journal_source"),
        SqlQueryExecutor.GetNullableString(reader, "reference"),
        SqlQueryExecutor.GetNullableString(reader, "prepared_by"),
        Convert.ToInt32(reader.GetValue(reader.GetOrdinal("is_out_of_period"))) == 1,
        SqlQueryExecutor.GetNullableGuid(reader, "audit_area_id"),
        Convert.ToInt64(reader.GetValue(reader.GetOrdinal("total_count"))));

    /// <summary>
    /// Records a refused import. The refusal is written outside the abandoned
    /// transaction so the trail keeps the attempt even though nothing was
    /// imported, and a failure of this record never replaces the real error.
    /// </summary>
    private async Task RecordRefusedImportAsync(Guid engagementId, Guid uploadId, ImportValidationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var record = await _uploads.LoadRecordAsync(engagementId, uploadId, cancellationToken)
                .ConfigureAwait(false);
            await _auditTrail.AppendAsync(
                    AuditEventType.GlImportFailed,
                    AuditEntityType.DatasetImport,
                    record.UploadId.ToString("D"),
                    $"{FinancialDatasetKind.DisplayName(Kind)} import from '{record.FileName}' was refused. " +
                    ImportSupport.DescribeBlockingErrors(outcome.Report),
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", Kind)
                        .With("file_name", record.FileName)
                        .With("file_sha256", record.Sha256)
                        .With("validation_status", outcome.Report.ValidationStatus)
                        .With("error_rows", outcome.Report.ErrorRowCount)
                        .With("warning_rows", outcome.Report.WarningRowCount)
                        .With("difference_minor", outcome.Report.DifferenceMinor),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The caller must still see why the import was refused.
        }
    }

    private async Task<GlImportContext> BuildContextAsync(Guid engagementId, FinancialPeriod period,
        bool allowUnbalanced, CancellationToken cancellationToken)
    {
        var engagement = await _engagements.LoadAsync(engagementId, cancellationToken).ConfigureAwait(false);
        var year = await _engagements.LoadYearAsync(engagement.FinancialYearId, cancellationToken)
            .ConfigureAwait(false);
        var accounts = await _dbContext.Accounts.AsNoTracking()
            .Where(a => a.EngagementId == engagementId)
            .Select(a => new { a.AccountId, a.AccountCode, a.AccountName, a.AccountGroup, a.NormalizedCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<string, KnownAccount>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            var key = string.IsNullOrWhiteSpace(account.NormalizedCode)
                ? Account.NormalizeCode(account.AccountCode)
                : account.NormalizedCode;
            map.TryAdd(key, new KnownAccount(account.AccountId, account.AccountCode, account.AccountName,
                account.AccountGroup));
        }

        return new GlImportContext
        {
            EngagementId = engagementId,
            FinancialPeriodId = period.FinancialPeriodId,
            CurrencyCode = engagement.CurrencyCode,
            MinorUnitScale = engagement.MinorUnitScale,
            PeriodStart = DateOnly.ParseExact(year.PeriodStart, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            PeriodEnd = DateOnly.ParseExact(year.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            AccountsByNormalizedCode = map,
            AllowUnbalanced = allowUnbalanced,
        };
    }

    private async Task<IReadOnlyDictionary<string, Guid>> WriteJournalsAsync(
        FinancialDatasetImport header,
        IReadOnlyList<GlJournalAttributes> journals,
        CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, Guid>(journals.Count, StringComparer.Ordinal);
        var rows = new List<object?[]>(FinancialImportLimits.InsertBatchSize);
        var columns = new[]
        {
            "gl_journal_id", "import_id", "engagement_id", "financial_period_id", "journal_identity",
            "identity_source", "journal_number", "journal_source", "posting_date", "reference", "description",
            "currency_code", "prepared_by", "line_count", "journal_hash", "created_at_utc",
        };
        var now = IClock.Format(_clock.UtcNow);

        foreach (var journal in journals)
        {
            var id = Guid.NewGuid();
            ids[journal.JournalIdentity] = id;
            rows.Add(new object?[]
            {
                id, header.ImportId, header.EngagementId, header.FinancialPeriodId, journal.JournalIdentity,
                journal.IdentitySource, journal.JournalNumber, journal.JournalSource, journal.PostingDate,
                journal.Reference, journal.Description, journal.CurrencyCode, journal.PreparedBy, journal.LineCount,
                journal.JournalHash, now,
            });

            if (rows.Count >= FinancialImportLimits.InsertBatchSize)
            {
                await _bulkInserter.InsertAsync("gl_journal", columns, rows, values => values, cancellationToken)
                    .ConfigureAwait(false);
                rows.Clear();
            }
        }

        if (rows.Count > 0)
        {
            await _bulkInserter.InsertAsync("gl_journal", columns, rows, values => values, cancellationToken)
                .ConfigureAwait(false);
        }

        return ids;
    }

    private async Task<int> WriteLinesAsync(
        FinancialDatasetImport header,
        StoredUpload upload,
        ImportColumnMapping mapping,
        GlImportContext context,
        IReadOnlyDictionary<string, Guid> journalIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<object?[]>(FinancialImportLimits.InsertBatchSize);
        var columns = new[]
        {
            "gl_line_id", "gl_journal_id", "import_id", "engagement_id", "financial_period_id", "line_no",
            "source_row_no", "line_identity", "identity_source", "source_line_no", "account_id", "account_code",
            "account_name", "transaction_date", "posting_date", "description", "debit_minor", "credit_minor",
            "amount_minor", "currency_code", "journal_source", "reference", "prepared_by", "is_out_of_period",
            "line_hash", "value_hash", "attribute_hash", "extra_columns_json", "created_at_utc",
        };
        var now = IClock.Format(_clock.UtcNow);
        var written = 0;

        foreach (var line in GeneralLedgerImportPipeline.PrepareLines(upload.Source, _reader, upload.Structure, mapping,
                     context, journalIds, cancellationToken))
        {
            if (!journalIds.TryGetValue(line.JournalIdentity, out var journalId))
            {
                throw new ValidationException(
                    $"Journal {line.JournalIdentity} was not written. Nothing was written.");
            }

            rows.Add(new object?[]
            {
                Guid.NewGuid(), journalId, header.ImportId, header.EngagementId, header.FinancialPeriodId, line.LineNo,
                line.SourceRowNo, line.LineIdentity, line.IdentitySource, line.SourceLineNo, line.AccountId,
                line.AccountCode, line.AccountName, line.TransactionDate, line.PostingDate, line.Description,
                line.DebitMinor, line.CreditMinor, line.DebitMinor - line.CreditMinor, line.CurrencyCode,
                line.JournalSource, line.Reference, line.PreparedBy, line.IsOutOfPeriod ? 1 : 0, line.LineHash,
                line.ValueHash, line.AttributeHash, line.ExtraColumnsJson, now,
            });

            if (rows.Count >= FinancialImportLimits.InsertBatchSize)
            {
                written += await _bulkInserter
                    .InsertAsync("gl_line", columns, rows, values => values, cancellationToken)
                    .ConfigureAwait(false);
                rows.Clear();
            }
        }

        if (rows.Count > 0)
        {
            written += await _bulkInserter.InsertAsync("gl_line", columns, rows, values => values, cancellationToken)
                .ConfigureAwait(false);
        }

        return written;
    }

    private static string? Like(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"%{value.Trim()}%";

    private static string? LikeLower(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"%{value.Trim().ToLowerInvariant()}%";

    private static string? NormalizeDate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : (ImportValueParser.TryParseDate(value, out var iso) ? iso : value.Trim());
}
