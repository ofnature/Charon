using Charon.Features.Consumables;

namespace Charon.Tests.Features.Consumables;

public sealed class ConsumablePolicyTests
{
    private static ConsumableItem Item(
        uint id = 16784, string name = "MGP Platinum Card",
        uint kind = ConsumableKinds.InstantMgp, int qty = 1,
        bool untradable = true, int value = 50_000, int container = 0, short slot = 0) =>
        new(id, name, kind, qty, untradable, value, container, slot);

    [Fact]
    public void KnownUntradableItems_AreOffered()
    {
        var usable = ConsumablePolicy.Usable([Item(qty: 3)]);
        Assert.Equal("MGP Platinum Card", Assert.Single(usable).Name);
    }

    [Fact]
    public void TradableCopies_AreLeftAlone()
    {
        // The whole reason this feature is safe is that the item has no market value. A tradable
        // copy of an allowed kind does, and using it would destroy it.
        Assert.Empty(ConsumablePolicy.Usable([Item(untradable: false)]));
    }

    [Fact]
    public void UnknownKinds_AreNeverUsed()
    {
        // The allowlist is the guard that stops this drinking every potion in the bags.
        Assert.Empty(ConsumablePolicy.Usable([Item(id: 4551, name: "Hi-Potion", kind: 1, value: 0)]));
    }

    [Fact]
    public void EmptySlotsAndEmptyStacks_AreIgnored()
    {
        Assert.Empty(ConsumablePolicy.Usable([Item(id: 0), Item(qty: 0)]));
    }

    [Fact]
    public void BiggestValueIsUsedFirst()
    {
        var usable = ConsumablePolicy.Usable([
            Item(id: 10131, name: "MGP Voucher", value: 100),
            Item(id: 16784, name: "MGP Platinum Card", value: 50_000),
            Item(id: 22522, name: "MGP Bronze Card", value: 5_000),
        ]);

        Assert.Equal(["MGP Platinum Card", "MGP Bronze Card", "MGP Voucher"], usable.Select(i => i.Name));
    }

    [Fact]
    public void TotalValue_CountsEveryUnitInEveryStack()
    {
        var total = ConsumablePolicy.TotalValue([
            Item(id: 16784, value: 50_000, qty: 3),
            Item(id: 22522, name: "MGP Bronze Card", value: 5_000, qty: 2),
        ]);

        Assert.Equal(160_000, total);
    }

    [Fact]
    public void Next_SkipsWhatTheGameRefused()
    {
        var bags = new[]
        {
            Item(id: 16784, value: 50_000),
            Item(id: 22522, name: "MGP Bronze Card", value: 5_000),
        };

        Assert.Equal(22522u, ConsumablePolicy.Next(bags, new HashSet<uint> { 16784 })?.ItemId);
        Assert.Null(ConsumablePolicy.Next(bags, new HashSet<uint> { 16784, 22522 }));
    }

    [Fact]
    public void Summary_SaysWhatARunIsWorth()
    {
        var bags = new[] { Item(qty: 3, value: 50_000) };
        var usable = ConsumablePolicy.Usable(bags);

        Assert.Equal("3 items to use — 150,000 Gold Saucer points",
            ConsumablePolicy.Summarize(usable, ConsumablePolicy.TotalValue(bags)));
    }

    [Fact]
    public void EmptyBags_SayNothingToDo()
    {
        Assert.Equal("nothing to use in the bags", ConsumablePolicy.Summarize([], 0));
    }
}
