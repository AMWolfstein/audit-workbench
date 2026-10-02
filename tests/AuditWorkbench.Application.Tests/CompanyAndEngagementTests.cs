using AuditWorkbench.Application.Companies;
using AuditWorkbench.Application.Engagements;
using AuditWorkbench.Domain.Common;

namespace AuditWorkbench.Application.Tests;

public class CompanyAndEngagementTests
{
    [Fact]
    public async Task CompanyIsStoredWithAllRequestedAttributes()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();

        var company = await workspace.UseAsync(scope =>
            scope.GetRequiredService<CompanyService>().GetAsync(companyId));

        Assert.Equal("ABC-DEMO (Demo) Limited", company.LegalName);
        Assert.Equal("ABC-DEMO", company.ShortName);
        Assert.Equal("Manufacturing", company.Industry);
        Assert.Equal("ZZ", company.CountryCode);
        Assert.Equal("DEMO-TAX-0000001", company.TaxReference);
        Assert.Equal("ACTIVE", company.Status);
    }

    [Fact]
    public async Task TaxReferenceIsOptional()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.UseAsync(scope =>
            scope.GetRequiredService<CompanyService>().CreateAsync(new CreateCompanyCommand(
                "No Reference Limited", "NOREF", "Services", "ZZ", null)));

        var company = await workspace.UseAsync(scope =>
            scope.GetRequiredService<CompanyService>().GetAsync(companyId));
        Assert.Null(company.TaxReference);
    }

    [Theory]
    [InlineData("", "SHORT", "Services", "ZZ")]
    [InlineData("Legal Name Limited", "", "Services", "ZZ")]
    [InlineData("Legal Name Limited", "SHORT", "", "ZZ")]
    [InlineData("Legal Name Limited", "SHORT", "Services", "ZZZ")]
    [InlineData("Legal Name Limited", "SHORT", "Services", "")]
    public async Task InvalidCompanyInputIsRejected(string legal, string shortName, string industry, string country)
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<CompanyService>().CreateAsync(
                new CreateCompanyCommand(legal, shortName, industry, country, null))));

        Assert.Equal(0, await workspace.ScalarAsync("SELECT COUNT(*) FROM company"));
    }

    [Fact]
    public async Task ShortNameIsUniqueIgnoringCase()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        await workspace.CreateCompanyAsync("ABC-DEMO");

        var error = await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<CompanyService>().CreateAsync(new CreateCompanyCommand(
                "Another Legal Name Limited", "abc-demo", "Services", "ZZ", null))));

        Assert.Contains("short name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompaniesAreListedWithTheirYearCounts()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        await workspace.CreateYearAsync(companyId, "FY2027", 2027);

        var list = await workspace.UseAsync(scope => scope.GetRequiredService<CompanyService>().ListAsync());

        var row = Assert.Single(list);
        Assert.Equal(2, row.EngagementCount);
        Assert.Equal(0, row.FinalizedEngagementCount);
    }

    [Fact]
    public async Task ArchivedCompaniesAcceptNoNewYears()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        await workspace.ExecuteRawAsync(
            "UPDATE company SET status = 'ARCHIVED' WHERE company_id = $id", ("$id", companyId.ToString("D")));

        await Assert.ThrowsAsync<ValidationException>(() => workspace.CreateYearAsync(companyId, "FY2026", 2026));
    }

    [Fact]
    public async Task CompanyWithYearsCannotBeDeletedEvenThroughRawSql()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        await workspace.CreateYearAsync(companyId, "FY2026", 2026);

        var error = await workspace.ExpectRawFailureAsync(
            "DELETE FROM company WHERE company_id = $id", ("$id", companyId.ToString("D")));
        Assert.Contains("AWB-GUARD-COMPANY-DELETE", error.Message);
    }

    [Fact]
    public async Task OverlappingPeriodsForOneCompanyAreRejected()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        await workspace.CreateYearAsync(companyId, "FY2026", 2026);

        var error = await Assert.ThrowsAsync<ValidationException>(() => workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().CreateAsync(new CreateEngagementCommand(
                companyId, "FY2026-H2", new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30),
                "DRAFT", "USD", 2, null))));

        Assert.Contains("overlaps", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlyFinalizedEarlierYearsAreOfferedAsPriorYear()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var fy2025 = await workspace.CreateYearAsync(companyId, "FY2025", 2025);
        await workspace.AddAccountAsync(fy2025, "4000", "Revenue", "100");
        await workspace.FinalizeAsync(fy2025);
        var fy2026Draft = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        var fy2027 = await workspace.CreateYearAsync(companyId, "FY2027", 2027);

        var options = await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().EligiblePriorYearsAsync(fy2027));

        Assert.Equal(new[] { fy2025 }, options.Select(o => o.EngagementId).ToArray());
        Assert.DoesNotContain(options, o => o.EngagementId == fy2026Draft);
    }

    [Fact]
    public async Task CreatingAYearWithAPriorYearLinksItInOneStep()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var fy2026 = await workspace.CreateYearAsync(companyId, "FY2026", 2026);
        await workspace.AddAccountAsync(fy2026, "4000", "Revenue", "850000000");
        await workspace.FinalizeAsync(fy2026);

        var fy2027 = await workspace.CreateYearAsync(companyId, "FY2027", 2027, fy2026);

        var summary = await workspace.UseAsync(scope =>
            scope.GetRequiredService<EngagementService>().GetAsync(fy2027));
        Assert.Equal(fy2026, summary.PriorEngagementId);
        Assert.Equal("FY2026", summary.PriorEngagementLabel);
    }

    [Fact]
    public async Task EngagementIdentityColumnsAreImmutable()
    {
        await using var workspace = await TestWorkspace.CreateAsync();
        var companyId = await workspace.CreateCompanyAsync();
        var engagementId = await workspace.CreateYearAsync(companyId, "FY2026", 2026);

        var error = await workspace.ExpectRawFailureAsync(
            "UPDATE engagement SET currency_code = 'EUR' WHERE engagement_id = $id",
            ("$id", engagementId.ToString("D")));

        Assert.Contains("AWB-GUARD-ENGAGEMENT-IDENTITY", error.Message);
    }

    [Fact]
    public async Task CompanyHasNoCurrentYearColumn()
    {
        await using var workspace = await TestWorkspace.CreateAsync();

        await using var connection = workspace.OpenRawConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('company')";
        await using var reader = await command.ExecuteReaderAsync();

        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        // requirements.md FR-M06 / ADR-004: the current year is never company state.
        Assert.DoesNotContain(columns, c => c.Contains("current", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, c => c.Contains("year", StringComparison.OrdinalIgnoreCase));
    }
}
