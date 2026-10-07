using System.Text;
using AuditWorkbench.Application.FinancialData;
using AuditWorkbench.Application.FinancialData.Imports;
using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.FinancialData;
using AuditWorkbench.Domain.FinancialImports;
using Microsoft.Extensions.DependencyInjection;

namespace AuditWorkbench.Application.Tests;

/// <summary>
/// End-to-end trial-balance and ledger import through the real services against a
/// real SQLite workspace: upload, structure detection, validation, commit,
/// versioning and period isolation. Nothing here touches the database directly
/// except to prove what the pipeline actually wrote.
/// </summary>
public sealed class FinancialImportPipelineTests
{
    private const string BalancedTb =
        "Account Code,Account Name,Debit,Credit\n" +
        "1000,Cash and bank,150000.00,0.00\n" +
        "4000,Revenue,0.00,150000.00\n";

    private const string UnbalancedTb =
        "Account Code,Account Name,Debit,Credit\n" +
        "1000,Cash and bank,150000.00,0.00\n" +
        "4000,Revenue,0.00,140000.00\n";

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

    private static MemoryStream File(string content) => new(Encoding.UTF8.GetBytes(content));

    private static Task<Guid> UploadAsync(TestWorkspace workspace, Guid engagementId, string kind, string content,
        string fileName = "source.csv") => workspace.UseAsync(async scope =>
    {
        await using var stream = File(content);
        var summary = await scope.GetRequiredService<FinancialUploadService>()
            .UploadAsync(engagementId, kind, fileName, "text/csv", stream);
        return summary.Record.UploadId;
    });

