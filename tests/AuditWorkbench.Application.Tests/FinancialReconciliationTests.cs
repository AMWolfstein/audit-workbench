using System.Text;
using AuditWorkbench.Application.FinancialData;
using AuditWorkbench.Application.FinancialData.Imports;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;
using Microsoft.Extensions.DependencyInjection;

namespace AuditWorkbench.Application.Tests;

/// <summary>
/// The comparisons an auditor actually acts on: a trial balance against the ledger
/// it came from, and a ledger snapshot against the snapshot that was examined
/// earlier. Both are checked for the distinctions the phase promises — matched vs
/// different, added vs changed vs missing — never for a "looks fine" answer.
/// </summary>
public sealed class FinancialReconciliationTests
{
    private const string Tb =
        "Account Code,Account Name,Debit,Credit\n" +
        "1000,Cash and bank,150000.00,0.00\n" +
        "4000,Revenue,0.00,150000.00\n";

    private const string LedgerHeader =
        "Journal No,Journal Source,Txn Date,Posting Date,Account Code,Account Name,Description," +
        "Debit,Credit,Reference,Line No\n";

    /// <summary>The ledger the trial balance was drawn from: the two lines agree exactly.</summary>
    private const string MatchingLedger = LedgerHeader +
        "JV-0001,GL,2026-03-01,2026-03-01,1000,Cash and bank,March sales,150000.00,0.00,INV-1,1\n" +
        "JV-0001,GL,2026-03-01,2026-03-01,4000,Revenue,March sales,0.00,150000.00,INV-1,2\n";

    /// <summary>Snapshot 1: JV-0001, JV-0002 and a receipt JV-0004 that snapshot 2 drops.</summary>
    private const string SnapshotOne = LedgerHeader +
        "JV-0001,GL,2026-03-01,2026-03-01,1000,Cash and bank,March sales,150000.00,0.00,INV-1,1\n" +
        "JV-0001,GL,2026-03-01,2026-03-01,4000,Revenue,March sales,0.00,150000.00,INV-1,2\n" +
        "JV-0002,GL,2026-04-01,2026-04-01,1000,Cash and bank,April receipt,500.00,0.00,INV-2,1\n" +
        "JV-0002,GL,2026-04-01,2026-04-01,4000,Revenue,April receipt,0.00,500.00,INV-2,2\n" +
        "JV-0004,GL,2026-05-01,2026-05-01,1000,Cash and bank,Receipt later reversed,200.00,0.00,INV-4,1\n";

    /// <summary>
    /// Snapshot 2: JV-0001 line 1 moved by 100.00 (value change, line 2 untouched),
    /// JV-0002 restated in words only (attribute change), JV-0003 is new, and the
    /// JV-0004 receipt of snapshot 1 is gone.
    /// </summary>
    private const string SnapshotTwo = LedgerHeader +
        "JV-0001,GL,2026-03-01,2026-03-01,1000,Cash and bank,March sales,160000.00,0.00,INV-1,1\n" +
        "JV-0001,GL,2026-03-01,2026-03-01,4000,Revenue,March sales,0.00,150000.00,INV-1,2\n" +
        "JV-0002,GL,2026-04-01,2026-04-01,1000,Cash and bank,April receipt restated,500.00,0.00,INV-2,1\n" +
        "JV-0002,GL,2026-04-01,2026-04-01,4000,Revenue,April receipt restated,0.00,500.00,INV-2,2\n" +
        "JV-0003,GL,2026-05-01,2026-05-01,1000,Cash and bank,May receipt,90.00,0.00,INV-3,1\n";

    private static ImportColumnMapping TbMapping() => ImportColumnMapping.From(new Dictionary<string, int>
    {
        [TbFields.AccountCode] = 0,
        [TbFields.AccountName] = 1,
        [TbFields.Debit] = 2,
        [TbFields.Credit] = 3,
    });

    private static ImportColumnMapping GlMapping() => ImportColumnMapping.From(new Dictionary<string, int>
    {
        [GlFields.JournalNumber] = 0,
        [GlFields.JournalSource] = 1,
        [GlFields.TransactionDate] = 2,
        [GlFields.PostingDate] = 3,
        [GlFields.AccountCode] = 4,
        [GlFields.AccountName] = 5,
        [GlFields.Description] = 6,
        [GlFields.Debit] = 7,
        [GlFields.Credit] = 8,
        [GlFields.Reference] = 9,
        [GlFields.LineNumber] = 10,
    });

