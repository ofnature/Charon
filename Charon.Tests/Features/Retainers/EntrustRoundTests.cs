using System.Collections.Generic;
using System.Linq;
using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class EntrustRoundTests
{
    private const uint IronOre = 5111, Mythril = 5114, Cotton = 5343, Maple = 5380;
    private const uint FireShard = 2, WaterCluster = 19;

    // Real items from the second live look (2026-10-18): gear never stacks, the dye stacks to 999.
    private const uint ThavnairianSarouel = 10390, FrontierShoes = 33658, PastelBlueDye = 13713;

    private static int StackSize(uint itemId) => itemId is ThavnairianSarouel or FrontierShoes ? 1 : 999;

    private static readonly IReadOnlyList<string> Retainers = ["T'sala", "T'sola", "Third"];

    private static IReadOnlyCollection<HeldStack> Held(params uint[] keys) => keys.Select(k => new HeldStack(k, 10)).ToList();

    private static IReadOnlyList<EntrustVisit> Plan(
        IReadOnlyList<string> retainers, Dictionary<string, IReadOnlyCollection<HeldStack>> snapshots, params uint[] bags) =>
        EntrustRound.Plan(retainers, snapshots, bags, StackSize);

    [Fact]
    public void ARetainerHoldingSomethingYouCarry_IsVisited()
    {
        var snapshots = new Dictionary<string, IReadOnlyCollection<HeldStack>>
        {
            ["T'sala"] = Held(IronOre, Mythril),
            ["T'sola"] = Held(Cotton),
            ["Third"] = Held(Maple),
        };

        var plan = Plan(Retainers, snapshots, IronOre, Mythril, Maple);

        Assert.Equal(2, plan.Count);
        Assert.Equal("T'sala", plan[0].Name);
        Assert.Equal([IronOre, Mythril], plan[0].Items);
        Assert.Equal("Third", plan[1].Name);
        Assert.Equal(1, plan[1].Matches);
    }

    [Fact]
    public void OneWhoseSnapshotSharesNothing_IsNotSummoned()
    {
        // A summon that could only end in a greyed-out Entrust Duplicates button.
        var snapshots = new Dictionary<string, IReadOnlyCollection<HeldStack>> { ["T'sola"] = Held(Cotton) };
        Assert.Empty(Plan(["T'sola"], snapshots, IronOre));
    }

    [Fact]
    public void CrystalsHeldOnBothSides_AreNotAMatch()
    {
        // The first live use: two retainers offered "2 matching items" each, and the game's button found
        // nothing — both sides hold shards.
        var snapshots = new Dictionary<string, IReadOnlyCollection<HeldStack>> { ["T'sala"] = Held(FireShard, WaterCluster) };
        Assert.Empty(Plan(["T'sala"], snapshots, FireShard, WaterCluster));
    }

    [Fact]
    public void GearThatNeverStacks_IsNotAMatch()
    {
        // The second live look: Imdone was offered for a sarouel and a pair of shoes — no stack to add to.
        var snapshots = new Dictionary<string, IReadOnlyCollection<HeldStack>>
        {
            ["Imdone"] = [new HeldStack(ThavnairianSarouel, 1), new HeldStack(FrontierShoes, 1)],
        };
        Assert.Empty(Plan(["Imdone"], snapshots, ThavnairianSarouel, FrontierShoes));
    }

    [Fact]
    public void AFullStack_TakesNothingMore()
    {
        var full = new Dictionary<string, IReadOnlyCollection<HeldStack>> { ["Holdingmycrafts"] = [new HeldStack(PastelBlueDye, 999)] };
        Assert.Empty(Plan(["Holdingmycrafts"], full, PastelBlueDye));

        var room = new Dictionary<string, IReadOnlyCollection<HeldStack>>
        {
            ["Holdingmycrafts"] = [new HeldStack(PastelBlueDye, 999), new HeldStack(PastelBlueDye, 40)],
        };
        Assert.Single(Plan(["Holdingmycrafts"], room, PastelBlueDye));
    }

    [Fact]
    public void AnUnknownStackSize_RulesNothingOut()
    {
        Assert.True(EntrustRound.HasRoom(999, 0));
        Assert.False(EntrustRound.HasRoom(1, 1));
        Assert.True(EntrustRound.HasRoom(998, 999));
        Assert.False(EntrustRound.HasRoom(999, 999));
    }

    [Fact]
    public void QualityMustMatch_HqIsNeverEntrustedOntoNq()
    {
        var snapshots = new Dictionary<string, IReadOnlyCollection<HeldStack>> { ["T'sala"] = Held(EntrustRound.Key(IronOre, hq: false)) };

        Assert.Empty(Plan(["T'sala"], snapshots, EntrustRound.Key(IronOre, hq: true)));
        Assert.Single(Plan(["T'sala"], snapshots, EntrustRound.Key(IronOre, hq: false)));
    }

    [Fact]
    public void Keys_RoundTripItemAndQuality()
    {
        var key = EntrustRound.Key(IronOre, hq: true);
        Assert.Equal(IronOre, EntrustRound.ItemIdOf(key));
        Assert.True(EntrustRound.IsHq(key));
        Assert.False(EntrustRound.IsHq(IronOre));
        Assert.True(EntrustRound.IsCrystal(EntrustRound.Key(FireShard, hq: false)));
        Assert.False(EntrustRound.IsCrystal(IronOre));
    }

    [Fact]
    public void OneNeverRead_IsVisited_BecauseSheMightHoldIt()
    {
        var plan = Plan(["Third"], new Dictionary<string, IReadOnlyCollection<HeldStack>>(), IronOre);

        var visit = Assert.Single(plan);
        Assert.Equal(-1, visit.Matches);
        Assert.Empty(visit.Items);
    }

    [Fact]
    public void SeveralStacksOfOneItemCountOnce()
    {
        var snapshots = new Dictionary<string, IReadOnlyCollection<HeldStack>> { ["T'sala"] = Held(IronOre, IronOre, IronOre) };
        Assert.Equal(1, Plan(["T'sala"], snapshots, IronOre)[0].Matches);
    }

    [Fact]
    public void TheNextVisitIsTheFirstNotYetMade_AndNullWhenAllAreDone()
    {
        IReadOnlyList<EntrustVisit> plan = [new("T'sala", 2, [IronOre, Mythril]), new("Third", -1, [])];

        Assert.Equal("T'sala", EntrustRound.Next(plan, []));
        Assert.Equal("Third", EntrustRound.Next(plan, ["T'sala"]));
        Assert.Null(EntrustRound.Next(plan, ["T'sala", "Third"]));
    }
}