    [Fact]
    public async Task A_balanced_trial_balance_is_previewed_validated_and_committed_against_its_period()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("TB-OK");
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);

        var upload = await UploadAsync(workspace, engagement, FinancialDatasetKind.TrialBalance, BalancedTb);
        var periodId = await workspace.TextScalarAsync(
            "SELECT financial_period_id FROM financial_period WHERE engagement_id=$id", ("$id", engagement.ToString("D")));

        var outcome = await workspace.UseAsync(scope => scope.GetRequiredService<TrialBalanceImportService>()
            .ValidateAsync(engagement, upload, TbMapping(), allowUnbalanced: false));

        Assert.DoesNotContain(outcome.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.True(outcome.Report.IsBalanced);
        Assert.Equal(15000000, outcome.Report.TotalDebitMinor);
        Assert.Equal(15000000, outcome.Report.TotalCreditMinor);
        Assert.Equal(2, outcome.Report.RowCount);
        Assert.Equal("Account Code", outcome.Structure.Headers[0]);
        Assert.Equal(1, outcome.Structure.HeaderRowNumber);
        Assert.True(outcome.Structure.ColumnCount == 4);
        Assert.True(outcome.CanImport);
        Assert.False(outcome.RequiresUnbalancedOverride);

        var result = await workspace.UseAsync(scope => scope.GetRequiredService<TrialBalanceImportService>()
            .ImportAsync(engagement, upload, TbMapping(), allowUnbalanced: false, allowRepeat: false));

        Assert.Equal(2, result.RowCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.True(result.IsBalanced);
        Assert.True(result.IsActive);
        Assert.Equal(1, result.ImportNo);

        // Every imported row is bound to the engagement's financial period, and the
        // period is the one the engagement was created with.
        Assert.Equal(2, await workspace.ScalarAsync(
            "SELECT count(*) FROM tb_line WHERE engagement_id=$e AND financial_period_id=$p",
            ("$e", engagement.ToString("D")), ("$p", periodId!)));
        Assert.Equal(2, await workspace.ScalarAsync(
            "SELECT count(*) FROM account WHERE engagement_id=$e AND account_origin='TB'",
            ("$e", engagement.ToString("D"))));
        Assert.Equal("TB", await workspace.TextScalarAsync(
            "SELECT account_origin FROM account WHERE engagement_id=$e AND account_code='1000'",
            ("$e", engagement.ToString("D"))));
    }

    [Fact]
    public async Task An_incomplete_mapping_is_reported_as_a_blocking_error_and_writes_nothing()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("TB-MAP");
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var upload = await UploadAsync(workspace, engagement, FinancialDatasetKind.TrialBalance, BalancedTb);

        var withoutCode = ImportColumnMapping.From(new Dictionary<string, int>
        {
            [TbFields.AccountName] = 1,
            [TbFields.Debit] = 2,
            [TbFields.Credit] = 3,
        });

        var outcome = await workspace.UseAsync(scope => scope.GetRequiredService<TrialBalanceImportService>()
            .ValidateAsync(engagement, upload, withoutCode, allowUnbalanced: false));

        Assert.True(outcome.Report.HasErrors);
        Assert.False(outcome.CanImport);
        Assert.Contains(outcome.Report.IssueCounts.Keys,
            code => code is ImportIssueCodes.MissingRequiredColumn or ImportIssueCodes.MissingAccountCode);

        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TrialBalanceImportService>()
                .ImportAsync(engagement, upload, withoutCode, allowUnbalanced: false, allowRepeat: false)));
        Assert.Equal(0, await workspace.ScalarAsync("SELECT count(*) FROM dataset_import"));
    }

    [Fact]
    public async Task An_unbalanced_trial_balance_is_never_committed_without_an_explicit_override()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("TB-OFF");
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var upload = await UploadAsync(workspace, engagement, FinancialDatasetKind.TrialBalance, UnbalancedTb);

        var outcome = await workspace.UseAsync(scope => scope.GetRequiredService<TrialBalanceImportService>()
            .ValidateAsync(engagement, upload, TbMapping(), allowUnbalanced: false));

        Assert.False(outcome.Report.IsBalanced);
        Assert.True(outcome.Report.HasErrors);
        Assert.Equal(1000000, outcome.Report.DifferenceMinor);
        Assert.Contains(ImportIssueCodes.UnbalancedTrialBalance, outcome.Report.IssueCounts.Keys);
        Assert.True(outcome.RequiresUnbalancedOverride);

        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TrialBalanceImportService>()
                .ImportAsync(engagement, upload, TbMapping(), allowUnbalanced: false, allowRepeat: false)));
        Assert.Equal(0, await workspace.ScalarAsync("SELECT count(*) FROM dataset_import"));

        // The operator may still accept the difference explicitly: it is recorded and
        // visible on the import instead of being hidden.
        var accepted = await workspace.UseAsync(scope => scope.GetRequiredService<TrialBalanceImportService>()
            .ImportAsync(engagement, upload, TbMapping(), allowUnbalanced: true, allowRepeat: false));
        Assert.False(accepted.IsBalanced);
        Assert.Equal(1000000, accepted.DifferenceMinor);
        Assert.Equal(DatasetValidationStatus.ValidWithWarnings, accepted.ValidationStatus);
    }

    [Fact]
    public async Task The_same_file_is_not_silently_imported_twice_but_a_new_version_is_allowed()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("TB-VER");
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);
        var upload = await UploadAsync(workspace, engagement, FinancialDatasetKind.TrialBalance, BalancedTb);

        var first = await workspace.UseAsync(scope => scope.GetRequiredService<TrialBalanceImportService>()
            .ImportAsync(engagement, upload, TbMapping(), allowUnbalanced: false, allowRepeat: false));
        Assert.True(first.IsActive);

        var repeat = await workspace.UseAsync(async scope =>
        {
            await using var stream = File(BalancedTb);
            var summary = await scope.GetRequiredService<FinancialUploadService>()
                .UploadAsync(engagement, FinancialDatasetKind.TrialBalance, "source.csv", "text/csv", stream);
            return summary;
        });
        Assert.True(repeat.IsRepeatSource);
        Assert.Single(repeat.PriorImportsWithSameFile);

        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<TrialBalanceImportService>()
                .ImportAsync(engagement, repeat.Record.UploadId, TbMapping(), allowUnbalanced: false,
                    allowRepeat: false)));

        var second = await workspace.UseAsync(scope => scope.GetRequiredService<TrialBalanceImportService>()
            .ImportAsync(engagement, repeat.Record.UploadId, TbMapping(), allowUnbalanced: false, allowRepeat: true));

        Assert.Equal(2, second.ImportNo);
        Assert.True(second.IsActive);

        var versions = await workspace.UseAsync(scope => scope.GetRequiredService<DatasetImportService>()
            .ListAsync(engagement, FinancialDatasetKind.TrialBalance));
        Assert.Equal(2, versions.Count);
        Assert.Equal(1, versions.Count(v => v.IsActive));
        Assert.Equal(2, versions.Single(v => v.IsActive).ImportNo);
        Assert.Contains(versions, v => v.ImportNo == 1 && !v.IsActive && v.SupersededByLabel is not null);
        // Version 1 stays readable: history is superseded, never deleted.
        Assert.Equal(4, await workspace.ScalarAsync("SELECT count(*) FROM tb_line"));
    }

    [Fact]
    public async Task A_ledger_import_lands_with_its_identity_and_flags_out_of_period_rows()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var company = await workspace.CreateCompanyAsync("GL-OK");
        var engagement = await workspace.CreateYearAsync(company, "FY2026", 2026);

        var tb = await UploadAsync(workspace, engagement, FinancialDatasetKind.TrialBalance, BalancedTb);
        await workspace.UseAsync(scope => scope.GetRequiredService<TrialBalanceImportService>()
            .ImportAsync(engagement, tb, TbMapping(), allowUnbalanced: false, allowRepeat: false));

        var ledger = "Journal No,Journal Source,Txn Date,Posting Date,Account Code,Account Name,Description," +
                     "Debit,Credit,Reference,Line No\n" +
                     "JV-0001,GL,2026-03-01,2026-03-01,1000,Cash and bank,March sales,150000.00,0.00,INV-1,1\n" +
                     "JV-0001,GL,2026-03-01,2026-03-01,4000,Revenue,March sales,0.00,150000.00,INV-1,2\n" +
                     "JV-0002,GL,2027-01-05,2027-01-05,1000,Cash and bank,Next year receipt,100.00,0.00,INV-2,1\n" +
                     "JV-0002,GL,2027-01-05,2027-01-05,4000,Revenue,Next year receipt,0.00,100.00,INV-2,2\n";
        var upload = await UploadAsync(workspace, engagement, FinancialDatasetKind.GeneralLedger, ledger, "gl.csv");

        var outcome = await workspace.UseAsync(scope => scope.GetRequiredService<GeneralLedgerImportService>()
            .ValidateAsync(engagement, upload, GlMapping(), allowUnbalanced: false));
        Assert.DoesNotContain(outcome.Report.Issues, issue => issue.Severity == IssueSeverity.Error);
        Assert.Equal(4, outcome.Report.RowCount);
        Assert.Equal(2, outcome.Report.OutOfPeriodCount);
        Assert.Contains(ImportIssueCodes.OutOfPeriod, outcome.Report.IssueCounts.Keys);

        var result = await workspace.UseAsync(scope => scope.GetRequiredService<GeneralLedgerImportService>()
            .ImportAsync(engagement, upload, GlMapping(), allowUnbalanced: false, allowRepeat: false));
        Assert.Equal(4, result.RowCount);
        Assert.Equal(2, result.OutOfPeriodCount);

        // The out-of-period row is imported and flagged, never dropped or moved.
        var flagged = await workspace.UseAsync(scope => scope.GetRequiredService<GeneralLedgerImportService>()
            .OutOfPeriodAsync(engagement, result.ImportId));
        Assert.Equal(2, flagged.Count);
        Assert.All(flagged, line => Assert.Equal("2027-01-05", line.PostingDate ?? line.TransactionDate));

        // A journal line keeps the source identity it came with, not the database key.
        const string identity = "SELECT journal_identity FROM gl_journal WHERE import_id=$i " +
                                "ORDER BY journal_identity LIMIT 1";
        Assert.Contains("JV-0001", (await workspace.TextScalarAsync(identity,
            ("$i", result.ImportId.ToString("D")))) ?? string.Empty);
        Assert.Equal("SOURCE", await workspace.TextScalarAsync(
            "SELECT identity_source FROM gl_journal WHERE import_id=$i ORDER BY journal_identity LIMIT 1",
            ("$i", result.ImportId.ToString("D"))));
        // Line numbers stay the client's own, so a later comparison can line them up.
        Assert.Equal("2", await workspace.TextScalarAsync(
            "SELECT l.source_line_no FROM gl_line l JOIN gl_journal j ON j.gl_journal_id = l.gl_journal_id " +
            "WHERE l.import_id=$i ORDER BY j.journal_identity, l.line_no DESC LIMIT 1",
            ("$i", result.ImportId.ToString("D"))));
    }

    [Fact]
    public async Task Two_years_of_the_same_client_never_share_rows()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var seeded = await workspace.SeedDemoAsync();
        var priorPeriod = await workspace.TextScalarAsync(
            "SELECT financial_period_id FROM financial_period WHERE engagement_id=$e",
            ("$e", seeded.PriorId.ToString("D")));
        var currentPeriod = await workspace.TextScalarAsync(
            "SELECT financial_period_id FROM financial_period WHERE engagement_id=$e",
            ("$e", seeded.CurrentId.ToString("D")));
        Assert.NotNull(priorPeriod);
        Assert.NotNull(currentPeriod);
        Assert.NotEqual(priorPeriod, currentPeriod);

        await workspace.UseAsync(async scope =>
        {
            var uploads = scope.GetRequiredService<FinancialUploadService>();
            await using var stream = File(BalancedTb);
            var summary = await uploads.UploadAsync(seeded.CurrentId, FinancialDatasetKind.TrialBalance,
                "tb-2027.csv", "text/csv", stream);
            await scope.GetRequiredService<TrialBalanceImportService>().ImportAsync(seeded.CurrentId,
                summary.Record.UploadId, TbMapping(), allowUnbalanced: false, allowRepeat: false);
        });

        Assert.Equal(2, await workspace.ScalarAsync(
            "SELECT count(*) FROM tb_line WHERE financial_period_id=$p", ("$p", currentPeriod!)));
        Assert.Equal(0, await workspace.ScalarAsync(
            "SELECT count(*) FROM tb_line WHERE financial_period_id=$p", ("$p", priorPeriod!)));
    }

    [Fact]
    public async Task A_finalized_year_refuses_new_uploads_and_keeps_its_rows_untouched()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var seeded = await workspace.SeedDemoAsync();
        var before = await workspace.ScalarAsync("SELECT count(*) FROM tb_line");

        await Assert.ThrowsAsync<EngagementFinalizedException>(() => UploadAsync(workspace, seeded.PriorId,
            FinancialDatasetKind.TrialBalance, BalancedTb));

        Assert.Equal(0, await workspace.ScalarAsync("SELECT count(*) FROM financial_upload"));
        Assert.Equal(before, await workspace.ScalarAsync("SELECT count(*) FROM tb_line"));

        // The linked open year still accepts imports: isolation, not a global lock.
        var upload = await UploadAsync(workspace, seeded.CurrentId, FinancialDatasetKind.TrialBalance, BalancedTb);
        Assert.NotEqual(Guid.Empty, upload);
    }
}
