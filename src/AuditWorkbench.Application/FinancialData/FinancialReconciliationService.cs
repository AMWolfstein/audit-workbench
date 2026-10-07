using System.Data.Common;
using AuditWorkbench.Application.Teams;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;
using AuditWorkbench.Domain.Identity;
using AuditWorkbench.Infrastructure.Persistence;

namespace AuditWorkbench.Application.FinancialData;

/// <summary>
/// The read side of the financial data foundation: comparing a trial balance with
/// the ledger it came from, and comparing a ledger snapshot with the snapshot that
/// was examined earlier (roll-forward).
///
/// Both comparisons reuse the same embedded reads that the .NET-free verification
/// harness exercises, follow the one debit-positive sign convention, and report
/// their exceptions explicitly: nothing is matched away, and a difference is never
/// presented as agreement.
/// </summary>
public sealed class FinancialReconciliationService
{
    private const int MaxPageSize = 500;

    private readonly SqlQueryExecutor _queries;
    private readonly DatasetImportService _imports;
    private readonly EngagementAuthorizationService _authorization;

    public FinancialReconciliationService(
        SqlQueryExecutor queries,
        DatasetImportService imports,
        EngagementAuthorizationService authorization)
    {
        _queries = queries;
        _imports = imports;
        _authorization = authorization;
    }

    /// <summary>
    /// Trial balance of one version against the ledger of one snapshot, account by
    /// account. Accounts present in only one dataset are returned with the other
    /// side empty and an explicit status.
    /// </summary>
    public async Task<ReconciliationSummary> ReconcileAsync(Guid engagementId, Guid tbImportId, Guid glImportId,
        CancellationToken cancellationToken = default)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        var trialBalance = await _imports.LoadTrackedAsync(engagementId, tbImportId,
            FinancialDatasetKind.TrialBalance, requireWritable: false, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        var ledger = await _imports.LoadTrackedAsync(engagementId, glImportId,
            FinancialDatasetKind.GeneralLedger, requireWritable: false, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);

        var rows = await _queries.QueryAsync(
            "tb_gl_reconciliation.sql",
            new Dictionary<string, object?>
            {
                ["tb_import_id"] = trialBalance.ImportId,
                ["gl_import_id"] = ledger.ImportId,
            },
            MapReconciliationRow,
            cancellationToken).ConfigureAwait(false);

        return new ReconciliationSummary
        {
            Rows = rows,
            TbImportId = trialBalance.ImportId,
            GlImportId = ledger.ImportId,
            TbLabel = trialBalance.SnapshotLabel,
            GlLabel = ledger.SnapshotLabel,
        };
    }

    /// <summary>
    /// What changed between two ledger snapshots of the same period, by the client's
    /// own transaction identity. This is the roll-forward foundation: it classifies
    /// the movement, it does not yet select a sample.
    /// </summary>
    public async Task<RollForwardSummary> CompareSnapshotsAsync(Guid engagementId, Guid previousImportId,
        Guid currentImportId, CancellationToken cancellationToken = default)
    {
        var (previous, current) = await LoadPairAsync(engagementId, previousImportId, currentImportId,
            cancellationToken).ConfigureAwait(false);

        var totals = await _queries.QueryAsync(
            "gl_snapshot_comparison_summary.sql",
            new Dictionary<string, object?>
            {
                ["previous_import_id"] = previous.ImportId,
                ["current_import_id"] = current.ImportId,
            },
            MapSnapshotTotals,
            cancellationToken).ConfigureAwait(false);
        var summary = totals.FirstOrDefault() ?? SnapshotTotals.Empty;

        return new RollForwardSummary
        {
            EngagementId = engagementId,
            DatasetKind = FinancialDatasetKind.GeneralLedger,
            PreviousImportId = previous.ImportId,
            CurrentImportId = current.ImportId,
            PreviousLabel = previous.SnapshotLabel,
            CurrentLabel = current.SnapshotLabel,
            PreviousThroughDate = previous.CoverageThroughDate,
            CurrentThroughDate = current.CoverageThroughDate,
            UnchangedCount = summary.UnchangedCount,
            ChangedValueCount = summary.ChangedValueCount,
            ChangedAttributesCount = summary.ChangedAttributesCount,
            AddedCount = summary.AddedCount,
            RemovedCount = summary.RemovedCount,
            AddedDebitMinor = summary.AddedDebitMinor,
            AddedCreditMinor = summary.AddedCreditMinor,
            ChangedValueDeltaMinor = summary.ChangedValueDeltaMinor,
            RemovedDebitMinor = summary.RemovedDebitMinor,
            RemovedCreditMinor = summary.RemovedCreditMinor,
        };
    }

