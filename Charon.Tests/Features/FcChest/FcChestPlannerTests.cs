using Charon.Features.FcChest;

namespace Charon.Tests.Features.FcChest;

public sealed class FcChestPlannerTests
{
    private const int Bag = 0;
    private const int Chest = 20000;

    private static ItemStack Stack(uint itemId, int qty, int container = Bag, short slot = 0) =>
        new(itemId, $"item {itemId}", qty, container, slot);

    // --- Entrust ---

    [Fact]
    public void Entrust_OnlySeededItems_NeverSeedsChestWithNewItems()
    {
        var inventory = new[] { Stack(100, 49, Bag, 0), Stack(200, 10, Bag, 1) };
        var chestPage = new[] { Stack(100, 5, Chest, 0) }; // only item 100 seeded

        var moves = FcChestPlanner.PlanEntrust(inventory, chestPage);

        var move = Assert.Single(moves);
        Assert.Equal(100u, move.ItemId);
        Assert.Equal(49, move.Quantity);
    }

    [Fact]
    public void Entrust_UnseededItem_KeepsEverythingInInventory()
    {
        // The keep-minimum-1 guarantee: an item NOT on the page is never entrusted at all,
        // so the last copy can never leave the player's holdings.
        var inventory = new[] { Stack(100, 1, Bag, 0) };
        var chestPage = Array.Empty<ItemStack>();

        Assert.Empty(FcChestPlanner.PlanEntrust(inventory, chestPage));
    }

    [Fact]
    public void Entrust_SeededItem_ChestSeedIsTheKeptCopy()
    {
        // Whole-stack API: the page seed guarantees >= 1 copy remains after entrusting
        // every inventory stack.
        var inventory = new[] { Stack(100, 20, Bag, 0), Stack(100, 30, Bag, 1) };
        var chestPage = new[] { Stack(100, 1, Chest, 0) };

        var moves = FcChestPlanner.PlanEntrust(inventory, chestPage);

        Assert.Equal(2, moves.Count);
        Assert.All(moves, m => Assert.Equal(100u, m.ItemId));
    }

    [Fact]
    public void Entrust_IgnoresEmptySlots()
    {
        var inventory = new[] { Stack(0, 0, Bag, 0), Stack(100, 5, Bag, 1) };
        var chestPage = new[] { Stack(100, 1, Chest, 0) };

        Assert.Single(FcChestPlanner.PlanEntrust(inventory, chestPage));
    }

    // --- Withdraw ---

    [Fact]
    public void Withdraw_KeepsLastStackAsSeed()
    {
        var chestPage = new[]
        {
            Stack(100, 40, Chest, 0),
            Stack(100, 40, Chest, 1),
            Stack(100, 7, Chest, 2),
        };

        var moves = FcChestPlanner.PlanWithdraw(chestPage);

        Assert.Equal(2, moves.Count); // last stack (slot 2) stays
        Assert.DoesNotContain(moves, m => m.SrcSlot == 2);
    }

    [Fact]
    public void Withdraw_SingleStack_LeavesTheSeed()
    {
        var chestPage = new[] { Stack(100, 99, Chest, 0) };
        Assert.Empty(FcChestPlanner.PlanWithdraw(chestPage));
    }

    [Fact]
    public void Withdraw_MultipleItems_SeedRulePerItem()
    {
        var chestPage = new[]
        {
            Stack(100, 40, Chest, 0),
            Stack(100, 10, Chest, 1),
            Stack(200, 5, Chest, 2), // single stack — stays
        };

        var moves = FcChestPlanner.PlanWithdraw(chestPage);

        var move = Assert.Single(moves);
        Assert.Equal(100u, move.ItemId);
        Assert.Equal(0, move.SrcSlot);
    }

    // --- Gate ---

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)] // near/open but page tab never viewed
    [InlineData(false, true, false)] // not near the chest
    [InlineData(true, true, true)]
    public void Operations_RequireChestOpenAndPageLoaded(bool chestOpen, bool pageLoaded, bool expected)
    {
        Assert.Equal(expected, FcChestPlanner.CanExecute(chestOpen, pageLoaded));
    }
}
