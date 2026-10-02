using AuditWorkbench.Application.Comparison;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Application.FinancialData;
using AuditWorkbench.Domain.Common;
using Xunit;

namespace AuditWorkbench.Application.Tests;

public class YearIsolationTests
{
    [Fact]
    public async Task TwoYearsOfOneCompanyAreSeparateEngagementRecords()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();

        var fy2026 = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        var fy2027 = await workspace.CreateYearAsync(companyId, "FY2027", 2027);

        Assert.NotEqual(fy2026, fy2027);

        var years = await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().ListByCompanyAsync(companyId));
        Assert.Equal(2, years.Count);
        Assert.Contains(years, y => y.Label == "FY2026");
        Assert.Contains(years, y => y.Label == "FY2027");
    }

    [Fact]
    public async Task DuplicateFinancialYearForTheSameCompanyIsRejected()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        await workspace.CreateYearAsync(companyId, "FY2026", 2026);

        var error = await Assert.ThrowsAsync<ValidationException>(
            () => workspace.CreateYearAsync(companyId, "FY2026", 2026));
        Assert.Contains("already has", error.Message);
    }

    [Fact]
    public async Task ValuesCannotReferenceAnAccountOfAnotherYear()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var fy2026 = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        var fy2027 = await workspace.CreateYearAsync(companyId, "FY2027", 2027);
        var accountIn2026 = await workspace.AddAccountAsync(fy2026, "4000", "Revenue", "850000000");

        var error = await Assert.ThrowsAsync<ValidationException>(
            () => workspace.RecordValueAsync(fy2027, accountIn2026, "920000000"));
        Assert.Contains("does not belong to this financial year", error.Message);

        // The composite ownership foreign key refuses it even through raw SQL.
        var sqlError = await workspace.ExpectRawFailureAsync(
            "INSERT INTO financial_data (financial_data_id, engagement_id, account_id, revision_no, amount_minor, " +
            "currency_code, recorded_at_utc, recorded_by) VALUES ('raw', $engagement, $account, 1, 1, 'USD', " +
            "'2027-01-01T00:00:00.000Z', '00000000-0000-4000-8000-000000000001')",
            ("$engagement", fy2027.ToString("D")),
            ("$account", accountIn2026.ToString("D")));
        Assert.Contains("FOREIGN KEY", sqlError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CurrentYearWorkDoesNotChangeThePriorYear()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var (_, priorId, currentId) = await workspace.SeedDemoAsync();

        var before = await SnapshotAsync(workspace, priorId);

        // Heavy current-year activity.
        await workspace.AddAccountAsync(currentId, "5000", "Cost of sales", "415000000");
        var revenue = (await workspace.UseAsync(scope =>
                scope.GetRequiredService<FinancialDataService>().GetLatestValuesAsync(currentId)))
            .Single(row => row.AccountCode == "4000");
        await workspace.RecordValueAsync(currentId, revenue.AccountId, "935000000", "Cut-off adjustment");
        await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().ChangeStatusAsync(currentId, "IN_PROGRESS"));
        await workspace.UseAsync(scope => scope.GetRequiredService<ComparisonService>().GetAsync(currentId));

        var after = await SnapshotAsync(workspace, priorId);
        Assert.Equal(before, after);

        var digestValid = await workspace.UseAsync(scope =>
            scope.GetRequiredService<Finalization.FinalizationService>().VerifyDigestAsync(priorId));
        Assert.True(digestValid);
    }

    [Fact]
    public async Task PriorYearIsReferencedNotDuplicated()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var (_, priorId, currentId) = await workspace.SeedDemoAsync();

        var relationships = await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM prior_year_relationship WHERE current_engagement_id = $id AND prior_engagement_id = $prior",
            ("$id", currentId.ToString("D")), ("$prior", priorId.ToString("D")));
        Assert.Equal(1, relationships);

        var sharedAccountIds = await workspace.ScalarAsync(
            "SELECT COUNT(*) FROM account a JOIN account b ON a.account_id = b.account_id " +
            "WHERE a.engagement_id = $prior AND b.engagement_id = $current",
            ("$prior", priorId.ToString("D")), ("$current", currentId.ToString("D")));
        Assert.Equal(0, sharedAccountIds);
    }

    [Fact]
    public async Task PriorYearMustBelongToTheSameCompany()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyA = await workspace.CreateCompanyAsync("AAA-DEMO");
        var companyB = await workspace.CreateCompanyAsync("BBB-DEMO");
        var priorOfA = await workspace.CreateYearAsync(companyA, "FY2026", 2026);
        await workspace.AddAccountAsync(priorOfA, "4000", "Revenue", "100");
        await workspace.FinalizeAsync(priorOfA);
        var currentOfB = await workspace.CreateYearAsync(companyB, "FY2027", 2027);

        var error = await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().LinkPriorYearAsync(currentOfB, priorOfA)));
        Assert.Contains("same company", error.Message);
    }

    [Fact]
    public async Task PriorYearLinkIsImmutable()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var (_, priorId, currentId) = await workspace.SeedDemoAsync();

        var error = await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().LinkPriorYearAsync(currentId, priorId)));
        Assert.Contains("immutable", error.Message);

        var updateError = await workspace.ExpectRawFailureAsync(
            "UPDATE prior_year_relationship SET prior_engagement_id = current_engagement_id");
        Assert.Contains("AWB-GUARD-PRIOR-YEAR-IMMUTABLE", updateError.Message);

        var deleteError = await workspace.ExpectRawFailureAsync("DELETE FROM prior_year_relationship");
        Assert.Contains("AWB-GUARD-PRIOR-YEAR-IMMUTABLE", deleteError.Message);
    }

    private static async Task<string> SnapshotAsync(TestWorkspace workspace, Guid engagementId)
    {
        await using var connection = workspace.OpenRawConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT group_concat(account_id || ':' || account_code || ':' || account_name, '|') " +
            "        FROM (SELECT * FROM account WHERE engagement_id = $id ORDER BY account_code)) || '#' || " +
            "       (SELECT group_concat(financial_data_id || ':' || revision_no || ':' || amount_minor, '|') " +
            "        FROM (SELECT * FROM financial_data WHERE engagement_id = $id ORDER BY account_id, revision_no)) " +
            "       || '#' || (SELECT status || coalesce(finalized_at_utc, '') || coalesce(finalization_digest, '') " +
            "        FROM engagement WHERE engagement_id = $id)";
        command.Parameters.AddWithValue("$id", engagementId.ToString("D"));
        return (await command.ExecuteScalarAsync())?.ToString() ?? string.Empty;
    }
}
