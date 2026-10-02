using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.Finalization;
using AuditWorkbench.Application.FinancialData;
using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Application.Tests;

public class FinalizationTests
{
    private static async Task<(TestWorkspace Workspace, Guid CompanyId, Guid EngagementId, Guid AccountId)>
        ArrangeAsync()
    {
        var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var engagementId = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        var accountId = await workspace.AddAccountAsync(engagementId, "4000", "Revenue", "850000000");
        return (workspace, companyId, engagementId, accountId);
    }

    [Fact]
    public async Task PreflightBlocksAYearWithoutValues()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var engagementId = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        await workspace.AddAccountAsync(engagementId, "4000", "Revenue", amount: null);

        var preflight = await workspace.UseAsync(scope =>
            scope.GetRequiredService<FinalizationService>().PreflightAsync(engagementId));

        Assert.False(preflight.CanFinalize);
        Assert.Contains(preflight.BlockingProblems, p => p.Contains("no recorded value"));

        await Assert.ThrowsAsync<ValidationException>(() => workspace.FinalizeAsync(engagementId));

        var summary = await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().GetAsync(engagementId));
        Assert.Equal("DRAFT", summary.Status);
    }

    [Fact]
    public async Task ConfirmationTextMustMatchCompanyAndYear()
    {
        var (workspace, _, engagementId, _) = await ArrangeAsync();
        await using var _disposable = workspace;

        var error = await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<FinalizationService>().FinalizeAsync(engagementId, "FY2026")));
        Assert.Contains("ABC-DEMO FY2026", error.Message);
    }

    [Fact]
    public async Task FinalizationRecordsActorTimeDigestAndEvent()
    {
        var (workspace, _, engagementId, _) = await ArrangeAsync();
        await using var _disposable = workspace;

        var digest = await workspace.FinalizeAsync(engagementId);

        var summary = await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().GetAsync(engagementId));
        Assert.Equal("FINALIZED", summary.Status);
        Assert.Equal(digest, summary.FinalizationDigest);
        Assert.False(string.IsNullOrWhiteSpace(summary.FinalizedAtUtc));
        Assert.False(string.IsNullOrWhiteSpace(summary.FinalizedByDisplayName));

        var verified = await workspace.UseAsync(scope =>
            scope.GetRequiredService<FinalizationService>().VerifyDigestAsync(engagementId));
        Assert.True(verified);

        var events = await workspace.UseAsync(scope =>
            scope.GetRequiredService<Auditing.AuditTrailQuery>().ListAsync(engagementId: engagementId));
        Assert.Contains(events, e => e.EventType == "ENGAGEMENT_FINALIZED" && e.Description.Contains("read-only"));
    }

    [Fact]
    public async Task FailedFinalizationLeavesNoPartialState()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var engagementId = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        await workspace.AddAccountAsync(engagementId, "4000", "Revenue", amount: null);

        var eventsBefore = await workspace.ScalarAsync("SELECT COUNT(*) FROM audit_event");

        await Assert.ThrowsAsync<ValidationException>(() => workspace.FinalizeAsync(engagementId));

        Assert.Equal(0, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM finalization_manifest WHERE engagement_id = $id",
            ("$id", engagementId.ToString("D"))));
        Assert.Equal(eventsBefore, await workspace.ScalarAsync("SELECT COUNT(*) FROM audit_event"));
        Assert.Equal(1, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM engagement WHERE engagement_id = $id AND status = 'DRAFT' " +
            "AND finalized_at_utc IS NULL AND finalization_digest IS NULL",
            ("$id", engagementId.ToString("D"))));
    }

    [Fact]
    public async Task ApplicationRefusesEveryWriteAfterFinalization()
    {
        var (workspace, _, engagementId, accountId) = await ArrangeAsync();
        await using var _disposable = workspace;
        await workspace.FinalizeAsync(engagementId);

        await Assert.ThrowsAsync<EngagementFinalizedException>(
            () => workspace.AddAccountAsync(engagementId, "9999", "Late account", "1"));
        await Assert.ThrowsAsync<EngagementFinalizedException>(
            () => workspace.RecordValueAsync(engagementId, accountId, "999"));
        await Assert.ThrowsAsync<EngagementFinalizedException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().ChangeStatusAsync(engagementId, "IN_PROGRESS")));
        await Assert.ThrowsAsync<EngagementFinalizedException>(() => workspace.FinalizeAsync(engagementId));
    }

    [Theory]
    [InlineData("INSERT INTO account (account_id, engagement_id, account_code, account_name, account_type, " +
                "display_order, created_at_utc, created_by) VALUES ('raw1', $id, 'Z999', 'Raw', 'ASSET', 99, " +
                "'2027-01-01T00:00:00.000Z', '00000000-0000-4000-8000-000000000001')",
        "AWB-GUARD-ACCOUNT-FINALIZED")]
    [InlineData("UPDATE account SET account_name = 'Rewritten' WHERE engagement_id = $id",
        "AWB-GUARD-ACCOUNT-FINALIZED")]
    [InlineData("DELETE FROM account WHERE engagement_id = $id", "AWB-GUARD-ACCOUNT")]
    [InlineData("UPDATE financial_data SET amount_minor = 1 WHERE engagement_id = $id",
        "AWB-GUARD-FINANCIAL-DATA-APPEND-ONLY")]
    [InlineData("DELETE FROM financial_data WHERE engagement_id = $id",
        "AWB-GUARD-FINANCIAL-DATA-APPEND-ONLY")]
    [InlineData("UPDATE engagement SET status = 'DRAFT' WHERE engagement_id = $id",
        "AWB-GUARD-ENGAGEMENT-FINALIZED")]
    [InlineData("DELETE FROM engagement WHERE engagement_id = $id", "AWB-GUARD-ENGAGEMENT-DELETE")]
    [InlineData("UPDATE finalization_manifest SET root_digest = replace(root_digest, 'a', 'b') " +
                "WHERE engagement_id = $id", "AWB-GUARD-MANIFEST-IMMUTABLE")]
    public async Task DatabaseTriggersRejectDirectWritesToAFinalizedYear(string sql, string expectedGuard)
    {
        var (workspace, _, engagementId, _) = await ArrangeAsync();
        await using var _disposable = workspace;
        await workspace.FinalizeAsync(engagementId);

        var error = await workspace.ExpectRawFailureAsync(sql, ("$id", engagementId.ToString("D")));

        Assert.Contains(expectedGuard, error.Message);
        Assert.True(await workspace.UseAsync(scope =>
            scope.GetRequiredService<FinalizationService>().VerifyDigestAsync(engagementId)));
    }

    [Fact]
    public async Task FinalizedStatusCannotBeForgedWithoutAManifest()
    {
        var (workspace, companyId, _, _) = await ArrangeAsync();
        await using var _disposable = workspace;
        var otherYear = await workspace.CreateYearAsync(companyId, "FY2023", 2023);

        var error = await workspace.ExpectRawFailureAsync(
            "UPDATE engagement SET status = 'FINALIZED', finalized_at_utc = '2027-01-01T00:00:00.000Z', " +
            "finalized_by = '00000000-0000-4000-8000-000000000001', finalization_digest = $digest, " +
            "finalization_manifest_version = 'AWB-MANIFEST/1.0' WHERE engagement_id = $id",
            ("$id", otherYear.ToString("D")), ("$digest", new string('0', 64)));

        Assert.Contains("AWB-GUARD-FINALIZATION-MANIFEST", error.Message);
    }

    [Fact]
    public async Task FinalizedValuesRemainReadable()
    {
        var (workspace, _, engagementId, _) = await ArrangeAsync();
        await using var _disposable = workspace;
        await workspace.FinalizeAsync(engagementId);

        var values = await workspace.UseAsync(scope =>
            scope.GetRequiredService<FinancialDataService>().GetLatestValuesAsync(engagementId));

        Assert.Single(values);
        Assert.Equal(85_000_000_000L, values[0].AmountMinor);
    }

    [Fact]
    public async Task DraftCorrectionsAppendRevisions()
    {
        var (workspace, _, engagementId, accountId) = await ArrangeAsync();
        await using var _disposable = workspace;

        await workspace.RecordValueAsync(engagementId, accountId, "860000000", "Cut-off adjustment");

        var history = await workspace.UseAsync(scope =>
            scope.GetRequiredService<FinancialDataService>().GetHistoryAsync(engagementId, accountId));

        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[0].RevisionNo);
        Assert.Equal(86_000_000_000L, history[0].AmountMinor);
        Assert.Equal(85_000_000_000L, history[1].AmountMinor);
    }
}
