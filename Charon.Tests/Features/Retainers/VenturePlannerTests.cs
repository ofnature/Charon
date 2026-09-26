using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

/// <summary>
/// Turning the settings into a plan: which venture a retainer runs, and how a farm list maps onto the
/// retainers that can serve it. The point of these tests is that "why is she running that" is answerable.
/// </summary>
public sealed class VenturePlannerTests
{
    private static VentureDef Hunt(uint id, string item, uint itemId, int level, string job, long exp = 900000) =>
        new(id, item, VentureKind.Hunting, itemId, item,
            new[] { 15, 20, 30, 40, 50 }, new[] { 1980, 2027, 2073, 2119, 2165 }, false,
            level, 0, 2073, job, 60, 1, exp, false);

    private static readonly VentureDef Manganese = Hunt(903, "Manganese Ore", 36165, 86, "MIN", 923100);
    private static readonly VentureDef Eblan = Hunt(901, "Eblan Alumen", 36241, 84, "MIN", 794800);
    private static readonly VentureDef Sykon = Hunt(926, "Sykon", 36096, 87, "BTN", 952900);
    private static readonly VentureDef Annite = Hunt(905, "Annite", 36181, 87, "MIN", 952900);

    private static readonly Dictionary<uint, long> Prices = new()
    {
        [36241] = 500,
        [36165] = 114,
        [36096] = 53,
        [36181] = 299,
    };

    private static RetainerProfile Miner = new("T'sala", "MIN", 86, 0, 2100);
    private static RetainerProfile Botanist = new("T'sola", "BTN", 87, 0, 2100);
    private static readonly List<VentureDef> All = new() { Manganese, Eblan, Sykon, Annite };

    [Theory]
    [InlineData("36165", 36165u, "", null)]
    [InlineData("Manganese Ore", 0u, "Manganese Ore", null)]
    [InlineData("36165 x500", 36165u, "", 500)]
    [InlineData("Manganese Ore x500", 0u, "Manganese Ore", 500)]
    [InlineData("  Sykon  x  250 ", 0u, "Sykon", 250)]
    [InlineData("", 0u, "", null)]
    public void FarmLine_Parsing(string line, uint id, string name, int? wanted)
    {
        var parsed = FarmTarget.TryParse(line, out var target);

        if (line.Trim().Length == 0)
        {
            Assert.False(parsed);
            return;
        }

        Assert.True(parsed);
        Assert.Equal(id, target.ItemId);
        Assert.Equal(name, target.Name);
        Assert.Equal(wanted, target.Wanted);
    }

    [Fact]
    public void ModeOff_PlansNothing()
    {
        Assert.Null(VenturePlanner.Resolve(Miner, VentureAssignment.Off, 0, Array.Empty<FarmTarget>(), All, Prices));
    }

    [Fact]
    public void ModePicked_RunsTheChosenVenture()
    {
        var option = VenturePlanner.Resolve(Miner, VentureAssignment.Picked, Manganese.TaskId, Array.Empty<FarmTarget>(), All, Prices);

        Assert.NotNull(option);
        Assert.Equal("Manganese Ore", option!.Venture.Name);
    }

    [Fact]
    public void ModePicked_RefusesAVentureTheRetainerCannotRun()
    {
        // Annite needs level 87; T'sala is 86. A hand-picked plan must not silently become a different one.
        Assert.Null(VenturePlanner.Resolve(Miner, VentureAssignment.Picked, Annite.TaskId, Array.Empty<FarmTarget>(), All, Prices));
    }

    [Fact]
    public void ModeBestValue_TakesTheBestItCanDo()
    {
        var option = VenturePlanner.Resolve(Miner, VentureAssignment.BestValue, 0, Array.Empty<FarmTarget>(), All, Prices);

        Assert.NotNull(option);
        Assert.Equal("Eblan Alumen", option!.Venture.Name); // 500 gil/h beats the level-86 hunt
    }

    [Fact]
    public void ModeFromFarm_OnlyPicksSomethingOnTheList()
    {
        var farm = new List<FarmTarget> { new(36165, "Manganese Ore", 999) };
        var option = VenturePlanner.Resolve(Miner, VentureAssignment.FromFarm, 0, farm, All, Prices);

        Assert.NotNull(option);
        Assert.Equal("Manganese Ore", option!.Venture.Name);
    }

    [Fact]
    public void ModeFromFarm_WithNothingOnTheList_DoesNotGuess()
    {
        var farm = new List<FarmTarget> { new(99999, "Nothing On The Sheet", 10) };

        Assert.Null(VenturePlanner.Resolve(Miner, VentureAssignment.FromFarm, 0, farm, All, Prices));
    }

    [Fact]
    public void FarmPlan_UsesDistinctRetainers_AndNamesWhatItCannotServe()
    {
        var farm = new List<FarmTarget>
        {
            new(36165, "Manganese Ore", 999),
            new(36096, "Sykon", 200),
            new(36181, "Annite", 500),
        };

        var plans = VenturePlanner.PlanFarm(farm, new[] { Miner, Botanist }, All, Prices);

        Assert.Equal(3, plans.Count);
        var served = plans.Where(p => p.Blocker == null).ToList();
        Assert.Equal(2, served.Count);
        Assert.Equal("T'sala", served.Single(p => p.Target.ItemId == 36165).Retainer);
        Assert.Equal("Manganese Ore", served.Single(p => p.Target.ItemId == 36165).Venture!.Name);
        Assert.Equal("T'sola", served.Single(p => p.Target.ItemId == 36096).Retainer);
        Assert.Equal("no retainer can bring this back yet", plans.Single(p => p.Target.ItemId == 36181).Blocker);
    }

    [Fact]
    public void FarmPlan_CountsTheRunsStillNeeded()
    {
        var farm = new List<FarmTarget> { new(36165, "Manganese Ore", 100) };
        var plans = VenturePlanner.PlanFarm(farm, new[] { Miner }, All, Prices);
        var plan = plans.Single();

        Assert.Equal(30, plan.QuantityPerRun); // gathering 2100 lands the 30-per-run tier
        Assert.Equal(4, plan.RunsNeeded);      // 100 wanted at 30 a run
    }

    [Fact]
    public void FarmPlan_UnknownWantedCount_HasNoRunEstimate()
    {
        var farm = new List<FarmTarget> { new(36165, "Manganese Ore", null) };
        var plan = VenturePlanner.PlanFarm(farm, new[] { Miner }, All, Prices).Single();

        Assert.Null(plan.RunsNeeded);
    }
}
