using Charon.Features.Tweaks;

namespace Charon.Tests.Features.Tweaks;

public class PlayerDrawRangeTests
{
    [Fact]
    public void APlayerBeyondTheRangeIsHidden_AndOneInsideIsNot()
    {
        Assert.True(PlayerDrawRange.ShouldHide(31f, 30f, hiddenNow: false, exempt: false));
        Assert.False(PlayerDrawRange.ShouldHide(29f, 30f, hiddenNow: false, exempt: false));
        Assert.False(PlayerDrawRange.ShouldHide(30f, 30f, hiddenNow: false, exempt: false));
    }

    [Fact]
    public void AHiddenPlayerComesBackOnlyOnceWellInside()
    {
        // Standing on the line must not load and drop the model over and over.
        Assert.True(PlayerDrawRange.ShouldHide(29f, 30f, hiddenNow: true, exempt: false));
        Assert.True(PlayerDrawRange.ShouldHide(27.5f, 30f, hiddenNow: true, exempt: false));
        Assert.False(PlayerDrawRange.ShouldHide(26.9f, 30f, hiddenNow: true, exempt: false));
    }

    [Fact]
    public void AnExemptPlayerIsNeverHidden_HoweverFar()
    {
        Assert.False(PlayerDrawRange.ShouldHide(500f, 30f, hiddenNow: false, exempt: true));
        Assert.False(PlayerDrawRange.ShouldHide(500f, 30f, hiddenNow: true, exempt: true));
    }

    [Fact]
    public void AnUnreadableDistanceHidesNobody()
        => Assert.False(PlayerDrawRange.ShouldHide(float.NaN, 30f, hiddenNow: true, exempt: false));

    [Theory]
    [InlineData(0f, PlayerDrawRange.MinRange)]
    [InlineData(-10f, PlayerDrawRange.MinRange)]
    [InlineData(1000f, PlayerDrawRange.MaxRange)]
    [InlineData(45f, 45f)]
    [InlineData(float.NaN, PlayerDrawRange.DefaultRange)]
    public void AHandEditedRangeIsBroughtBackIntoBounds(float asked, float used)
        => Assert.Equal(used, PlayerDrawRange.Clamp(asked));

    [Fact]
    public void ANonsenseRangeStillHidesByTheClampedOne()
    {
        // A saved range of 0 would otherwise hide everyone standing next to you.
        Assert.False(PlayerDrawRange.ShouldHide(4f, 0f, hiddenNow: false, exempt: false));
        Assert.True(PlayerDrawRange.ShouldHide(6f, 0f, hiddenNow: false, exempt: false));
    }
}
