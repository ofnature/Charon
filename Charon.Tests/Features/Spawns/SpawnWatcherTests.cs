using System;
using System.Collections.Generic;
using Charon.Features.Spawns;

namespace Charon.Tests.Features.Spawns;

public sealed class SpawnWatcherTests
{
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Watchlist = ["Croakadile", "mimic"];

    private static NearbyMob Mob(uint id, string name, float distance = 12f) => new(id, name, distance);

    [Theory]
    [InlineData("Croakadile", true)]
    [InlineData("croakadile", true)]          // case-insensitive
    [InlineData("Giant Mimic", true)]         // substring, either side
    [InlineData("Ornery Karakul", false)]
    [InlineData("", false)]
    public void IsWatched_MatchesCaseInsensitiveSubstrings(string name, bool expected)
    {
        Assert.Equal(expected, SpawnWatcher.IsWatched(name, Watchlist));
    }

    [Fact]
    public void EmptyWatchlist_MatchesNothing()
    {
        Assert.False(SpawnWatcher.IsWatched("Croakadile", Array.Empty<string>()));
    }

    [Fact]
    public void BlankWatchlistEntry_IsNotAWildcard()
    {
        // A stray empty row would otherwise match every mob in the zone.
        Assert.False(SpawnWatcher.IsWatched("Croakadile", ["   "]));
    }

    [Fact]
    public void WatchedMob_IsReportedOnceAndLogged()
    {
        var watcher = new SpawnWatcher();
        var nearby = new List<NearbyMob> { Mob(1, "Croakadile", 30f) };

        var first = watcher.Observe(nearby, Watchlist, Now, territory: 135);
        var sighting = Assert.Single(first);
        Assert.Equal("Croakadile", sighting.Name);
        Assert.Equal(30f, sighting.Distance);
        Assert.Equal(135, sighting.Territory);
        Assert.Single(watcher.History);

        // Still standing there on the next scan — not a second spawn.
        Assert.Empty(watcher.Observe(nearby, Watchlist, Now.AddSeconds(1), 135));
        Assert.Single(watcher.History);
    }

    [Fact]
    public void UnwatchedMobs_AreIgnored()
    {
        var watcher = new SpawnWatcher();
        var fresh = watcher.Observe([Mob(1, "Ornery Karakul")], Watchlist, Now, 135);

        Assert.Empty(fresh);
        Assert.Empty(watcher.History);
    }

    [Fact]
    public void AddingAName_ReportsAMobAlreadyStandingThere()
    {
        // "Is it up?" and "did it spawn?" are the same question to a watcher that starts empty.
        var watcher = new SpawnWatcher();
        Assert.Empty(watcher.Observe([Mob(7, "Croakadile")], [], Now, 135));

        var fresh = watcher.Observe([Mob(7, "Croakadile")], Watchlist, Now.AddSeconds(5), 135);
        Assert.Single(fresh);
    }

    [Fact]
    public void Reset_LetsTheSameMobBeReportedAgain()
    {
        var watcher = new SpawnWatcher();
        watcher.Observe([Mob(1, "Croakadile")], Watchlist, Now, 135);

        watcher.Reset(); // zone change — entity ids only mean anything within a zone
        var again = watcher.Observe([Mob(1, "Croakadile")], Watchlist, Now.AddMinutes(5), 135);

        Assert.Single(again);
        Assert.Equal(2, watcher.History.Count); // the log keeps both
    }

    [Fact]
    public void History_IsNewestFirstAndBounded()
    {
        var watcher = new SpawnWatcher();
        for (var i = 0u; i < SpawnWatcher.MaxHistory + 10; i++)
            watcher.Observe([Mob(i, $"Croakadile {i}")], Watchlist, Now.AddSeconds(i), 135);

        Assert.Equal(SpawnWatcher.MaxHistory, watcher.History.Count);
        Assert.Equal("Croakadile 209", watcher.History[0].Name); // newest survives, oldest fall off
    }

    [Fact]
    public void ClearHistory_AlsoForgetsWhatWasSeen()
    {
        var watcher = new SpawnWatcher();
        watcher.Observe([Mob(1, "Croakadile")], Watchlist, Now, 135);
        watcher.ClearHistory();

        Assert.Empty(watcher.History);
        Assert.Single(watcher.Observe([Mob(1, "Croakadile")], Watchlist, Now.AddSeconds(1), 135));
    }
}
