using System.Linq;
using Charon.Features.Gear;
using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

/// <summary>
/// The goal figures are the real level-87 miner hunt (RetainerTask 905, verified on XIVAPI): Gathering
/// 2,119 to be sent at all, Perception gates at 2,247 / 3,210 / 3,531 / 3,852 over five quantities.
/// </summary>
public sealed class RetainerGearTests
{
    private static readonly GearGoal MinerHunt = new(
        Gatherer: true, RequiredGathering: 2119, RequiredItemLevel: 0,
        Thresholds: [2247, 3210, 3531, 3852], QuantitySteps: 5, VentureName: "Lv. 87 hunt");

    private static RetainerPiece Worn(GearSlot slot, int gathering, int perception, int ilvl = 500, string name = "worn") =>
        new(1000 + (uint)slot, name, false, slot, ilvl, gathering, perception);

    private static RetainerPiece Held(GearSlot slot, int gathering, int perception, int ilvl = 500, uint id = 0, int bagSlot = 0, bool unique = false) =>
        new(id == 0 ? 2000 + (uint)slot : id, "held", false, slot, ilvl, gathering, perception, false, unique, Container: 0, SourceSlot: bagSlot);

    /// <summary>A full worn set adding up to <paramref name="gathering"/> / <paramref name="perception"/>, spread evenly.</summary>
    private static RetainerPiece[] Set(int gathering, int perception)
    {
        GearSlot[] slots =
        [
            GearSlot.MainHand, GearSlot.OffHand, GearSlot.Head, GearSlot.Body, GearSlot.Hands, GearSlot.Legs,
            GearSlot.Feet, GearSlot.Ears, GearSlot.Neck, GearSlot.Wrists, GearSlot.RingRight, GearSlot.RingLeft,
        ];
        return slots.Select((s, i) => Worn(s,
            gathering / 12 + (i == 0 ? gathering % 12 : 0),
            perception / 12 + (i == 0 ? perception % 12 : 0))).ToArray();
    }

    // --- Stats ---

    [Fact]
    public void MateriaCounts_AndTheTotalIsCapped()
    {
        // base 300, HQ +33, two melds of 36 — capped at what the item level allows.
        Assert.Equal(405, RetainerGearMath.StatWithMateria(300, 33, hq: true, [36, 36], cap: 0));
        Assert.Equal(380, RetainerGearMath.StatWithMateria(300, 33, hq: true, [36, 36], cap: 380));
        Assert.Equal(372, RetainerGearMath.StatWithMateria(300, 33, hq: false, [36, 36], cap: 0));
    }

    [Fact]
    public void AverageItemLevelIsOverTwelveSlots_AndATwoHanderCountsTwice()
    {
        var twoHander = new RetainerPiece(1, "greatsword", false, GearSlot.MainHand, 600, 0, 0, TwoHanded: true);
        var rest = new[] { GearSlot.Head, GearSlot.Body, GearSlot.Hands, GearSlot.Legs, GearSlot.Feet, GearSlot.Ears, GearSlot.Neck, GearSlot.Wrists, GearSlot.RingRight, GearSlot.RingLeft }
            .Select(s => Worn(s, 0, 0, ilvl: 600));

        Assert.Equal(600, RetainerGearMath.Stats(rest.Prepend(twoHander)).ItemLevel);
    }

    // --- Tiers and gates ---

    [Fact]
    public void TheGoalReadsTheRightStatForTheRightJob()
    {
        var stats = new RetainerGearStats(ItemLevel: 640, Gathering: 2500, Perception: 3300);

        Assert.True(MinerHunt.Meets(stats));
        Assert.Equal(2, MinerHunt.Tier(stats));      // clears 2,247 and 3,210
        Assert.Equal(3531, MinerHunt.NextGate(stats));
    }

    // --- The gatherer search ---

    [Fact]
    public void ASwapThatReachesTheNextTier_IsProposed()
    {
        var worn = Set(gathering: 2400, perception: 3150);                       // tier 1, 60 short of 3,210
        var better = Held(GearSlot.Head, gathering: 200, perception: 350);       // worn head is 200 / 262

        var plan = RetainerGearOptimizer.Plan(worn, [better], MinerHunt);

        var swap = Assert.Single(plan.Swaps);
        Assert.Equal(GearSlot.Head, swap.Slot);
        Assert.Equal(1, plan.TierBefore);
        Assert.Equal(2, plan.TierAfter);
    }

