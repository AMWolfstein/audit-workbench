using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Engagements;
using Xunit;

namespace AuditWorkbench.Domain.Tests;

public class PriorYearRelationshipTests
{
    private static readonly Guid CompanyA = Guid.NewGuid();
    private static readonly Guid CompanyB = Guid.NewGuid();

    private static (Engagement Engagement, FinancialYear Year) Year(
        Guid companyId, string label, int year, bool finalized)
    {
        var financialYear = FinancialYear.Create(
            Guid.NewGuid(), label, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), "t");
        var engagement = Engagement.Create(
            Guid.NewGuid(), companyId, financialYear.FinancialYearId, "USD", 2, "t", Guid.NewGuid());
        if (finalized)
        {
            engagement.MarkFinalized("2027-01-01T00:00:00.000Z", Guid.NewGuid(), new string('a', 64), "AWB-MANIFEST/1.0");
        }

        return (engagement, financialYear);
    }

    [Fact]
    public void LinksFinalizedEarlierYearOfSameCompany()
    {
        var prior = Year(CompanyA, "FY2026", 2026, finalized: true);
        var current = Year(CompanyA, "FY2027", 2027, finalized: false);

        var relationship = PriorYearRelationship.Create(
            Guid.NewGuid(), current.Engagement, current.Year, prior.Engagement, prior.Year, "t", Guid.NewGuid());

        Assert.Equal(current.Engagement.EngagementId, relationship.CurrentEngagementId);
        Assert.Equal(prior.Engagement.EngagementId, relationship.PriorEngagementId);
    }

    [Fact]
    public void RejectsDifferentCompany()
    {
        var prior = Year(CompanyB, "FY2026", 2026, finalized: true);
        var current = Year(CompanyA, "FY2027", 2027, finalized: false);

        var error = Assert.Throws<ValidationException>(() => PriorYearRelationship.Create(
            Guid.NewGuid(), current.Engagement, current.Year, prior.Engagement, prior.Year, "t", Guid.NewGuid()));
        Assert.Contains("same company", error.Message);
    }

    [Fact]
    public void RejectsDraftPriorYear()
    {
        var prior = Year(CompanyA, "FY2026", 2026, finalized: false);
        var current = Year(CompanyA, "FY2027", 2027, finalized: false);

        Assert.Throws<ValidationException>(() => PriorYearRelationship.Create(
            Guid.NewGuid(), current.Engagement, current.Year, prior.Engagement, prior.Year, "t", Guid.NewGuid()));
    }

    [Fact]
    public void RejectsLaterOrEqualPriorPeriod()
    {
        var prior = Year(CompanyA, "FY2028", 2028, finalized: true);
        var current = Year(CompanyA, "FY2027", 2027, finalized: false);

        Assert.Throws<ValidationException>(() => PriorYearRelationship.Create(
            Guid.NewGuid(), current.Engagement, current.Year, prior.Engagement, prior.Year, "t", Guid.NewGuid()));
    }

    [Fact]
    public void RejectsLinkingIntoAFinalizedCurrentYear()
    {
        var prior = Year(CompanyA, "FY2026", 2026, finalized: true);
        var current = Year(CompanyA, "FY2027", 2027, finalized: true);

        Assert.Throws<EngagementFinalizedException>(() => PriorYearRelationship.Create(
            Guid.NewGuid(), current.Engagement, current.Year, prior.Engagement, prior.Year, "t", Guid.NewGuid()));
    }

    [Fact]
    public void WarnsAboutPeriodGapsWithoutBlocking()
    {
        var prior = Year(CompanyA, "FY2025", 2025, finalized: true);
        var current = Year(CompanyA, "FY2027", 2027, finalized: false);

        var relationship = PriorYearRelationship.Create(
            Guid.NewGuid(), current.Engagement, current.Year, prior.Engagement, prior.Year, "t", Guid.NewGuid());
        var warning = PriorYearRelationship.PeriodGapWarning(prior.Year, current.Year);

        Assert.NotNull(relationship);
        Assert.NotNull(warning);
        Assert.Contains("does not start on the day after", warning);
    }

    [Fact]
    public void ConsecutiveYearsProduceNoWarning()
    {
        var prior = Year(CompanyA, "FY2026", 2026, finalized: true);
        var current = Year(CompanyA, "FY2027", 2027, finalized: false);

        Assert.Null(PriorYearRelationship.PeriodGapWarning(prior.Year, current.Year));
    }
}
