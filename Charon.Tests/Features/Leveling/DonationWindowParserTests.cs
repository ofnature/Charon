using Charon.Features.Leveling;

namespace Charon.Tests.Features.Leveling;

public sealed class DonationWindowParserTests
{
    // --- Amount parsing: the window's text carries separators and icon glyphs ---

    [Theory]
    [InlineData("25,000", 25_000)]
    [InlineData("200", 200)]
    [InlineData("0", 0)]              // a real zero ("Grand Total 0") must parse as zero…
    [InlineData("45,303,089", 45_303_089)]
    [InlineData(" 1,234 gil", 1_234)] // junk around the digits is skipped, not fatal
    public void Amounts_ParseThroughSeparatorsAndJunk(string text, long expected)
    {
        Assert.Equal(expected, DonationWindowParser.ParseAmount(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Weekly Budget: ")] // …but NO digits is absence, which must not read as zero
    public void NoDigits_IsMinusOne_NotZero(string? text)
    {
        Assert.Equal(-1, DonationWindowParser.ParseAmount(text));
    }

    // --- The budget model, as user-verified live ---

    [Fact]
    public void UnitGratuity_IsBaseTimesRate()
    {
        // The verified sample: 150-gil orchestrion roll at 200% → 300 each, and 3 staged showed
        // Grand Total 900 = 300 × 3. The budget consumes gratuity, not base value.
        Assert.Equal(300, DonationWindowParser.UnitGratuity(150, 200));
    }

    [Fact]
    public void DuckBones_AtTwoHundredPercent()
    {
        // Duck Bones sell for 360 (sheet-verified): 720 budget each at 200%.
        Assert.Equal(720, DonationWindowParser.UnitGratuity(360, 200));
    }

    [Fact]
    public void Target_MeetsOrExceeds_TheRemainingBudget()
    {
        // 25,000 remaining ÷ 720 = 34.7… → 35 bones (25,200 ≥ 25,000), never 34.
        var target = DonationWindowParser.TargetQuantity(25_000, 360, 200, held: 999);

        Assert.Equal(35, target);
        Assert.True(target * 720L >= 25_000);
        Assert.True((target - 1) * 720L < 25_000);
    }

    [Fact]
    public void Target_ClampsToWhatIsHeld()
    {
        Assert.Equal(10, DonationWindowParser.TargetQuantity(25_000, 360, 200, held: 10));
    }

    [Theory]
    [InlineData(0, 360, 200)]   // budget spent
    [InlineData(25_000, 0, 200)] // worthless item
    [InlineData(25_000, 360, 0)] // no rate — no gratuity, nothing to chase
    public void NothingToDonate_IsZero(long budget, long price, long rate)
    {
        Assert.Equal(0, DonationWindowParser.TargetQuantity(budget, price, rate, held: 999));
    }
}