    [Fact]
    public void MorePerceptionThatReachesNoNewTier_IsNotWorthASwap()
    {
        // Perception between two gates buys no extra items — shuffling gear for it is churn.
        var worn = Set(gathering: 2400, perception: 3000);
        var slightlyBetter = Held(GearSlot.Head, gathering: 200, perception: 270); // +20, still under 3,210

        var plan = RetainerGearOptimizer.Plan(worn, [slightlyBetter], MinerHunt);

        Assert.Empty(plan.Swaps);
        Assert.Contains("3210", plan.Note);
    }

    [Fact]
    public void PerceptionIsNeverBoughtWithTheGatheringTheVentureNeeds()
    {
        // A piece with far more Perception but so little Gathering that the venture would lock.
        var worn = Set(gathering: 2130, perception: 3150);                       // 11 over the gate
        var greedy = Held(GearSlot.Body, gathering: 20, perception: 900);        // worn body is 177 / 262

        var plan = RetainerGearOptimizer.Plan(worn, [greedy], MinerHunt);

        Assert.Empty(plan.Swaps);
        Assert.True(plan.MeetsAfter);
    }

    [Fact]
    public void WhenTheGateIsOutOfReach_ItGetsAsCloseAsItCan()
    {
        var worn = Set(gathering: 1800, perception: 3300);
        var more = Held(GearSlot.Legs, gathering: 300, perception: 100);         // worn legs are 150 / 275

        var plan = RetainerGearOptimizer.Plan(worn, [more], MinerHunt);

        Assert.Single(plan.Swaps);
        Assert.False(plan.MeetsAfter);
        Assert.Equal(1950, plan.After.Gathering);
        Assert.Contains("still short of Gathering 2119", plan.Note);
    }

    [Fact]
    public void UnlockingTheVentureBeatsKeepingPerception()
    {
        var worn = Set(gathering: 2000, perception: 3900);                       // top tier on paper, but locked out
        var gathering = Held(GearSlot.Body, gathering: 320, perception: 100);    // worn body is 166 / 325

        var plan = RetainerGearOptimizer.Plan(worn, [gathering], MinerHunt);

        Assert.Single(plan.Swaps);
        Assert.False(plan.MeetsBefore);
        Assert.True(plan.MeetsAfter);
    }

    [Fact]
    public void OnePhysicalRingCannotFillBothHands()
    {
        var worn = Set(2400, 3150);
        var ring = Held(GearSlot.RingRight, gathering: 200, perception: 400, id: 9001, bagSlot: 3);

        var plan = RetainerGearOptimizer.Plan(worn, [ring], MinerHunt);

        Assert.Single(plan.Swaps.Where(s => s.Wear.ItemId == 9001));
    }

    [Fact]
    public void TwoCopiesOfAUniqueRing_AreNotBothWorn()
    {
        var worn = Set(2400, 3150);
        var a = Held(GearSlot.RingRight, 200, 400, id: 9001, bagSlot: 3, unique: true);
        var b = Held(GearSlot.RingRight, 200, 400, id: 9001, bagSlot: 4, unique: true);

        var plan = RetainerGearOptimizer.Plan(worn, [a, b], MinerHunt);

        Assert.Equal(1, plan.Swaps.Count(s => s.Wear.ItemId == 9001));
    }

    [Fact]
    public void AWornRingStaysOnItsOwnHand()
    {
        var worn = Set(2400, 3150);
        var ring = Held(GearSlot.RingRight, gathering: 200, perception: 400, id: 9001);

        var plan = RetainerGearOptimizer.Plan(worn, [ring], MinerHunt);

        // Exactly one ring changes; the other worn ring is not moved to the opposite hand.
        Assert.Single(plan.Swaps);
    }

    [Fact]
    public void AnEmptySlotIsFilled()
    {
        var worn = Set(2400, 3150).Where(p => p.Slot != GearSlot.Neck).ToArray(); // 2,200 / 2,888 without the neck
        var neck = Held(GearSlot.Neck, gathering: 200, perception: 400);

        var plan = RetainerGearOptimizer.Plan(worn, [neck], MinerHunt);

        var swap = Assert.Single(plan.Swaps);
        Assert.Null(swap.Replacing);
        Assert.Equal(GearSlot.Neck, swap.Slot);
    }

    // --- Combat ---