    /// <summary>
    /// The individual added, changed, missing and unchanged transactions behind the
    /// summary, paged, with the client's journal and line identity on every row.
    /// </summary>
    public async Task<IReadOnlyList<RollForwardLineRow>> SnapshotChangesAsync(Guid engagementId,
        Guid previousImportId, Guid currentImportId, string state = "ALL", int page = 1, int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var normalized = string.IsNullOrWhiteSpace(state) ? "ALL" : state.Trim().ToUpperInvariant();
        if (normalized != "ALL" && !RollForwardState.All.Contains(normalized))
        {
            throw new ValidationException($"'{state}' is not a roll-forward state.");
        }

        var (previous, current) = await LoadPairAsync(engagementId, previousImportId, currentImportId,
            cancellationToken).ConfigureAwait(false);
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        return await _queries.QueryAsync(
            "gl_snapshot_comparison_detail.sql",
            new Dictionary<string, object?>
            {
                ["previous_import_id"] = previous.ImportId,
                ["current_import_id"] = current.ImportId,
                ["state"] = normalized,
                ["page_size"] = size,
                ["offset"] = (Math.Max(1, page) - 1) * size,
            },
            MapRollForwardLine,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(FinancialDatasetImport Previous, FinancialDatasetImport Current)> LoadPairAsync(
        Guid engagementId, Guid previousImportId, Guid currentImportId, CancellationToken cancellationToken)
    {
        await _authorization.RequireAsync(engagementId, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        var previous = await _imports.LoadTrackedAsync(engagementId, previousImportId,
            FinancialDatasetKind.GeneralLedger, requireWritable: false, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        var current = await _imports.LoadTrackedAsync(engagementId, currentImportId,
            FinancialDatasetKind.GeneralLedger, requireWritable: false, Permissions.ViewEngagement, cancellationToken)
            .ConfigureAwait(false);
        if (previous.ImportId == current.ImportId)
        {
            throw new ValidationException("A snapshot is compared with an earlier snapshot of the same period.");
        }

        if (previous.FinancialPeriodId != current.FinancialPeriodId)
        {
            throw new ValidationException(
                "Snapshots of different financial periods are never compared: they do not cover the same year.");
        }

        if (string.CompareOrdinal(previous.ImportedAtUtc, current.ImportedAtUtc) > 0)
        {
            throw new ValidationException(
                $"Snapshot {current.SnapshotLabel} was imported before {previous.SnapshotLabel}; " +
                "compare the earlier snapshot with the later one.");
        }

        return (previous, current);
    }

    // ------------------------------------------------------------------ mapping

    private static ReconciliationRow MapReconciliationRow(DbDataReader reader)
    {
        var tbBalance = SqlQueryExecutor.GetNullableInt64(reader, "tb_balance_minor");
        var glNet = SqlQueryExecutor.GetNullableInt64(reader, "gl_net_minor");
        var status = tbBalance is null ? ReconciliationStatus.GlOnly
            : glNet is null ? ReconciliationStatus.TbOnly
            : tbBalance == glNet ? ReconciliationStatus.Matched
            : ReconciliationStatus.Difference;
        return new ReconciliationRow(
            reader.GetString(reader.GetOrdinal("account_code")),
            SqlQueryExecutor.GetNullableString(reader, "account_name") ?? string.Empty,
            status,
            tbBalance,
            SqlQueryExecutor.GetNullableInt64(reader, "tb_debit_minor"),
            SqlQueryExecutor.GetNullableInt64(reader, "tb_credit_minor"),
            SqlQueryExecutor.GetNullableInt64(reader, "gl_debit_minor"),
            SqlQueryExecutor.GetNullableInt64(reader, "gl_credit_minor"),
            glNet,
            SqlQueryExecutor.GetNullableInt32(reader, "gl_line_count") ?? 0,
            SqlQueryExecutor.GetNullableInt32(reader, "gl_out_of_period_count") ?? 0);
    }

    private static SnapshotTotals MapSnapshotTotals(DbDataReader reader) => new(
        SqlQueryExecutor.GetNullableInt32(reader, "unchanged_count") ?? 0,
        SqlQueryExecutor.GetNullableInt32(reader, "changed_value_count") ?? 0,
        SqlQueryExecutor.GetNullableInt32(reader, "changed_attributes_count") ?? 0,
        SqlQueryExecutor.GetNullableInt32(reader, "added_count") ?? 0,
        SqlQueryExecutor.GetNullableInt32(reader, "removed_count") ?? 0,
        SqlQueryExecutor.GetNullableInt64(reader, "added_debit_minor") ?? 0,
        SqlQueryExecutor.GetNullableInt64(reader, "added_credit_minor") ?? 0,
        SqlQueryExecutor.GetNullableInt64(reader, "changed_value_delta_minor") ?? 0,
        SqlQueryExecutor.GetNullableInt64(reader, "removed_debit_minor") ?? 0,
        SqlQueryExecutor.GetNullableInt64(reader, "removed_credit_minor") ?? 0);

    private static RollForwardLineRow MapRollForwardLine(DbDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("state")),
        reader.GetString(reader.GetOrdinal("journal_identity")),
        reader.GetString(reader.GetOrdinal("line_identity")),
        SqlQueryExecutor.GetNullableString(reader, "previous_account_code"),
        SqlQueryExecutor.GetNullableString(reader, "current_account_code"),
        SqlQueryExecutor.GetNullableInt64(reader, "previous_debit_minor"),
        SqlQueryExecutor.GetNullableInt64(reader, "previous_credit_minor"),
        SqlQueryExecutor.GetNullableInt64(reader, "current_debit_minor"),
        SqlQueryExecutor.GetNullableInt64(reader, "current_credit_minor"),
        SqlQueryExecutor.GetNullableString(reader, "previous_transaction_date"),
        SqlQueryExecutor.GetNullableString(reader, "current_transaction_date"),
        SqlQueryExecutor.GetNullableString(reader, "previous_description"),
        SqlQueryExecutor.GetNullableString(reader, "current_description"),
        SqlQueryExecutor.GetNullableString(reader, "previous_journal_source"),
        SqlQueryExecutor.GetNullableString(reader, "current_journal_source"));

    private sealed record SnapshotTotals(
        int UnchangedCount,
        int ChangedValueCount,
        int ChangedAttributesCount,
        int AddedCount,
        int RemovedCount,
        long AddedDebitMinor,
        long AddedCreditMinor,
        long ChangedValueDeltaMinor,
        long RemovedDebitMinor,
        long RemovedCreditMinor)
    {
        public static readonly SnapshotTotals Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}
