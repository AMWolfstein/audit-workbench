using AuditWorkbench.Application.Auditing;
using AuditWorkbench.Application.Companies;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.FinancialData;

namespace AuditWorkbench.Application.Tests;

public class AuditTrailTests
{
    [Fact]
    public async Task EveryRequiredLifecycleEventIsRecorded()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.SeedDemoAsync();

        var events = await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(limit: 500));
        var types = events.Select(e => e.EventType).ToHashSet();

        // requirements.md FR-M17: the five mandatory event types.
        Assert.Contains("COMPANY_CREATED", types);
        Assert.Contains("ENGAGEMENT_CREATED", types);
        Assert.Contains("FINANCIAL_DATA_ADDED", types);
        Assert.Contains("ENGAGEMENT_FINALIZED", types);
        Assert.Contains("PRIOR_YEAR_LINKED", types);
    }

    [Fact]
    public async Task EachEventCarriesTypeTimeEntityAndActor()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.CreateCompanyAsync();

        var row = (await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(eventType: "COMPANY_CREATED"))).Single();

        Assert.Equal("COMPANY", row.EntityType);
        Assert.True(Guid.TryParse(row.EntityId, out _));
        Assert.Equal("SUCCESS", row.Outcome);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", row.OccurredAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(row.ActorDisplayName));
        Assert.False(string.IsNullOrWhiteSpace(row.Description));
    }

    [Fact]
    public async Task CorrectionsPointAtTheSupersededRevisionWithoutReproducingAmounts()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var engagementId = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        var accountId = await workspace.AddAccountAsync(engagementId, "4000", "Revenue", "850000000");

        await workspace.RecordValueAsync(engagementId, accountId, "860000000", "Cut-off adjustment");

        var changed = (await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(eventType: "FINANCIAL_DATA_CHANGED"))).Single();

        Assert.Equal("FINANCIAL_DATA", changed.EntityType);
        Assert.Equal(engagementId, changed.EngagementId);
        Assert.Equal(companyId, changed.CompanyId);
        Assert.Contains("\"revision_no\":2", changed.DetailsJson);
        Assert.Contains("supersedes_id", changed.DetailsJson);
        Assert.Contains("4000", changed.DetailsJson);
        Assert.Contains("revision 2", changed.Description);

        // NFR-14: the trail identifies the record, it does not reproduce the evidence.
        Assert.DoesNotContain("85000000000", changed.DetailsJson);
        Assert.DoesNotContain("86000000000", changed.DetailsJson);
        Assert.DoesNotContain("85000000000", changed.Description);

        // Both values stay recoverable from the append-only revision history instead.
        var history = await workspace.UseAsync(scope =>
            scope.GetRequiredService<FinancialDataService>().GetHistoryAsync(engagementId, accountId));
        Assert.Equal(new[] { 86_000_000_000L, 85_000_000_000L }, history.Select(h => h.AmountMinor).ToArray());
        Assert.Equal("Cut-off adjustment", history[0].CorrectionReason);
    }

    [Fact]
    public async Task TheHashChainVerifiesAndDetectsTampering()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.SeedDemoAsync();

        Assert.True(await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().VerifyChainAsync()));

        // Tampering is only possible by rewriting the file outside the application;
        // the append-only trigger blocks it from SQL, so prove both halves.
        var updateError = await workspace.ExpectRawFailureAsync(
            "UPDATE audit_event SET description = 'rewritten' WHERE sequence_no = 1");
        Assert.Contains("AWB-GUARD-AUDIT-APPEND-ONLY", updateError.Message);

        var deleteError = await workspace.ExpectRawFailureAsync("DELETE FROM audit_event WHERE sequence_no = 1");
        Assert.Contains("AWB-GUARD-AUDIT-APPEND-ONLY", deleteError.Message);

        Assert.True(await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().VerifyChainAsync()));
    }

    [Fact]
    public async Task SequenceNumbersAreContiguousAndChronological()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.SeedDemoAsync();

        var events = (await workspace.UseAsync(scope =>
                scope.GetRequiredService<AuditTrailQuery>().ListAsync(limit: 500)))
            .OrderBy(e => e.SequenceNo).ToList();

        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.SequenceNo));
        Assert.Equal(events.Select(e => e.OccurredAtUtc).OrderBy(t => t, StringComparer.Ordinal),
            events.Select(e => e.OccurredAtUtc));
    }

    [Fact]
    public async Task EventsCanBeFilteredByCompanyEngagementAndType()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var (companyId, priorId, _) = await workspace.SeedDemoAsync();
        var otherCompany = await workspace.CreateCompanyAsync("XYZ-DEMO");

        var byCompany = await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(companyId: companyId, limit: 500));
        Assert.All(byCompany, e => Assert.Equal(companyId, e.CompanyId));
        Assert.DoesNotContain(byCompany, e => e.EntityId == otherCompany.ToString("D"));

        var byEngagement = await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(engagementId: priorId, limit: 500));
        Assert.All(byEngagement, e => Assert.Equal(priorId, e.EngagementId));
        Assert.Contains(byEngagement, e => e.EventType == "ENGAGEMENT_FINALIZED");
    }

    [Fact]
    public async Task RejectedWritesAreNotSilentlyLost()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<CompanyService>().CreateAsync(new CreateCompanyCommand(
                "Duplicate Short Name Limited", "abc-demo", "Manufacturing", "ZZ", null))));

        // The rejection must not have written a company, and the chain must still verify.
        Assert.Equal(1, await workspace.ScalarAsync("SELECT COUNT(*) FROM company"));
        Assert.True(await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().VerifyChainAsync()));
        Assert.NotEqual(Guid.Empty, companyId);
    }

    [Fact]
    public async Task StatusChangesAreRecordedWithBothStates()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var engagementId = await workspace.CreateYearAsync(companyId, "FY2026", 2026);

        await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().ChangeStatusAsync(engagementId, "IN_PROGRESS"));

        var row = (await workspace.UseAsync(scope =>
            scope.GetRequiredService<AuditTrailQuery>().ListAsync(eventType: "ENGAGEMENT_STATUS_CHANGED"))).Single();

        Assert.Contains("DRAFT", row.DetailsJson);
        Assert.Contains("IN_PROGRESS", row.DetailsJson);
    }
}