    private static Task<Guid> ImportTbAsync(TestWorkspace workspace, Guid engagement, string content) =>
        workspace.UseAsync(async scope =>
        {
            var uploads = scope.GetRequiredService<FinancialUploadService>();
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            var upload = await uploads.UploadAsync(engagement, FinancialDatasetKind.TrialBalance, "tb.csv",
                "text/csv", stream);
            var result = await scope.GetRequiredService<TrialBalanceImportService>().ImportAsync(engagement,
                upload.Record.UploadId, TbMapping(), allowUnbalanced: false, allowRepeat: false);
            return result.ImportId;
        });

    private static Task<Guid> ImportGlAsync(TestWorkspace workspace, Guid engagement, string content,
        string fileName) => workspace.UseAsync(async scope =>
    {
        var uploads = scope.GetRequiredService<FinancialUploadService>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var upload = await uploads.UploadAsync(engagement, FinancialDatasetKind.GeneralLedger, fileName,
            "text/csv", stream);
        var result = await scope.GetRequiredService<GeneralLedgerImportService>().ImportAsync(engagement,
            upload.Record.UploadId, GlMapping(), allowUnbalanced: false, allowRepeat: false);
        return result.ImportId;
    });

    [Fact]
    public async Task A_trial_balance_reconciles_against_the_ledger_it_came_from()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("REC-MATCH");
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var tb = await ImportTbAsync(workspace, engagement, Tb);
        var gl = await ImportGlAsync(workspace, engagement, MatchingLedger, "gl-match.csv");

        var summary = await workspace.UseAsync(scope => scope.GetRequiredService<FinancialReconciliationService>()
            .ReconcileAsync(engagement, tb, gl));

