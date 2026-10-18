using System.Linq;
using Charon.Features.Variant;

namespace Charon.Tests.Features.Variant;

public sealed class VariantTreesTests
{
    [Fact]
    public void EveryRoute_IsTheEndOfAtLeastOneBranch()
    {
        foreach (var dungeon in VariantRoutes.All)
        {
            var reached = VariantTrees.For(dungeon).Values.SelectMany(n => n.Leaves()).Select(l => l.Route).ToHashSet();
            Assert.Equal(dungeon.Routes.Select(r => r.Number).ToHashSet(), reached);
        }
    }

    [Fact]
    public void EveryBranch_EndsOnTheSidePathItsRouteBelongsTo()
    {
        // A route drawn under the wrong opening fork would send a party down the wrong door.
        foreach (var dungeon in VariantRoutes.All)
        foreach (var (path, root) in VariantTrees.For(dungeon))
        foreach (var leaf in root.Leaves())
            Assert.Equal(path, dungeon.Route(leaf.Route)!.Path);
    }

    [Fact]
    public void OnlyLeavesCarryARoute()
    {
        foreach (var dungeon in VariantRoutes.All)
        foreach (var root in VariantTrees.For(dungeon).Values)
        {
            Assert.Equal(0, root.Route);
            Assert.All(root.Leaves(), l => Assert.True(l.Route > 0));
        }
    }

    [Fact]
    public void AloaloRoute10_IsReachableBothWays()
    {
        var tens = VariantTrees.For(VariantRoutes.Aloalo)[VariantPath.Right].Leaves().Count(l => l.Route == 10);
        Assert.Equal(2, tens);
    }
}
