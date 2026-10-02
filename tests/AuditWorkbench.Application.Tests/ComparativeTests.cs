using AuditWorkbench.Application.Comparison;
using AuditWorkbench.Application.Finalization;
using AuditWorkbench.Application.FinancialData;

namespace AuditWorkbench.Application.Tests;

public class ComparativeTests
{
    [Fact]
    public async Task DemoScenarioMatchesTheDocumentedOutput()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var (_, _, currentId) = await workspace.SeedDemoAsync();

        var view = await workspace.UseAsync(scope =>
            scope.GetRequiredService<ComparisonService>().GetAsync(currentId));

        Assert.NotNull(view.Prior);
        Assert.Equal("FY2026", view.Prior!.Label);

        var revenue = view.Rows.Single(r => r.AccountCode == "4000");
        Assert.Equal(85_000_000_000L, revenue.PriorAmountMinor);
        Assert.Equal(92_000_000_000L, revenue.CurrentAmountMinor);
        Assert.Equal(7_000_000_000L, revenue.ChangeAmountMinor);
        Assert.Equal("8.24", revenue.ChangePercentDisplay);

        Assert.Equal("16.67", view.Rows.Single(r => r.AccountCode == "1200").ChangePercentDisplay);
        Assert.Equal("14.58", view.Rows.Single(r => r.AccountCode == "1300").ChangePercentDisplay);
    }

    [Fact]
    public async Task ComparisonDoesNotModifyThePriorYear()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var (_, priorId, currentId) = await workspace.SeedDemoAsync();

        var eventsBefore = await workspace.ScalarAsync("SELECT COUNT(*) FROM audit_event");
        var rowsBefore = await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM financial_data WHERE engagement_id = $id", ("$id", priorId.ToString("D")));

        for (var i = 0; i < 3; i++)
        {
            await workspace.UseAsync(scope => scope.GetRequiredService<ComparisonService>().GetAsync(currentId));
        }

        Assert.Equal(eventsBefore, await workspace.ScalarAsync("SELECT COUNT(*) FROM audit_event"));
        Assert.Equal(rowsBefore, await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM financial_data WHERE engagement_id = $id", ("$id", priorId.ToString("D"))));
        Assert.True(await workspace.UseAsync(scope =>
            scope.GetRequiredService<FinalizationService>().VerifyDigestAsync(priorId)));
    }

    [Fact]
    public async Task WithoutARelationshipThereIsNoPriorColumn()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var fy2026 = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        await workspace.AddAccountAsync(fy2026, "4000", "Revenue", "850000000");
        await workspace.FinalizeAsync(fy2026);

        var unlinked = await workspace.CreateYearAsync(companyId, "FY2027", 2027);
        await workspace.AddAccountAsync(unlinked, "4000", "Revenue", "920000000");

        var view = await workspace.UseAsync(scope =>
            scope.GetRequiredService<ComparisonService>().GetAsync(unlinked));

        Assert.Null(view.Prior);
        Assert.Single(view.Rows);
        Assert.Null(view.Rows[0].PriorAmountMinor);
        Assert.True(view.Rows[0].IsNewAccount);
        Assert.Equal("N/A", view.Rows[0].ChangePercentDisplay);
        Assert.NotNull(view.Warning);
    }

    [Fact]
    public async Task ZeroPriorAmountIsReportedAsNotAvailable()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var prior = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        await workspace.AddAccountAsync(prior, "4000", "Revenue", "0");
        await workspace.FinalizeAsync(prior);
        var current = await workspace.CreateYearAsync(companyId, "FY2027", 2027, prior);
        await workspace.AddAccountAsync(current, "4000", "Revenue", "500");

        var row = (await workspace.UseAsync(scope =>
            scope.GetRequiredService<ComparisonService>().GetAsync(current))).Rows.Single();

        Assert.Equal(50_000L, row.ChangeAmountMinor);
        Assert.Equal("N/A", row.ChangePercentDisplay);
    }

    [Fact]
    public async Task ComparisonResolvesTheLatestRevisionOfEachYear()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var (_, _, currentId) = await workspace.SeedDemoAsync();

        var revenue = (await workspace.UseAsync(scope =>
                scope.GetRequiredService<FinancialDataService>().GetLatestValuesAsync(currentId)))
            .Single(r => r.AccountCode == "4000");
        await workspace.RecordValueAsync(currentId, revenue.AccountId, "935000000", "Cut-off adjustment");

        var row = (await workspace.UseAsync(scope =>
                scope.GetRequiredService<ComparisonService>().GetAsync(currentId)))
            .Rows.Single(r => r.AccountCode == "4000");

        Assert.Equal(2, row.CurrentRevisionNo);
        Assert.Equal(93_500_000_000L, row.CurrentAmountMinor);
        Assert.Equal(1, row.PriorRevisionNo);
        Assert.Equal(85_000_000_000L, row.PriorAmountMinor);
    }

    [Fact]
    public async Task NewCurrentYearAccountsAreLabelled()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var (_, _, currentId) = await workspace.SeedDemoAsync();
        await workspace.AddAccountAsync(currentId, "5000", "Cost of sales", "415000000");

        var row = (await workspace.UseAsync(scope =>
                scope.GetRequiredService<ComparisonService>().GetAsync(currentId)))
            .Rows.Single(r => r.AccountCode == "5000");

        Assert.True(row.IsNewAccount);
        Assert.Equal("New this year", row.StatusLabel);
    }
}
