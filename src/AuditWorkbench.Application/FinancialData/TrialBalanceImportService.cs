using System.Globalization;
using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Common;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.FinancialData.Imports;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Auditing;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Engagements;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Infrastructure.FinancialData;
using AuditWorkbench.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuditWorkbench.Application.FinancialData;

/// <summary>Paged trial-balance line search.</summary>
public sealed record TbLineQuery(
    Guid ImportId,
    string? AccountCode = null,
    string? AccountName = null,
    string? AccountGroup = null,
    long? MinBalanceMinor = null,
    long? MaxBalanceMinor = null,
    int Page = 1,
    int PageSize = 50);

public sealed record TbLineRow(
    Guid TbLineId,
    int LineNo,
    int? SourceRowNo,
    string AccountCode,
    string AccountName,
    string? AccountGroup,
    string? CostCenter,
    long DebitMinor,
    long CreditMinor,
    long BalanceMinor,
    string CurrencyCode,
    Guid? AuditAreaId,
    string? AuditAreaCode,
    long TotalCount);

/// <summary>
/// Trial-balance import workflow: validate the mapped file, commit it in one
/// transaction, and keep every earlier version addressable.
/// </summary>
public sealed class TrialBalanceImportService
{
    private const string Kind = FinancialDatasetKind.TrialBalance;

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
    private readonly EngagementAuthorizationService _authorization;

