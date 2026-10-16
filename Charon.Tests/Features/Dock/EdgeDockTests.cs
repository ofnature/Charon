using System;
using System.Collections.Generic;
using System.Numerics;
using Charon.Features.Dock;

namespace Charon.Tests.Features.Dock;

public sealed class EdgeDockTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(450);

    private static readonly IReadOnlyList<DockTab> BellAndFloor = [DockTab.Retainers, DockTab.Floor];

    // --- Which tabs exist ---

    [Fact]
    public void WithNothingGoingOn_TheEdgeIsEmpty()
    {
        Assert.Empty(DockTabs.Visible(new DockContext(false, false, false, false)));
    }

    [Fact]
    public void ATabExistsOnlyWhileItApplies()
    {
        var tabs = DockTabs.Visible(new DockContext(AtBell: true, InDeepDungeon: false, ChestOpen: true, DrivingMultiSeat: false));
        Assert.Equal([DockTab.Retainers, DockTab.Chest], tabs);
    }

    // --- Hover ---

    [Fact]
    public void PointingAtATabOpensItsPanel()
    {
        var hover = new DockHover();
        hover.Update(DockTab.Floor, false, T0, Delay, BellAndFloor);
        Assert.Equal(DockTab.Floor, hover.Open);
    }

    [Fact]
    public void ThePanelSurvivesTheTripAcrossTheGap()
    {
        // Off the tab, not yet on the panel: it must not flicker shut on the way.
        var hover = new DockHover();
        hover.Update(DockTab.Floor, false, T0, Delay, BellAndFloor);
        hover.Update(null, false, T0.AddMilliseconds(200), Delay, BellAndFloor);
        Assert.Equal(DockTab.Floor, hover.Open);
    }

    [Fact]
    public void ItStaysWhileThePointerIsOnThePanel_HoweverLong()
    {
        var hover = new DockHover();
        hover.Update(DockTab.Floor, false, T0, Delay, BellAndFloor);
        hover.Update(null, panelBusy: true, T0.AddSeconds(30), Delay, BellAndFloor);
        hover.Update(null, false, T0.AddSeconds(30.2), Delay, BellAndFloor);
        Assert.Equal(DockTab.Floor, hover.Open);
    }

    [Fact]
    public void ItClosesAMomentAfterThePointerLeaves()
    {
        var hover = new DockHover();
        hover.Update(DockTab.Floor, false, T0, Delay, BellAndFloor);
        hover.Update(null, false, T0.AddMilliseconds(600), Delay, BellAndFloor);
        Assert.Null(hover.Open);
    }

    // --- Pinning ---

    [Fact]
    public void AClickPinsThePanel_AndItNoLongerClosesByItself()
    {
        var hover = new DockHover();
        hover.Click(DockTab.Floor);
        hover.Update(null, false, T0.AddMinutes(5), Delay, BellAndFloor);
        Assert.Equal(DockTab.Floor, hover.Open);
    }

    [Fact]
    public void ASecondClickUnpins()
    {
        var hover = new DockHover();
        hover.Click(DockTab.Floor);
        hover.Click(DockTab.Floor);
        hover.Update(null, false, T0.AddMinutes(5), Delay, BellAndFloor);
        Assert.Null(hover.Pinned);
        Assert.Null(hover.Open);
    }

    [Fact]
    public void PeekingAtAnotherTab_ReturnsToThePinnedOne()
    {
        var hover = new DockHover();
        hover.Click(DockTab.Floor);
        hover.Update(DockTab.Retainers, false, T0, Delay, BellAndFloor);
        Assert.Equal(DockTab.Retainers, hover.Open);

        hover.Update(null, false, T0.AddSeconds(1), Delay, BellAndFloor);
        Assert.Equal(DockTab.Floor, hover.Open);
    }

    [Fact]
    public void ATabWhoseMomentHasPassed_TakesItsPanelAndItsPinWithIt()
    {
        // Leaving the deep dungeon: the Floor tab is gone, so nothing may stay open or pinned for it.
        var hover = new DockHover();
        hover.Click(DockTab.Floor);
        hover.Update(null, false, T0, Delay, [DockTab.Retainers]);
        Assert.Null(hover.Open);
        Assert.Null(hover.Pinned);
    }

    // --- Sideways text ---

    [Fact]
    public void TurnedText_LandsInTheBoxThatWasAskedFor()
    {
        // Text 80 wide and 16 tall, wanted with its (turned) top-left at (10, 20): a box 16 wide, 80 tall.
        var topLeft = new Vector2(10f, 20f);
        var size = new Vector2(80f, 16f);
        var pivot = DockMath.PivotFor(topLeft, size);

        var corners = new[]
        {
            DockMath.TurnUp(pivot, pivot),
            DockMath.TurnUp(pivot + new Vector2(size.X, 0f), pivot),
            DockMath.TurnUp(pivot + new Vector2(0f, size.Y), pivot),
            DockMath.TurnUp(pivot + size, pivot),
        };

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in corners)
        {
            minX = MathF.Min(minX, c.X); maxX = MathF.Max(maxX, c.X);
            minY = MathF.Min(minY, c.Y); maxY = MathF.Max(maxY, c.Y);
        }

        Assert.Equal(10f, minX, 3);
        Assert.Equal(20f, minY, 3);
        Assert.Equal(26f, maxX, 3);  // 16 wide
        Assert.Equal(100f, maxY, 3); // 80 tall
    }

    [Fact]
    public void TurnedText_ReadsBottomToTop()
    {
        // The start of the text (its left end) ends up at the BOTTOM of the box, the end at the top.
        var pivot = DockMath.PivotFor(new Vector2(0f, 0f), new Vector2(80f, 16f));
        var start = DockMath.TurnUp(pivot, pivot);
        var end = DockMath.TurnUp(pivot + new Vector2(80f, 0f), pivot);
        Assert.True(start.Y > end.Y);
    }

    [Fact]
    public void TextDrawnElsewhere_IsPlacedExactlyWhereTextDrawnAtThePivotWouldBe()
    {
        // The right-edge fix: drawing at an on-screen point and then placing must give the same result as
        // drawing at the pivot itself would have, had nothing been culled.
        var pivot = DockMath.PivotFor(new Vector2(1880f, 400f), new Vector2(95f, 16f));
        var drawnAt = new Vector2(8f, 8f);

        foreach (var offset in new[] { Vector2.Zero, new Vector2(95f, 0f), new Vector2(0f, 16f), new Vector2(95f, 16f) })
        {
            var expected = DockMath.TurnUp(pivot + offset, pivot);
            var placed = DockMath.TurnUpAndPlace(drawnAt + offset, drawnAt, pivot);
            Assert.Equal(expected.X, placed.X, 3);
            Assert.Equal(expected.Y, placed.Y, 3);
        }
    }
}
