using System.Collections.Generic;
using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class EntrustRoundTests
{
    private const uint IronOre = 5111, Mythril = 5114, Cotton = 5343, Maple = 5380;

    private static readonly IReadOnlyList<string> Retainers = ["T'sala", "T'sola", "Third"];

    [Fact]
    public void ARetainerHoldingSomethingYouCarry_IsVisited()
    {
        var snapshots = new Dictionary<string, IReadOnlyCollection<uint>>
        {
            ["T'sala"] = [IronOre, Mythril],
            ["T'sola"] = [Cotton],
            ["Third"] = [Maple],
        };

        var plan = EntrustRound.Plan(Retainers, snapshots, [IronOre, Mythril, Maple]);

        Assert.Equal(2, plan.Count);
        Assert.Equal(new EntrustVisit("T'sala", 2), plan[0]);
        Assert.Equal(new EntrustVisit("Third", 1), plan[1]);
    }

    [Fact]
    public void OneWhoseSnapshotSharesNothing_IsNotSummoned()
    {
        // A summon that could only end in a greyed-out Entrust Duplicates button.
        var snapshots = new Dictionary<string, IReadOnlyCollection<uint>> { ["T'sola"] = [Cotton] };
        var plan = EntrustRound.Plan(["T'sola"], snapshots, [IronOre]);
        Assert.Empty(plan);
    }

    [Fact]
    public void OneNeverRead_IsVisited_BecauseSheMightHoldIt()
    {
        var plan = EntrustRound.Plan(["Third"], new Dictionary<string, IReadOnlyCollection<uint>>(), [IronOre]);

        var visit = Assert.Single(plan);
        Assert.Equal(-1, visit.Matches);
    }

    [Fact]
    public void SeveralStacksOfOneItemCountOnce()
    {
        var snapshots = new Dictionary<string, IReadOnlyCollection<uint>> { ["T'sala"] = [IronOre, IronOre, IronOre] };
        Assert.Equal(1, EntrustRound.Plan(["T'sala"], snapshots, [IronOre])[0].Matches);
    }

    [Fact]
    public void TheNextVisitIsTheFirstNotYetMade_AndNullWhenAllAreDone()
    {
        IReadOnlyList<EntrustVisit> plan = [new("T'sala", 2), new("Third", -1)];

        Assert.Equal("T'sala", EntrustRound.Next(plan, []));
        Assert.Equal("Third", EntrustRound.Next(plan, ["T'sala"]));
        Assert.Null(EntrustRound.Next(plan, ["T'sala", "Third"]));
    }
}
