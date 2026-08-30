using System;
using System.Linq;
using Charon.Features.Weeklies;

namespace Charon.Tests.Features.Weeklies;

public sealed class WeekliesBoardTests
{
    // 2026-08-12 is a Wednesday; the next weekly reset is Tuesday 2026-08-18 08:00 UTC.
    private static readonly DateTime Wednesday = new(2026, 8, 12, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NextWeeklyReset_IsTheTuesdayAhead()
    {
        Assert.Equal(new DateTime(2026, 8, 18, 8, 0, 0, DateTimeKind.Utc),
            WeekliesBoard.NextWeeklyReset(Wednesday));
    }

    [Fact]
    public void NextDailyReset_LaterToday_WhenBeforeFifteen()
    {
        var morning = new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 8, 12, 15, 0, 0, DateTimeKind.Utc),
            WeekliesBoard.NextDailyReset(morning));
    }

    [Fact]
    public void NextDailyReset_Tomorrow_WhenAtOrPastFifteen()
    {
        var atReset = new DateTime(2026, 8, 12, 15, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 8, 13, 15, 0, 0, DateTimeKind.Utc),
            WeekliesBoard.NextDailyReset(atReset));
    }

    [Theory]
    [InlineData(2, 16, 30, "2d 16h")]
    [InlineData(0, 16, 12, "16h 12m")]
    [InlineData(0, 0, 12, "12m")]
    public void FormatCountdown_PicksTheRightGranularity(int days, int hours, int minutes, string expected)
    {
        Assert.Equal(expected, WeekliesBoard.FormatCountdown(new TimeSpan(days, hours, minutes, 0)));
    }

    [Fact]
    public void FormatCountdown_NeverShowsZeroMinutes()
    {
        Assert.Equal("1m", WeekliesBoard.FormatCountdown(TimeSpan.FromSeconds(10)));
        Assert.Equal("1m", WeekliesBoard.FormatCountdown(TimeSpan.FromSeconds(-5)));
    }

    [Fact]
    public void AllSourcesLoaded_NothingUsed_EverythingPending()
    {
        var items = WeekliesBoard.Compose(Wednesday,
            domanLoaded: true, domanDone: false, domanBudgetRemaining: 20_000, domanCached: false,
            deliveriesLoaded: true, deliveriesUsed: 0,
            tribesLoaded: true, tribeAllowanceLeft: 12);

        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(WeekliesBoard.ItemState.Pending, i.State));
        Assert.Contains(items, i => i.Detail == "12 of 12 left");
    }

    [Fact]
    public void EverythingSpent_AllDone()
    {
        var items = WeekliesBoard.Compose(Wednesday,
            domanLoaded: true, domanDone: true, domanBudgetRemaining: 0, domanCached: false,
            deliveriesLoaded: true, deliveriesUsed: 12,
            tribesLoaded: true, tribeAllowanceLeft: 0);

        Assert.All(items, i => Assert.Equal(WeekliesBoard.ItemState.Done, i.State));
    }

    [Fact]
    public void UnloadedSources_ReadAsUnknown_NeverConfidentZero()
    {
        var items = WeekliesBoard.Compose(Wednesday,
            domanLoaded: false, domanDone: false, domanBudgetRemaining: 0, domanCached: false,
            deliveriesLoaded: false, deliveriesUsed: 0,
            tribesLoaded: false, tribeAllowanceLeft: 0);

        Assert.All(items, i => Assert.Equal(WeekliesBoard.ItemState.Unknown, i.State));
    }

    [Fact]
    public void DeliveriesUsedBeyondCap_ClampsToZeroLeft()
    {
        var items = WeekliesBoard.Compose(Wednesday,
            domanLoaded: true, domanDone: true, domanBudgetRemaining: 0, domanCached: false,
            deliveriesLoaded: true, deliveriesUsed: 14,
            tribesLoaded: true, tribeAllowanceLeft: 0);

        var deliveries = items.Single(i => i.Name == "Custom Deliveries");
        Assert.Equal(WeekliesBoard.ItemState.Done, deliveries.State);
    }

    [Fact]
    public void OpenDomanBudget_ShowsTheGilFigure()
    {
        var items = WeekliesBoard.Compose(Wednesday,
            domanLoaded: true, domanDone: false, domanBudgetRemaining: 20_000, domanCached: false,
            deliveriesLoaded: true, deliveriesUsed: 0,
            tribesLoaded: true, tribeAllowanceLeft: 12);

        var doman = items.Single(i => i.Name == "Doman donation");
        Assert.Equal("20,000 gil remaining", doman.Detail);
    }

    [Fact]
    public void CachedDomanFigure_SaysSo()
    {
        var items = WeekliesBoard.Compose(Wednesday,
            domanLoaded: true, domanDone: false, domanBudgetRemaining: 20_000, domanCached: true,
            deliveriesLoaded: true, deliveriesUsed: 0,
            tribesLoaded: true, tribeAllowanceLeft: 12);

        var doman = items.Single(i => i.Name == "Doman donation");
        Assert.Equal("20,000 gil remaining (from last visit)", doman.Detail);
        Assert.Equal(WeekliesBoard.ItemState.Pending, doman.State);
    }

    [Fact]
    public void WeeklyAndDailyRows_CarryTheirOwnResetClocks()
    {
        var items = WeekliesBoard.Compose(Wednesday,
            domanLoaded: true, domanDone: false, domanBudgetRemaining: 20_000, domanCached: false,
            deliveriesLoaded: true, deliveriesUsed: 3,
            tribesLoaded: true, tribeAllowanceLeft: 5);

        var doman = items.Single(i => i.Name == "Doman donation");
        var tribes = items.Single(i => i.Name == "Allied Society dailies");
        Assert.Contains("(Tue)", doman.Reset);
        Assert.DoesNotContain("(Tue)", tribes.Reset);
    }
}
