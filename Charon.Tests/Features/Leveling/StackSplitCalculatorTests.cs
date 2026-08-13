using Charon.Features.Leveling;

namespace Charon.Tests.Features.Leveling;

public sealed class StackSplitCalculatorTests
{
    // Duckbone-flavoured numbers: unit price ~546, cap headroom in the tens of thousands.

    [Fact]
    public void ExactDivision_NeedsNoOvershoot()
    {
        Assert.Equal(100, StackSplitCalculator.QuantityToReach(54_600, 546, 999));
    }

    [Fact]
    public void MeetsOrExceeds_NeverFallsShort()
    {
        // 54_601 needs 100.001… duckbones — floor would leave 1 gil of headroom and another trip.
        // The user's rule: get one over the target, never under (the over-cap message is just a
        // chat line, so overshoot costs nothing).
        var q = StackSplitCalculator.QuantityToReach(54_601, 546, 999);

        Assert.Equal(101, q);
        Assert.True(q * 546L >= 54_601);
        Assert.True((q - 1) * 546L < 54_601); // and it is the SMALLEST such quantity
    }

    [Fact]
    public void OneGilOfHeadroom_SellsOneItem()
    {
        Assert.Equal(1, StackSplitCalculator.QuantityToReach(1, 546, 999));
    }

    [Fact]
    public void HoldingLessThanTheTargetNeeds_SellsEverything()
    {
        // As close as this toon can get this trip — still under, reported by the caller.
        Assert.Equal(40, StackSplitCalculator.QuantityToReach(54_600, 546, 40));
    }

    [Theory]
    [InlineData(0, 546, 99)]     // no headroom — already at/over the cap
    [InlineData(-500, 546, 99)]  // over the cap
    [InlineData(54_600, 0, 99)]  // worthless item
    [InlineData(54_600, 546, 0)] // nothing held
    public void NothingToDo_IsZero(long headroom, long unit, int held)
    {
        Assert.Equal(0, StackSplitCalculator.QuantityToReach(headroom, unit, held));
    }

    [Fact]
    public void LargeValues_DoNotOverflow()
    {
        // A silly-but-legal case: donation-sized headroom with a 1-gil item.
        Assert.Equal(300_000, StackSplitCalculator.QuantityToReach(300_000, 1, 999_999));
    }
}