    [Fact]
    public void ACombatRetainerGetsTheHighestItemLevel()
    {
        var goal = new GearGoal(false, 0, RequiredItemLevel: 400, Thresholds: [430, 460, 490, 520], QuantitySteps: 5, "hunt");
        var worn = Set(0, 0).Select(p => p with { ItemLevel = 425 }).ToArray(); // average 425: under the first gate
        var upgrades = new[] { GearSlot.Head, GearSlot.Body, GearSlot.Legs }.Select(s => Held(s, 0, 0, ilvl: 460)).ToArray();

        var plan = RetainerGearOptimizer.Plan(worn, upgrades, goal);

        Assert.Equal(3, plan.Swaps.Count);
        Assert.Equal(433, plan.After.ItemLevel);
        Assert.Equal(1, plan.TierAfter);
    }

    [Fact]
    public void ATwoHander_IsWeighedAgainstATwoPieceSet()
    {
        var goal = new GearGoal(false, 0, 400, [430, 460, 490, 520], 5, "hunt");
        var worn = Set(0, 0).Select(p => p with { ItemLevel = 425 }).ToArray();
        var twoHander = new RetainerPiece(7000, "greatsword", false, GearSlot.MainHand, 500, 0, 0, TwoHanded: true, Container: 0, SourceSlot: 1);

        var plan = RetainerGearOptimizer.Plan(worn, [twoHander], goal);

        // 2 × 500 beats 425 + 425: the plan wears it, and the off-hand is no longer part of the set.
        Assert.Contains(plan.Swaps, s => s.Wear.ItemId == 7000);
        Assert.Equal((500 * 2 + 425 * 10) / 12, plan.After.ItemLevel);
    }

    // --- Which venture the gear is chosen for ---

    private static VentureDef Hunt(uint id, string job, int level, int gathering, int[] gates, bool random = false) =>
        new(id, $"hunt {id}", VentureKind.Hunting, id, $"item {id}", [15, 20, 30, 40, 50], gates, false,
            level, 0, gathering, job, 60, 1, 1000, random);

    private static readonly VentureDef[] MinerLadder =
    [
        Hunt(903, "MIN", 86, 2073, [2198, 3140, 3454, 3768]),
        Hunt(905, "MIN", 87, 2119, [2247, 3210, 3531, 3852]),
        Hunt(906, "MIN", 88, 2165, [2296, 3280, 3608, 3936]),
        Hunt(926, "BTN", 87, 2119, [2247, 3210, 3531, 3852]),
    ];

    [Fact]
    public void TheGoalIsTheTopHuntHerLevelAllows_NotWhatSheCanRunToday()
    {
        // A level-87 miner aims at the level-87 gate (2,119), not the 86 one she may already clear —
        // and never at the level-88 hunt she cannot take yet.
        var goal = GearGoals.For("MIN", 87, picked: null, MinerLadder);

        Assert.True(goal.Gatherer);
        Assert.Equal(2119, goal.RequiredGathering);
        Assert.Equal([2247, 3210, 3531, 3852], goal.Thresholds);
        Assert.Equal(5, goal.QuantitySteps);
    }

    [Fact]
    public void AVentureThePlayerPicked_IsTheGoal()
    {
        var goal = GearGoals.For("MIN", 87, picked: MinerLadder[0], MinerLadder);
        Assert.Equal(2073, goal.RequiredGathering);
    }

    [Fact]
    public void AnotherJobsHunts_AreNotHerGoal()
    {
        var goal = GearGoals.For("BTN", 88, picked: null, MinerLadder);
        Assert.Equal(2119, goal.RequiredGathering); // the only botanist hunt in the list
    }

    [Fact]
    public void WithNoVentureKnown_TheGoalAsksForNothing()
    {
        var goal = GearGoals.For("FSH", 50, picked: null, MinerLadder);

        Assert.True(goal.Gatherer);
        Assert.Equal(0, goal.RequiredGathering);
        Assert.True(goal.Meets(new RetainerGearStats(0, 0, 0)));
    }

    // --- The game's rule ---

    [Fact]
    public void GearCannotBeChangedWhileSheIsOutOnAVenture()
    {
        // The first live run failed on every piece for exactly this.
        Assert.Null(RetainerGearRules.ChangeBlocker("T'sala", VentureState.Idle));
        Assert.Contains("out on a venture", RetainerGearRules.ChangeBlocker("T'sala", VentureState.Running));
        Assert.Contains("out on a venture", RetainerGearRules.ChangeBlocker("T'sala", VentureState.Unknown));
    }

    [Fact]
    public void AnUncollectedVentureStillCountsAsOut_AndSaysHowToGetHerBack()
    {
        var blocker = RetainerGearRules.ChangeBlocker("T'sala", VentureState.Ready);
        Assert.Contains("WITHOUT reassigning", blocker);
    }
}