        Assert.Equal(2, summary.MatchedCount);
        Assert.Equal(0, summary.DifferenceCount);
        Assert.Equal(0, summary.GlOnlyCount);
        Assert.Equal(0, summary.TbOnlyCount);
        Assert.Empty(summary.Exceptions);
        Assert.Equal(0, summary.DifferenceMinor);
        Assert.True(summary.IsFullyReconciled);
        Assert.All(summary.Rows, row => Assert.False(row.HasDifference));
    }

    [Fact]
    public async Task A_moved_ledger_is_a_listed_difference_not_a_hidden_one()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("REC-DIFF");
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var tb = await ImportTbAsync(workspace, engagement, Tb);
        var gl = await ImportGlAsync(workspace, engagement, SnapshotTwo, "gl-2.csv");

        var summary = await workspace.UseAsync(scope => scope.GetRequiredService<FinancialReconciliationService>()
            .ReconcileAsync(engagement, tb, gl));

        Assert.False(summary.IsFullyReconciled);
        Assert.Equal(2, summary.DifferenceCount);
        Assert.Equal(2, summary.Exceptions.Count);
        Assert.All(summary.Exceptions, row => Assert.Equal(ReconciliationStatus.Difference, row.Status));

        // Cash: 150,000.00 in the trial balance against 160,000.00 + 500.00 of ledger
        // movement, all debit-positive.
        var cash = summary.Rows.Single(row => row.AccountCode == "1000");
        Assert.Equal(15000000, cash.TbBalanceMinor);
        Assert.Equal(16059000, cash.GlNetMinor);
        Assert.Equal(-1059000, cash.DifferenceMinor);
        Assert.True(cash.HasDifference);
        Assert.Equal(3, cash.GlLineCount);
    }

    [Fact]
    public async Task A_ledger_account_missing_from_the_trial_balance_is_never_hidden()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("REC-GLONLY");
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var tb = await ImportTbAsync(workspace, engagement, Tb);
        var ledger = LedgerHeader +
                     "JV-0009,GL,2026-06-01,2026-06-01,5300,Repairs and maintenance,Repair invoice,75.00,0.00,INV-9,1\n" +
                     "JV-0009,GL,2026-06-01,2026-06-01,1000,Cash and bank,Repair invoice,0.00,75.00,INV-9,2\n";
        var gl = await ImportGlAsync(workspace, engagement, ledger, "gl-only.csv");

        var summary = await workspace.UseAsync(scope => scope.GetRequiredService<FinancialReconciliationService>()
            .ReconcileAsync(engagement, tb, gl));

        var missing = summary.Rows.Single(row => row.AccountCode == "5300");
        Assert.Equal(ReconciliationStatus.GlOnly, missing.Status);
        Assert.Null(missing.TbBalanceMinor);
        Assert.Equal(7500, missing.GlNetMinor);
        Assert.Equal(1, summary.GlOnlyCount);
        Assert.Contains(missing, summary.Exceptions);
        Assert.Equal(ReconciliationStatus.Difference,
            summary.Rows.Single(row => row.AccountCode == "1000").Status);
    }

    [Fact]
    public async Task Roll_forward_distinguishes_changed_values_changed_attributes_added_and_removed()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("ROLLFWD");
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        await ImportTbAsync(workspace, engagement, Tb);
        var first = await ImportGlAsync(workspace, engagement, SnapshotOne, "gl-1.csv");
        var second = await ImportGlAsync(workspace, engagement, SnapshotTwo, "gl-2.csv");

        var summary = await workspace.UseAsync(scope => scope.GetRequiredService<FinancialReconciliationService>()
            .CompareSnapshotsAsync(engagement, first, second));

        Assert.Equal(1, summary.UnchangedCount);
        Assert.Equal(1, summary.ChangedValueCount);
        Assert.Equal(2, summary.ChangedAttributesCount);
        Assert.Equal(1, summary.AddedCount);
        Assert.Equal(1, summary.RemovedCount);
        Assert.Equal(6, summary.TotalCompared);
        Assert.Equal(4, summary.ChangeOrAdditionCount);
        Assert.False(summary.IsIdentical);
        Assert.NotNull(summary.PreviousLabel);
        Assert.NotEqual(summary.PreviousLabel, summary.CurrentLabel);

        // A changed amount moves the net by the difference; attribute-only edits
        // and removals are reported with their own totals.
        Assert.Equal(1000000, summary.ChangedValueDeltaMinor);
        Assert.Equal(9000, summary.AddedDebitMinor);
        Assert.Equal(20000, summary.RemovedDebitMinor);
        Assert.Equal(0, summary.RemovedCreditMinor);

        var changedValues = await workspace.UseAsync(scope => scope
            .GetRequiredService<FinancialReconciliationService>()
            .SnapshotChangesAsync(engagement, first, second, RollForwardState.ChangedValue));
        var moved = Assert.Single(changedValues);
        Assert.Equal("SRC:JV-0001", moved.JournalIdentity);
        Assert.Equal(16000000, moved.CurrentDebitMinor);
        Assert.Equal(15000000, moved.PreviousDebitMinor);
        Assert.Equal("1000", moved.CurrentAccountCode);

        var added = await workspace.UseAsync(scope => scope.GetRequiredService<FinancialReconciliationService>()
            .SnapshotChangesAsync(engagement, first, second, RollForwardState.Added));
        Assert.Single(added);
        Assert.Equal("SRC:JV-0003", added[0].JournalIdentity);
        Assert.Null(added[0].PreviousDebitMinor);
        Assert.Equal(9000, added[0].CurrentDebitMinor);
        Assert.True(RollForwardState.IsChangeOrAddition(added[0].State));

        var removed = await workspace.UseAsync(scope => scope.GetRequiredService<FinancialReconciliationService>()
            .SnapshotChangesAsync(engagement, first, second, RollForwardState.Removed));
        Assert.Single(removed);
        Assert.Equal("SRC:JV-0004", removed[0].JournalIdentity);
        Assert.Null(removed[0].CurrentDebitMinor);
        Assert.Equal(20000, removed[0].PreviousDebitMinor);

        var attributeOnly = await workspace.UseAsync(scope => scope
            .GetRequiredService<FinancialReconciliationService>()
            .SnapshotChangesAsync(engagement, first, second, RollForwardState.ChangedAttributes));
        Assert.Equal(2, attributeOnly.Count);
        Assert.All(attributeOnly, row =>
        {
            Assert.Equal("SRC:JV-0002", row.JournalIdentity);
            // Same money, different words: the movement is in the description only.
            Assert.Equal(row.PreviousDebitMinor, row.CurrentDebitMinor);
            Assert.NotEqual(row.PreviousDescription, row.CurrentDescription);
        });
    }

    [Fact]
    public async Task A_snapshot_of_another_engagement_is_never_compared()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("REC-OWN");
        var current = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var other = await workspace.CreateYearAsync(company, "FY2027", 2027);
        await ImportTbAsync(workspace, other, Tb);
        var mine = await ImportGlAsync(workspace, current, SnapshotOne, "mine.csv");
        var foreign = await ImportGlAsync(workspace, other, SnapshotTwo, "foreign.csv");

        // The id belongs to another engagement: the request is refused instead of
        // comparing data the caller has no business reading.
        await Assert.ThrowsAsync<NotFoundException>(() => workspace.UseAsync(scope => scope
            .GetRequiredService<FinancialReconciliationService>()
            .CompareSnapshotsAsync(engagementId: current, previousImportId: foreign, currentImportId: mine)));

        await Assert.ThrowsAsync<NotFoundException>(() => workspace.UseAsync(scope => scope
            .GetRequiredService<FinancialReconciliationService>()
            .CompareSnapshotsAsync(engagementId: current, previousImportId: mine, currentImportId: foreign)));
    }
}
