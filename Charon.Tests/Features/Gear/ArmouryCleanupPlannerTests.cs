using Charon.Features.Gear;

namespace Charon.Tests.Features.Gear;

public sealed class ArmouryCleanupPlannerTests
{
    private static ArmouryItem Armoury(uint id, short slot = 0, bool soulCrystal = false) =>
        new(id, $"item {id}", Container: 3200, Slot: slot, IsSoulCrystal: soulCrystal);

    /// <summary>Nothing on the keep list — the common case.</summary>
    private static List<ArmouryItem> Plan(IReadOnlyList<ArmouryItem> armoury, uint[] gearsetIds) =>
        ArmouryCleanupPlanner.Plan(armoury, gearsetIds, []);

    [Fact]
    public void ItemsNotInAnyGearset_AreEvicted()
    {
        var plan = Plan(
            [Armoury(100), Armoury(200, 1), Armoury(300, 2)],
            [100u, 300u]);

        var item = Assert.Single(plan);
        Assert.Equal(200u, item.ItemId);
    }

    [Fact]
    public void GearsetItems_AreNeverTouched()
    {
        var plan = Plan([Armoury(100), Armoury(200, 1)], [100u, 200u]);
        Assert.Empty(plan);
    }

    [Fact]
    public void SoulCrystals_AreNeverEvicted_EvenWithNoGearsetForThatJob()
    {
        var plan = Plan(
            [Armoury(100, 0, soulCrystal: true), Armoury(200, 1)],
            [999u]);

        Assert.Equal(200u, Assert.Single(plan).ItemId);
    }

    [Fact]
    public void EmptyGearsetSet_EvictsNothing_ReadsAsNotLoadedYet()
    {
        // Guessing "no gearsets" would empty the entire armoury — refuse instead.
        Assert.Empty(Plan([Armoury(100), Armoury(200, 1)], []));
    }

    [Fact]
    public void EmptySlots_AreIgnored()
    {
        var plan = Plan([Armoury(0), Armoury(200, 1)], [100u]);
        Assert.Equal(200u, Assert.Single(plan).ItemId);
    }

    [Fact]
    public void EverySlotOfAnUnregisteredItem_IsEvicted()
    {
        var plan = Plan(
            [Armoury(200, 0), Armoury(200, 1), Armoury(100, 2)],
            [100u]);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, i => Assert.Equal(200u, i.ItemId));
    }

    // --- Keep list (the per-item veto from the cleanup preview) ---

    [Fact]
    public void KeptItems_AreNeverEvicted()
    {
        var plan = ArmouryCleanupPlanner.Plan(
            [Armoury(200), Armoury(300, 1)],
            [100u],
            keepItemIds: [200u]);

        Assert.Equal(300u, Assert.Single(plan).ItemId);
    }

    [Fact]
    public void KeepList_AppliesToEveryStackOfTheItem()
    {
        var plan = ArmouryCleanupPlanner.Plan(
            [Armoury(200, 0), Armoury(200, 1), Armoury(200, 2)],
            [100u],
            keepItemIds: [200u]);

        Assert.Empty(plan);
    }

    [Fact]
    public void KeepingAnItemAlreadyInAGearset_ChangesNothing()
    {
        var plan = ArmouryCleanupPlanner.Plan([Armoury(100)], [100u], keepItemIds: [100u]);
        Assert.Empty(plan);
    }

    // --- The preview list ---

    [Fact]
    public void Unregistered_ListsKeptItemsToo_SoTheVetoCanBeUndone()
    {
        // The preview must keep showing a vetoed item — otherwise ticking the box makes it
        // vanish and there is no way to un-tick it.
        var rows = ArmouryCleanupPlanner.Unregistered([Armoury(200), Armoury(300, 1)], [100u]);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Unregistered_StillHidesGearsetItemsAndSoulCrystals()
    {
        var rows = ArmouryCleanupPlanner.Unregistered(
            [Armoury(100), Armoury(200, 1), Armoury(300, 2, soulCrystal: true)],
            [100u]);

        Assert.Equal(200u, Assert.Single(rows).ItemId);
    }

    [Fact]
    public void Unregistered_EmptyGearsetSet_ListsNothing()
    {
        Assert.Empty(ArmouryCleanupPlanner.Unregistered([Armoury(100), Armoury(200, 1)], []));
    }
}