    public TrialBalanceImportService(
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
        ICurrentActor actor,
        EngagementAuthorizationService authorization)
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
        _authorization = authorization;
    }

    /// <summary>
    /// Validation pass: reads the preserved source file row by row, applies the
    /// mapping and reports counts, totals and findings. Nothing is written.
    /// </summary>
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

        var pass = TrialBalanceImportPipeline.Validate(upload.Source, _reader, upload.Structure, mapping, context,
            progress, cancellationToken);
        var fingerprint = TransactionIdentity.Fingerprint(Kind, engagementId, period.FinancialPeriodId,
            upload.Record.Sha256, mapping.ToJson(), pass.Report.TotalDebitMinor, pass.Report.TotalCreditMinor,
            pass.Report.RowCount);
        return new ImportValidationOutcome(Kind, upload.Structure, mapping, pass.Report, fingerprint);
    }

    /// <summary>
    /// Commits the import. The rows, the import header, the account master
    /// additions and the audit event are written in one transaction: a fatal
    /// error leaves the workspace without a half-populated dataset.
    /// </summary>
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
                    "Confirm that you want to import the same file as a new version, or upload the corrected file.");
            }

            // The validation of the first pass is re-checked while streaming inside
            // the transaction, so a file that changed on disk cannot slip through.
            var pass = TrialBalanceImportPipeline.Validate(upload.Source, _reader, upload.Structure, mapping, context,
                progress: null, token);
            if (pass.Report.HasErrors)
            {
                throw new ValidationException(ImportSupport.DescribeBlockingErrors(pass.Report));
            }

            var importNo = await _imports.NextImportNumberAsync(engagementId, Kind, token).ConfigureAwait(false);
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
                year.PeriodEnd);
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
                allowUnbalanced && !pass.Report.IsBalanced);
            await _dbContext.SaveChangesAsync(token).ConfigureAwait(false);

            var accountIds = await EnsureAccountsAsync(engagement, pass.NewAccounts, context, token)
                .ConfigureAwait(false);

            var written = await WriteLinesAsync(header, upload, mapping, context, accountIds, token)
                .ConfigureAwait(false);

            header.MarkImported(
                pass.Report.RowCount,
                pass.Report.ValidRowCount,
                pass.Report.WarningRowCount,
                pass.Report.ErrorRowCount,
                0,
                pass.Report.TotalDebitMinor,
                pass.Report.TotalCreditMinor,
                pass.Report.DifferenceMinor,
                pass.Report.IsBalanced,
                allowUnbalanced && !pass.Report.IsBalanced,
                year.PeriodEnd,
                activate: true);
            await _dbContext.SaveChangesAsync(token).ConfigureAwait(false);

            await _auditTrail.AppendAsync(
                    AuditEventType.DatasetActivated,
                    AuditEntityType.DatasetImport,
                    header.ImportId.ToString("D"),
                    $"{header.SnapshotLabel} is now the active trial balance of {year.Label}.",
                    companyId: engagement.CompanyId,
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", Kind)
                        .With("import_no", header.ImportNo),
                    cancellationToken: token)
                .ConfigureAwait(false);

            await _auditTrail.AppendAsync(
                    AuditEventType.TbImportCompleted,
                    AuditEntityType.DatasetImport,
                    header.ImportId.ToString("D"),
                    $"Trial balance {header.SnapshotLabel} imported from '{upload.Record.FileName}' " +
                    $"({pass.Report.RowCount} rows, debits {pass.Report.TotalDebitMinor}, credits " +
                    $"{pass.Report.TotalCreditMinor}, balanced: {(pass.Report.IsBalanced ? "yes" : "no")}).",
                    companyId: engagement.CompanyId,
                    engagementId: engagementId,
                    details: AuditDetails.Empty()
                        .With("dataset_kind", Kind)
                        .With("import_no", header.ImportNo)
                        .With("file_name", upload.Record.FileName)
                        .With("file_sha256", upload.Record.Sha256)
                        .With("rows", pass.Report.RowCount)
                        .With("errors", pass.Report.ErrorRowCount)
                        .With("warnings", pass.Report.WarningRowCount)
                        .With("balanced", pass.Report.IsBalanced)
                        .With("unbalanced_override", allowUnbalanced && !pass.Report.IsBalanced)
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
                0,
                pass.Report.TotalDebitMinor,
                pass.Report.TotalCreditMinor,
                pass.Report.DifferenceMinor,
                pass.Report.IsBalanced,
                header.IsActive,
                pass.Report.ValidationStatus);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Paged search of the imported lines of one version (SQL paging, no N+1).</summary>
    public async Task<IReadOnlyList<TbLineRow>> SearchAsync(Guid engagementId, TbLineQuery query,
        CancellationToken cancellationToken = default)
    {
        var import = await _imports.LoadTrackedAsync(engagementId, query.ImportId, Kind, requireWritable: false,
            Permissions.ViewEngagement, cancellationToken).ConfigureAwait(false);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var page = Math.Max(1, query.Page);
        return await _queries.QueryAsync(
            "tb_search.sql",
            new Dictionary<string, object?>
            {
                ["import_id"] = import.ImportId,
                ["account_code"] = Like(query.AccountCode),
                ["account_name"] = LikeLower(query.AccountName),
                ["account_group"] = LikeLower(query.AccountGroup),
                ["min_balance"] = query.MinBalanceMinor,
                ["max_balance"] = query.MaxBalanceMinor,
                ["page_size"] = pageSize,
                ["offset"] = (page - 1) * pageSize,
            },
            reader => new TbLineRow(
                SqlQueryExecutor.GetNullableGuid(reader, "tb_line_id")!.Value,
                Convert.ToInt32(reader.GetValue(reader.GetOrdinal("line_no"))),
                SqlQueryExecutor.GetNullableInt32(reader, "source_row_no"),
                reader.GetString(reader.GetOrdinal("account_code")),
                reader.GetString(reader.GetOrdinal("account_name")),
                SqlQueryExecutor.GetNullableString(reader, "account_group"),
                SqlQueryExecutor.GetNullableString(reader, "cost_center"),
                Convert.ToInt64(reader.GetValue(reader.GetOrdinal("debit_minor"))),
                Convert.ToInt64(reader.GetValue(reader.GetOrdinal("credit_minor"))),
                Convert.ToInt64(reader.GetValue(reader.GetOrdinal("balance_minor"))),
                reader.GetString(reader.GetOrdinal("currency_code")),
                SqlQueryExecutor.GetNullableGuid(reader, "audit_area_id"),
                SqlQueryExecutor.GetNullableString(reader, "audit_area_code"),
                Convert.ToInt64(reader.GetValue(reader.GetOrdinal("total_count")))),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Account-level comparison of two TB versions (roll-forward style).</summary>
    public async Task<IReadOnlyList<TbVersionDifferenceRow>> CompareVersionsAsync(Guid engagementId,
        Guid previousImportId, Guid currentImportId, CancellationToken cancellationToken = default)
    {
        await _imports.LoadTrackedAsync(engagementId, previousImportId, Kind, requireWritable: false,
            Permissions.ViewEngagement, cancellationToken).ConfigureAwait(false);
        await _imports.LoadTrackedAsync(engagementId, currentImportId, Kind, requireWritable: false,
            Permissions.ViewEngagement, cancellationToken).ConfigureAwait(false);

        return await _queries.QueryAsync(
            "tb_version_comparison.sql",
            new Dictionary<string, object?>
            {
                ["previous_import_id"] = previousImportId,
                ["current_import_id"] = currentImportId,
            },
            reader => new TbVersionDifferenceRow(
                reader.GetString(reader.GetOrdinal("state")),
                reader.GetString(reader.GetOrdinal("account_code")),
                reader.GetString(reader.GetOrdinal("account_name")),
                SqlQueryExecutor.GetNullableInt64(reader, "previous_balance_minor"),
                SqlQueryExecutor.GetNullableInt64(reader, "current_balance_minor"),
                SqlQueryExecutor.GetNullableInt64(reader, "previous_debit_minor"),
                SqlQueryExecutor.GetNullableInt64(reader, "previous_credit_minor"),
                SqlQueryExecutor.GetNullableInt64(reader, "current_debit_minor"),
                SqlQueryExecutor.GetNullableInt64(reader, "current_credit_minor")),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TbImportContext> BuildContextAsync(Guid engagementId, FinancialPeriod period,
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

        return new TbImportContext
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

    private async Task<Dictionary<string, Guid>> EnsureAccountsAsync(
        Engagement engagement,
        IReadOnlyList<NewAccountRequest> newAccounts,
        TbImportContext context,
        CancellationToken cancellationToken)
    {
        var accountIds = context.AccountsByNormalizedCode.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.AccountId,
            StringComparer.Ordinal);

        if (newAccounts.Count == 0)
        {
            return accountIds;
        }

        var nextOrder = await _dbContext.Accounts
            .Where(a => a.EngagementId == engagement.EngagementId)
            .Select(a => (int?)a.DisplayOrder)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0;

        var now = IClock.Format(_clock.UtcNow);
        foreach (var request in newAccounts)
        {
            var account = Account.Create(
                Guid.NewGuid(),
                engagement.EngagementId,
                request.AccountCode,
                request.AccountName,
                AccountType.Unclassified,
                nextOrder + 10,
                now,
                _actor.UserId,
                request.AccountGroup,
                AccountOrigins.TrialBalance);
            nextOrder += 10;
            _dbContext.Accounts.Add(account);
            accountIds[request.NormalizedCode] = account.AccountId;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return accountIds;
    }

    private async Task<int> WriteLinesAsync(
        FinancialDatasetImport header,
        StoredUpload upload,
        ImportColumnMapping mapping,
        TbImportContext context,
        IReadOnlyDictionary<string, Guid> accountIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<object?[]>(FinancialImportLimits.InsertBatchSize);
        var columns = new[]
        {
            "tb_line_id", "import_id", "engagement_id", "financial_period_id", "account_id", "line_no",
            "source_row_no", "account_code", "account_name", "normalized_code", "debit_minor", "credit_minor",
            "balance_minor", "currency_code", "cost_center", "account_group", "extra_columns_json", "row_hash",
            "created_at_utc",
        };
        var now = IClock.Format(_clock.UtcNow);
        var written = 0;

        foreach (var row in TrialBalanceImportPipeline.PrepareRows(upload.Source, _reader, upload.Structure, mapping,
                     context, cancellationToken))
        {
            if (!accountIds.TryGetValue(row.NormalizedCode, out var accountId))
            {
                throw new ValidationException(
                    $"Account {row.AccountCode} could not be resolved to the account master. Nothing was written.");
            }

            rows.Add(new object?[]
            {
                Guid.NewGuid(), header.ImportId, header.EngagementId, header.FinancialPeriodId, accountId, row.LineNo,
                row.SourceRowNo, row.AccountCode, row.AccountName, row.NormalizedCode, row.DebitMinor, row.CreditMinor,
                row.BalanceMinor, row.CurrencyCode, row.CostCenter, row.AccountGroup,
                row.ExtraColumnsJson, row.RowHash, now,
            });

            if (rows.Count >= FinancialImportLimits.InsertBatchSize)
            {
                written += await _bulkInserter
                    .InsertAsync("tb_line", columns, rows, values => values, cancellationToken)
                    .ConfigureAwait(false);
                rows.Clear();
            }
        }

        if (rows.Count > 0)
        {
            written += await _bulkInserter.InsertAsync("tb_line", columns, rows, values => values, cancellationToken)
                .ConfigureAwait(false);
        }

        return written;
    }

    private static string? Like(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"%{value.Trim()}%";

    private static string? LikeLower(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"%{value.Trim().ToLowerInvariant()}%";
}
