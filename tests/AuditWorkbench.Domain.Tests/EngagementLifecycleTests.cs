using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Engagements;
using Xunit;

namespace AuditWorkbench.Domain.Tests;

public class EngagementLifecycleTests
{
    private static Engagement NewEngagement(string status = EngagementStatus.Draft) => Engagement.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "USD", 2, "2027-01-01T00:00:00.000Z", Guid.NewGuid(), status);

    [Theory]
    [InlineData(EngagementStatus.Draft, EngagementStatus.InProgress, true)]
    [InlineData(EngagementStatus.Draft, EngagementStatus.Finalized, true)]
    [InlineData(EngagementStatus.InProgress, EngagementStatus.Finalized, true)]
    [InlineData(EngagementStatus.InProgress, EngagementStatus.Draft, false)]
    [InlineData(EngagementStatus.Finalized, EngagementStatus.Draft, false)]
    [InlineData(EngagementStatus.Finalized, EngagementStatus.InProgress, false)]
    public void TransitionMatrix(string from, string to, bool allowed) =>
        Assert.Equal(allowed, EngagementStatus.CanTransition(from, to));

    [Fact]
    public void FinalizedIsTerminal()
    {
        var engagement = NewEngagement();
        engagement.MarkFinalized("2027-02-01T10:00:00.000Z", Guid.NewGuid(), new string('a', 64), "AWB-MANIFEST/1.0");

        Assert.True(engagement.IsFinalized);
        Assert.Throws<EngagementFinalizedException>(() => engagement.ChangeStatus(EngagementStatus.Draft));
        Assert.Throws<EngagementFinalizedException>(() => engagement.EnsureOpenForEditing("FY2026"));
        Assert.Throws<EngagementFinalizedException>(() =>
            engagement.MarkFinalized("2027-03-01T10:00:00.000Z", Guid.NewGuid(), new string('b', 64), "AWB-MANIFEST/1.0"));
    }

    [Fact]
    public void DraftAndInProgressAreBothEditable()
    {
        var engagement = NewEngagement();
        engagement.EnsureOpenForEditing("FY2027");

        engagement.ChangeStatus(EngagementStatus.InProgress);
        engagement.EnsureOpenForEditing("FY2027");

        Assert.Equal(EngagementStatus.InProgress, engagement.Status);
    }

    [Fact]
    public void StatusChangeBumpsTheConcurrencyToken()
    {
        var engagement = NewEngagement();
        var before = engagement.RowVersion;

        engagement.ChangeStatus(EngagementStatus.InProgress);

        Assert.Equal(before + 1, engagement.RowVersion);
        Assert.Throws<ConcurrencyException>(() => engagement.EnsureExpectedVersion(before));
    }

    [Fact]
    public void FinalizationCannotBeRequestedThroughStatusChange() =>
        Assert.Throws<ValidationException>(() => NewEngagement().ChangeStatus(EngagementStatus.Finalized));

    [Fact]
    public void RejectsInvalidCurrencyAndScale()
    {
        Assert.Throws<ValidationException>(() => Engagement.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "DOLLARS", 2, "t", Guid.NewGuid()));
        Assert.Throws<ValidationException>(() => Engagement.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "USD", 9, "t", Guid.NewGuid()));
    }

    [Fact]
    public void FinancialYearRejectsInvertedPeriods() =>
        Assert.Throws<ValidationException>(() => FinancialYear.Create(
            Guid.NewGuid(), "FY2026", new DateOnly(2026, 12, 31), new DateOnly(2026, 1, 1), "t"));
}
