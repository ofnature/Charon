using System.Numerics;
using Charon.Features.Follow;

namespace Charon.Tests.Features.Follow;

public sealed class TrapRouteTests
{
    private static readonly Vector3 Start = new(0, 0, 0);
    private static readonly Vector3 End = new(20, 0, 0);

    [Fact]
    public void ARouteStraightThroughATrap_Crosses()
    {
        var crossing = TrapRoute.FirstCrossing([Start, End], [new Vector3(10, 0, 1)]);
        Assert.NotNull(crossing);
        Assert.Equal(0, crossing!.Value.Leg);
    }

    [Fact]
    public void ARouteWellClearOfATrap_DoesNotCross()
    {
        Assert.Null(TrapRoute.FirstCrossing([Start, End], [new Vector3(10, 0, 5)]));
    }

    [Fact]
    public void ATrapOnAnotherLevel_IsNoThreat()
    {
        Assert.Null(TrapRoute.FirstCrossing([Start, End], [new Vector3(10, 8, 0)]));
    }

    [Fact]
    public void ATrapPastTheEndOfALeg_IsMeasuredToTheEnd()
    {
        Assert.Equal(3f, TrapRoute.DistanceToLeg(new Vector3(23, 0, 0), Start, End), 3);
    }

    [Fact]
    public void Sidesteps_SitBesideTheTrap_AndTheSplicedRouteIsClear()
    {
        var trap = new Vector3(10, 0, 1);
        var (first, second) = TrapRoute.Sidesteps(Start, End, trap, TrapRoute.Clearance + 1f);

        // At right angles to the leg, one each side, the offset away from the trap.
        Assert.Equal(10f, first.X, 3);
        Assert.Equal(10f, second.X, 3);
        Assert.Equal(-2.5f, System.Math.Min(first.Z, second.Z), 3);
        Assert.Equal(4.5f, System.Math.Max(first.Z, second.Z), 3);

        // The leg runs on the -Z side of this trap, so that side comes first — the shorter way round.
        Assert.True(first.Z < trap.Z);

        var detour = TrapRoute.Splice([Start, End], 0, first);
        Assert.Equal(3, detour.Count);
        Assert.Null(TrapRoute.FirstCrossing(detour, [trap]));
    }

    [Fact]
    public void StopShort_EndsTheRouteAtFollowDistance_AlongThePath()
    {
        var path = TrapRoute.StopShort([Start, new Vector3(10, 0, 0), new Vector3(10, 0, 10)], 4f);
        Assert.Equal(3, path.Count);
        Assert.Equal(new Vector3(10, 0, 6), path[^1]);

        // Shorter than the whole last leg and then some: whole legs are dropped.
        var shorter = TrapRoute.StopShort([Start, new Vector3(10, 0, 0), new Vector3(10, 0, 2)], 5f);
        Assert.Equal(2, shorter.Count);
        Assert.Equal(new Vector3(7, 0, 0), shorter[^1]);
    }
}
