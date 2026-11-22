using System.Linq;
using System.Numerics;
using Charon.Features.DeepDungeon;
using Charon.Features.Follow;
using Xunit;

namespace Charon.Tests.Features.DeepDungeon;

public class LikelyTrapAvoidTests
{
    private static readonly Vector3 Me = new(100f, 0f, 100f);

    [Fact]
    public void EachSpot_BecomesItsPositionAndTheTrapClearance()
    {
        var circles = LikelyTrapAvoid.Circles([new Vector3(110f, 1.5f, 95f)], Me);

        Assert.Equal(new[] { 110f, 1.5f, 95f, TrapRoute.Clearance }, circles);
        Assert.Equal(1, LikelyTrapAvoid.Count(circles));
    }

    [Fact]
    public void NoSpots_IsAnEmptyList()
    {
        Assert.Empty(LikelyTrapAvoid.Circles([], Me));
        Assert.Equal(0, LikelyTrapAvoid.Count([]));
    }

    [Fact]
    public void ASpotOutOfRange_IsNotSent()
    {
        var far = Me + new Vector3(LikelyTrapAvoid.Range + 1f, 0f, 0f);
        var near = Me + new Vector3(LikelyTrapAvoid.Range - 1f, 0f, 0f);

        var circles = LikelyTrapAvoid.Circles([far, near], Me);

        Assert.Equal(1, LikelyTrapAvoid.Count(circles));
        Assert.Equal(near.X, circles[0]);
    }

    [Fact]
    public void TheNearestComeFirst_AndTheListIsCapped()
    {
        // 50 spots in a line, 1y apart, given furthest first.
        var spots = Enumerable.Range(1, 50).Select(i => Me + new Vector3(i, 0f, 0f)).Reverse().ToList();

        var circles = LikelyTrapAvoid.Circles(spots, Me);

        Assert.Equal(LikelyTrapAvoid.MaxCircles, LikelyTrapAvoid.Count(circles));
        Assert.Equal(Me.X + 1f, circles[0]);
        Assert.Equal(Me.X + 2f, circles[4]);
        Assert.Equal(Me.X + LikelyTrapAvoid.MaxCircles, circles[(LikelyTrapAvoid.MaxCircles - 1) * 4]);
    }
}
