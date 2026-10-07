using System;
using System.Linq;
using Charon.Features.DeepDungeon;

namespace Charon.Tests.Features.DeepDungeon;

public sealed class PomanderLogTests
{
    private static readonly DateTime At = new(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);

    // DeepDungeonItem rows (verified on XIVAPI).
    private const uint Sight = 2, Affluence = 5, Flight = 6, Alteration = 7, ProtoAlteration = 29, Devotion = 38, Intuition = 14;

    private static PomanderUse Use(int floor, uint item, string user = "Xia Discord") =>
        new(floor, item, $"item {item}", user, At);

    [Fact]
    public void Alteration_ActsOnTheNextFloor()
    {
        // "Changes the enemy occupying a random room in the NEXT floor into a mimic or a mandragora."
        var log = new PomanderLog();
        log.Record(Use(31, Alteration));

        Assert.Empty(log.ActiveOn(31));
        Assert.Single(log.Pending(31));
        Assert.Single(log.ActiveOn(32));
    }

    [Theory]
    [InlineData(Affluence)]
    [InlineData(Flight)]
    [InlineData(Alteration)]
    [InlineData(ProtoAlteration)]
    [InlineData(Devotion)]
    public void TheNextFloorPomanders(uint item)
    {
        Assert.True(PomanderLog.ActsOnNextFloor(item));
    }

    [Fact]
    public void Sight_ActsWhereItIsUsed()
    {
        var log = new PomanderLog();
        log.Record(Use(31, Sight, "Korha Ishere"));

        Assert.Single(log.ActiveOn(31));
        Assert.Empty(log.Pending(31));
        Assert.Empty(log.ActiveOn(32));
    }

    [Fact]
    public void TheFloorShowsBothItsOwnAndThePreviousFloorsQueuedEffects()
    {
        var log = new PomanderLog();
        log.Record(Use(31, Alteration, "Xia Discord"));
        log.Record(Use(32, Intuition, "Korha Ishere"));

        Assert.Equal(["Xia Discord", "Korha Ishere"], log.ActiveOn(32).Select(u => u.User));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(38, true)]
    [InlineData(39, false)]
    [InlineData(268_435_456, false)] // an object id — the user's parameter, never the item
    public void OnlyARealPomanderRowCounts(long value, bool expected)
    {
        Assert.Equal(expected, PomanderLog.IsPomander(value));
    }

    [Fact]
    public void ANewRunStartsClean()
    {
        var log = new PomanderLog();
        log.Record(Use(31, Sight));
        log.Clear();
        Assert.Empty(log.ActiveOn(31));
    }

    [Fact]
    public void DescribeNamesTheItem_NotWhoUsedIt()
    {
        // The source entity reads back as the local toon whoever used it, so the name would mislead.
        var text = PomanderLog.Describe([new PomanderUse(31, Alteration, "pomander of alteration", "Xia Discord", At)]);
        Assert.Equal("pomander of alteration", text);
    }
}
