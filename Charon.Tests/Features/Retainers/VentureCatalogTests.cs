using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

/// <summary>
/// The venture ranking. These tests exist because the obvious implementation — "pick the highest-level
/// venture" — is wrong money: on Maduin a level-84 hunt sells for several times the level-86 one for the
/// same hour and the same venture token. The numbers below are the real sheet values and a real day's
/// Universalis prices.
/// </summary>
public sealed class VentureCatalogTests
{
    // RetainerTask rows as read from the game (verified via the sheet schema).
    private static VentureDef Hunt(uint id, string item, uint itemId, int level, int gathering, long exp, IReadOnlyList<int>? thresholds = null) =>
        new(id, item, VentureKind.Hunting, itemId, item,
            new[] { 15, 20, 30, 40, 50 },
            thresholds ?? new[] { 1980, 2027, 2073, 2119, 2165 },
            false,
            level,
            0,
            gathering,
            "MIN",
            60,
            1,
            exp,
            false);

    private static VentureDef Eblan() => Hunt(901, "Eblan Alumen", 36241, 84, 1980, 794800);
    private static VentureDef Manganese() => Hunt(903, "Manganese Ore", 36165, 86, 2073, 923100);

    private static readonly Dictionary<uint, long> Prices = new()
    {
        [36241] = 500, // Eblan Alumen — level 84
        [36165] = 114, // Manganese Ore — level 86
    };

    private static RetainerProfile Miner(int level = 86, int gathering = 2100) => new("T'sala", "MIN", level, 0, gathering);

    [Fact]
    public void LowerLevelVenture_CanOutrankAHigherOne_BecauseValueIsThePoint()
    {
        var ranked = VentureCatalog.Rank(Miner(), new[] { Eblan(), Manganese() }, Prices);

        Assert.Equal("Eblan Alumen", ranked[0].Venture.Name);   // level 84 beats level 86 at 500 vs 114 gil
        Assert.Equal("Manganese Ore", ranked[1].Venture.Name);
        Assert.True(ranked[0].ValuePerHour > ranked[1].ValuePerHour);
    }

    [Fact]
    public void Best_SkipsWhatTheRetainerCannotRun()
    {
        var blocked = Hunt(905, "Annite", 36181, 87, 2119, 952900, new[] { 2119, 2165, 2211, 2257, 2303 });
        var best = VentureCatalog.Best(Miner(level: 86), new[] { Eblan(), blocked }, new Dictionary<uint, long> { [36181] = 299, [36241] = 500 });

        Assert.NotNull(best);
        Assert.Equal("Eblan Alumen", best!.Venture.Name);
    }

    [Fact]
    public void Best_NeverPicksARandomPool()
    {
        var exploration = new VentureDef(883, "Field Exploration", VentureKind.Exploration, 0, string.Empty,
            Array.Empty<int>(), Array.Empty<int>(), false, 85, 0, 2027, "MIN", 1080, 2, 4335200, true);

        var best = VentureCatalog.Best(Miner(), new[] { exploration, Manganese(), Eblan() }, Prices);

        Assert.NotNull(best);
        Assert.False(best!.Venture.IsRandom);
    }

    [Fact]
    public void WithoutPrices_TheOrderFallsBackToExperience()
    {
        var cheap = Hunt(903, "Manganese Ore", 36165, 86, 2073, 923100);
        var dear = Hunt(901, "Eblan Alumen", 36241, 84, 1980, 794800);

        var ranked = VentureCatalog.Rank(Miner(), new[] { dear, cheap }, new Dictionary<uint, long>());

        Assert.Equal("Manganese Ore", ranked[0].Venture.Name); // 923,100 exp beats 794,800
    }

    [Fact]
    public void Blockers_NameTheReason()
    {
        Assert.Equal("needs level 87", VentureCatalog.Blocker(Miner(level: 86), Hunt(905, "Annite", 36181, 87, 2119, 952900)));
        // A level-86 BTN venture, so level is not the reason under test — the job is.
        Assert.Equal("needs BTN", VentureCatalog.Blocker(Miner(), Hunt(924, "Ironwood Log", 36193, 86, 2073, 923100) with { JobCategory = "BTN" }));
        Assert.Null(VentureCatalog.Blocker(Miner(), Eblan()));
    }

    [Fact]
    public void AnUnknownStat_IsNotABlocker()
    {
        // Reading the retainer's gear is a separate job: until it works, "unknown" must not read as "fails",
        // or every retainer comes back with nothing runnable.
        var blind = new RetainerProfile("T'sala", "MIN", 86);
        var needsGear = Hunt(903, "Manganese Ore", 36165, 86, 2073, 923100);

        Assert.Null(VentureCatalog.Blocker(blind, needsGear));
        Assert.False(VentureCatalog.Tier(blind, needsGear).Known);
    }

    [Fact]
    public void Tier_FollowsTheGatheringLadder()
    {
        var venture = Manganese(); // thresholds 1980 2027 2073 2119 2165

        Assert.Equal(0, VentureCatalog.Tier(new RetainerProfile("x", "MIN", 86, 0, 1980), venture).Index);
        Assert.Equal(2, VentureCatalog.Tier(new RetainerProfile("x", "MIN", 86, 0, 2100), venture).Index);
        Assert.Equal(4, VentureCatalog.Tier(new RetainerProfile("x", "MIN", 86, 0, 9999), venture).Index);
        Assert.True(VentureCatalog.Tier(new RetainerProfile("x", "MIN", 86, 0, 2100), venture).Known);
    }

    [Theory]
    [InlineData("MIN", "MIN", true)]
    [InlineData("BTN", "MIN", false)]
    [InlineData("WAR", "WAR,PLD,DRK", true)]
    [InlineData("MIN", "", true)]
    [InlineData("", "MIN", false)]
    public void JobFit_IsATokenMatch(string job, string category, bool expected)
    {
        Assert.Equal(expected, VentureCatalog.JobFits(job, category));
    }

    [Fact]
    public void RatesArePerHour_SoAVentureIsComparableToAClock()
    {
        var hunt = Manganese();
        var exploration = new VentureDef(883, "Field Exploration", VentureKind.Exploration, 0, string.Empty,
            Array.Empty<int>(), Array.Empty<int>(), false, 85, 0, 2027, "MIN", 1080, 2, 4335200, true);

        Assert.Equal(923100, hunt.ExpPerHour);                 // an hour long, so unchanged
        Assert.Equal(4335200 * 60 / 1080, exploration.ExpPerHour);
        Assert.True(exploration.CostPerHour < hunt.CostPerHour); // 2 tokens over 18h is cheap per hour
    }

    [Fact]
    public void Describe_SaysWhenTheTierIsAssumed()
    {
        var option = VentureCatalog.Rank(new RetainerProfile("x", "MIN", 86), new[] { Manganese() }, Prices)[0];

        Assert.Contains("tier assumed", VentureCatalog.Describe(option));
    }
}
