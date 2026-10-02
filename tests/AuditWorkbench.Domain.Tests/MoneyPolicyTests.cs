using AuditWorkbench.Domain.Common;
using AuditWorkbench.Domain.Money;
using Xunit;

namespace AuditWorkbench.Domain.Tests;

public class MoneyPolicyTests
{
    [Theory]
    [InlineData("850000000", 2, 85_000_000_000L)]
    [InlineData("1,234.56", 2, 123_456L)]
    [InlineData("-1234.56", 2, -123_456L)]
    [InlineData("0", 2, 0L)]
    [InlineData("12", 0, 12L)]
    public void ParsesExactMinorUnits(string input, int scale, long expected) =>
        Assert.Equal(expected, MoneyPolicy.ParseToMinor(input, scale));

    [Fact]
    public void RejectsMorePrecisionThanTheEngagementScale() =>
        Assert.Throws<ValidationException>(() => MoneyPolicy.ParseToMinor("1.234", 2));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    public void RejectsInvalidInput(string input) =>
        Assert.Throws<ValidationException>(() => MoneyPolicy.ParseToMinor(input, 2));

    [Fact]
    public void RejectsValuesOutsideSixtyFourBitRange() =>
        Assert.Throws<ValidationException>(() => MoneyPolicy.ParseToMinor("99999999999999999999", 2));

    [Fact]
    public void ArithmeticIsExactUnlikeFloatingPoint()
    {
        var tenth = MoneyPolicy.ParseToMinor("0.1", 2);
        var fifth = MoneyPolicy.ParseToMinor("0.2", 2);

        Assert.Equal(30, tenth + fifth);
        Assert.Equal("0.30", MoneyPolicy.ToEditString(tenth + fifth, 2));
    }

    [Fact]
    public void FormatsForDisplayWithGroupSeparators() =>
        Assert.Equal("850,000,000.00", MoneyPolicy.Format(85_000_000_000L, 2));

    [Fact]
    public void FormatsNullAsEmpty() => Assert.Equal(string.Empty, MoneyPolicy.Format(null, 2));
}
