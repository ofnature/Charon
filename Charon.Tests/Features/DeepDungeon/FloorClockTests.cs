using System;
using Charon.Features.DeepDungeon;

namespace Charon.Tests.Features.DeepDungeon;

public sealed class FloorClockTests
{
    private static readonly DateTime Start = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TimePerFloor_RunsForTheCurrentFloor_AndFreezesForFinishedOnes()
    {
        var clock = new FloorClock();
        clock.Track(31, Start);
        clock.Track(32, Start.AddSeconds(199)); // 3:19 on 31

        Assert.Equal(TimeSpan.FromSeconds(199), clock.TimeOn(31, Start.AddSeconds(500)));
        Assert.Equal(TimeSpan.FromSeconds(301), clock.TimeOn(32, Start.AddSeconds(500)));
        Assert.Null(clock.TimeOn(33, Start.AddSeconds(500)));
    }

    [Fact]
    public void TheSameFloorTrackedAgain_DoesNotRestartItsTime()
    {
        var clock = new FloorClock();
        clock.Track(31, Start);
        clock.Track(31, Start.AddSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(90), clock.TimeOn(31, Start.AddSeconds(90)));
    }

    [Theory]
    [InlineData(35, 31)]
    [InlineData(31, 31)]
    [InlineData(40, 31)]
    [InlineData(1, 1)]
    public void TheFloorSetIsTenFloors(int floor, int first)
    {
        var set = FloorClock.FloorSet(floor);
        Assert.Equal(10, set.Count);
        Assert.Equal(first, set[0]);
        Assert.Equal(first + 9, set[9]);
    }

    [Theory]
    [InlineData(60001u, 40)]   // Palace of the Dead 1-10
    [InlineData(60005u, 120)]  // Palace 41-50
    [InlineData(60016u, 300)]  // Palace 151-160
    [InlineData(60024u, 600)]  // Heaven-on-High 31-40
    [InlineData(60034u, 600)]  // Eureka Orthos 31-40
    [InlineData(60041u, 60)]   // Pilgrim's Traverse 1-10
    public void RespawnIntervals_AreNecroLensTable(uint contentId, int seconds)
    {
        Assert.Equal(seconds, FloorClock.RespawnSeconds(contentId));
    }

    [Fact]
    public void AnUnknownFloorSet_HasNoInterval()
    {
        Assert.Null(FloorClock.RespawnSeconds(1));
    }

    [Theory]
    [InlineData(60004u, 35, true)]
    [InlineData(60004u, 40, false)]  // boss floor
    [InlineData(60040u, 99, false)]  // Eureka Orthos 99
    [InlineData(60010u, 99, true)]   // Palace 99 still respawns
    public void BossFloorsDoNotRespawn(uint contentId, int floor, bool expected)
    {
        Assert.Equal(expected, FloorClock.HasRespawns(contentId, floor));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(17, 43)]
    [InlineData(60, 60)]
    [InlineData(77, 43)]
    public void RespawnCountsWholeIntervalsFromTheFloorStart(int elapsed, int left)
    {
        Assert.Equal(left, FloorClock.SecondsToRespawn(Start, Start.AddSeconds(elapsed), 60));
    }

    private static PomanderUse Use(uint item) => new(35, item, "x", "y", Start);

    [Fact]
    public void TrapsAreActiveUntilAPomanderSaysOtherwise()
    {
        Assert.Equal(TrapStatus.Active, FloorClock.Traps([]));
        Assert.Equal(TrapStatus.Visible, FloorClock.Traps([Use(2)]));    // Sight
        Assert.Equal(TrapStatus.Visible, FloorClock.Traps([Use(24)]));   // Protomander of Sight
        Assert.Equal(TrapStatus.Inactive, FloorClock.Traps([Use(2), Use(1)])); // Safety beats Sight
    }

    [Fact]
    public void ProtomandersCountAsTheirEffect()
    {
        var (_, alteration) = FloorClock.Effects[2];
        Assert.True(FloorClock.Has([Use(29)], alteration));
        Assert.False(FloorClock.Has([Use(5)], alteration));
    }

    [Theory]
    [InlineData(199, "03:19")]
    [InlineData(15, "00:15")]
    [InlineData(4000, "66:40")]
    public void TimesPrintAsMinutesAndSeconds(int seconds, string expected)
    {
        Assert.Equal(expected, FloorClock.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(60001u, CofferKind.Bronze)]  // Palace 1-10
    [InlineData(60004u, CofferKind.Silver)]  // Palace 31-40
    [InlineData(60005u, CofferKind.Gold)]    // Palace 41-50
    [InlineData(60024u, CofferKind.Silver)]  // Heaven-on-High 31-40
    [InlineData(60026u, CofferKind.Silver)]  // Heaven-on-High 51-60
    [InlineData(60027u, CofferKind.Gold)]    // Heaven-on-High 61-70
    [InlineData(60034u, CofferKind.Silver)]  // Eureka Orthos 31-40
    [InlineData(60044u, CofferKind.Silver)]  // Pilgrim's Traverse 31-40
    [InlineData(60050u, CofferKind.Gold)]    // Pilgrim's Traverse 91-100
    public void TheMimicCofferColour_IsNecroLensTable(uint contentId, CofferKind expected)
    {
        Assert.Equal(expected, FloorClock.MimicCoffer(contentId));
    }

    [Fact]
    public void OutsideADeepDungeon_NoColourIsAMimic()
    {
        Assert.Null(FloorClock.MimicCoffer(0));
    }
}
