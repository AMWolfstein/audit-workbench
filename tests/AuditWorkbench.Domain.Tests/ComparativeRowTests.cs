using AuditWorkbench.Domain.Comparison;
using Xunit;

namespace AuditWorkbench.Domain.Tests;

public class ComparativeRowTests
{
    private static ComparativeRow Row(long? prior, long? current) => new()
    {
        AccountCode = "4000",
        AccountName = "Revenue",
        PriorAmountMinor = prior,
        CurrentAmountMinor = current,
        IsNewAccount = prior is null,
        IsMissingInCurrent = current is null,
    };

    [Theory]
    // The documented demo scenario (mvp-scope.md section 4), in minor units.
    [InlineData(85_000_000_000L, 92_000_000_000L, 7_000_000_000L, "8.24")]
    [InlineData(18_000_000_000L, 21_000_000_000L, 3_000_000_000L, "16.67")]
    [InlineData(24_000_000_000L, 27_500_000_000L, 3_500_000_000L, "14.58")]
    public void CalculatesDocumentedDemoFigures(long prior, long current, long change, string percent)
    {
        var row = Row(prior, current);

        Assert.Equal(change, row.ChangeAmountMinor);
        Assert.Equal(percent, row.ChangePercentDisplay);
    }

    [Fact]
    public void ZeroPriorAmountGivesNotAvailablePercentage()
    {
        var row = Row(0, 50_000);

        Assert.Equal(50_000, row.ChangeAmountMinor);
        Assert.Null(row.ChangePercent);
        Assert.Equal("N/A", row.ChangePercentDisplay);
    }

    [Fact]
    public void MissingPriorAmountGivesNoChangeAndNoPercentage()
    {
        var row = Row(null, 50_000);

        Assert.Null(row.ChangeAmountMinor);
        Assert.Equal("N/A", row.ChangePercentDisplay);
        Assert.Equal("New this year", row.StatusLabel);
    }

    [Fact]
    public void MissingCurrentAmountIsLabelled()
    {
        var row = Row(50_000, null);

        Assert.Null(row.ChangeAmountMinor);
        Assert.Equal("Not in current year", row.StatusLabel);
    }

    [Fact]
    public void NegativePriorUsesAbsoluteDenominator()
    {
        var row = Row(-20_000, -15_000);

        Assert.Equal(5_000, row.ChangeAmountMinor);
        Assert.Equal("25.00", row.ChangePercentDisplay);
    }

    [Fact]
    public void DecreaseIsReportedAsNegativeChange()
    {
        var row = Row(100_000, 75_000);

        Assert.Equal(-25_000, row.ChangeAmountMinor);
        Assert.Equal("-25.00", row.ChangePercentDisplay);
    }

    [Fact]
    public void RoundingIsHalfUpAwayFromZero()
    {
        // 1 / 8 = 12.5%, 1 / 16 = 6.25% -> 6.25 stays exact; 1/3 -> 33.33
        Assert.Equal("33.33", Row(300, 400).ChangePercentDisplay);
        Assert.Equal("12.50", Row(800, 900).ChangePercentDisplay);
    }
}
