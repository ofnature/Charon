using Charon.Features.Gear;

namespace Charon.Tests.Features.Gear;

public sealed class GearSelectorTests
{
    private const int JobLevel = 90;

    private static GearItem Item(
        uint id, GearSlot slot, int ilvl, int equipLevel = 1,
        bool fitsJob = true, bool unique = false, bool blocksOffHand = false, int stats = 0) =>
        new(id, $"item {id}", slot, ilvl, equipLevel, fitsJob, unique, blocksOffHand, stats, Container: 1000, SourceSlot: 0);

    private static Dictionary<GearSlot, GearItem?> Equipped(params GearItem[] worn) =>
        worn.ToDictionary(w => w.Slot, w => (GearItem?)w);

    // --- Basic upgrade detection ---

    [Fact]
    public void HigherIlvl_IsAnUpgrade()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Head, 100)),
            [Item(2, GearSlot.Head, 130)],
            JobLevel);

        var upgrade = Assert.Single(upgrades);
        Assert.Equal(GearSlot.Head, upgrade.Slot);
        Assert.Equal(2u, upgrade.Item.ItemId);
        Assert.Equal(30, upgrade.IlvlGain);
    }

    [Fact]
    public void LowerOrEqualIlvl_IsNotAnUpgrade()
    {
        var equipped = Equipped(Item(1, GearSlot.Head, 130));

        Assert.Empty(GearSelector.Plan(equipped, [Item(2, GearSlot.Head, 100)], JobLevel));
        Assert.Empty(GearSelector.Plan(equipped, [Item(2, GearSlot.Head, 130)], JobLevel)); // sidegrade
    }

    [Fact]
    public void EmptySlot_IsAlwaysAnUpgrade()
    {
        var upgrades = GearSelector.Plan(
            new Dictionary<GearSlot, GearItem?>(),
            [Item(2, GearSlot.Body, 15)],
            JobLevel);

        var upgrade = Assert.Single(upgrades);
        Assert.Null(upgrade.Replacing);
        Assert.Equal(15, upgrade.IlvlGain);
    }

    [Fact]
    public void ExplicitlyNullSlot_CountsAsEmpty()
    {
        var equipped = new Dictionary<GearSlot, GearItem?> { [GearSlot.Body] = null };

        Assert.Single(GearSelector.Plan(equipped, [Item(2, GearSlot.Body, 15)], JobLevel));
    }

    // --- Filters ---

    [Fact]
    public void AboveJobLevel_IsFilteredOut()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Head, 100)),
            [Item(2, GearSlot.Head, 130, equipLevel: 91)],
            jobLevel: 90);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void ExactlyAtJobLevel_IsAllowed()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Head, 100)),
            [Item(2, GearSlot.Head, 130, equipLevel: 90)],
            jobLevel: 90);

        Assert.Single(upgrades);
    }

    [Fact]
    public void WrongJob_IsFilteredOut()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Head, 100)),
            [Item(2, GearSlot.Head, 300, fitsJob: false)],
            JobLevel);

        Assert.Empty(upgrades);
    }

    // --- Ranking ---

    [Fact]
    public void PicksHighestIlvlAmongCandidates()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Legs, 100)),
            [Item(2, GearSlot.Legs, 120), Item(3, GearSlot.Legs, 150), Item(4, GearSlot.Legs, 130)],
            JobLevel);

        Assert.Equal(3u, Assert.Single(upgrades).Item.ItemId);
    }

    [Fact]
    public void SameIlvl_BreaksTieOnStatScore()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Neck, 100)),
            [Item(2, GearSlot.Neck, 130, stats: 40), Item(3, GearSlot.Neck, 130, stats: 90)],
            JobLevel);

        Assert.Equal(3u, Assert.Single(upgrades).Item.ItemId);
    }

    [Fact]
    public void SameIlvlAndStats_BreaksTieOnItemId_SoEveryBoxAgrees()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Neck, 100)),
            [Item(9, GearSlot.Neck, 130), Item(4, GearSlot.Neck, 130)],
            JobLevel);

        Assert.Equal(4u, Assert.Single(upgrades).Item.ItemId);
    }

    // --- Two-handers ---

    [Fact]
    public void TwoHandedMainhandUpgrade_SkipsTheOffhandSlot()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.MainHand, 100), Item(2, GearSlot.OffHand, 100)),
            [
                Item(3, GearSlot.MainHand, 150, blocksOffHand: true),
                Item(4, GearSlot.OffHand, 150),
            ],
            JobLevel);

        var upgrade = Assert.Single(upgrades);
        Assert.Equal(GearSlot.MainHand, upgrade.Slot);
    }

    [Fact]
    public void TwoHandedWeaponAlreadyWorn_SkipsTheOffhandSlot()
    {
        // No mainhand upgrade available — the worn weapon still owns the offhand.
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.MainHand, 200, blocksOffHand: true)),
            [Item(4, GearSlot.OffHand, 150)],
            JobLevel);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void OneHandedMainhand_StillAllowsAnOffhandUpgrade()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.MainHand, 100), Item(2, GearSlot.OffHand, 100)),
            [Item(3, GearSlot.MainHand, 150), Item(4, GearSlot.OffHand, 150)],
            JobLevel);

        Assert.Equal(2, upgrades.Count);
        Assert.Contains(upgrades, u => u.Slot == GearSlot.OffHand);
    }

    // --- Rings ---

    [Fact]
    public void TwoRingCandidates_FillBothHands_BestIntoTheWorseHand()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.RingRight, 120), Item(2, GearSlot.RingLeft, 100)),
            [Item(3, GearSlot.RingRight, 150), Item(4, GearSlot.RingRight, 140)],
            JobLevel);

        Assert.Equal(2, upgrades.Count);
        Assert.Equal(3u, upgrades.Single(u => u.Slot == GearSlot.RingLeft).Item.ItemId);  // worst hand, best ring
        Assert.Equal(4u, upgrades.Single(u => u.Slot == GearSlot.RingRight).Item.ItemId);
    }

    [Fact]
    public void OneRingCandidate_OnlyReplacesTheWorseHand()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.RingRight, 120), Item(2, GearSlot.RingLeft, 100)),
            [Item(3, GearSlot.RingRight, 150)],
            JobLevel);

        var upgrade = Assert.Single(upgrades);
        Assert.Equal(GearSlot.RingLeft, upgrade.Slot);
    }

    [Fact]
    public void UniqueRing_IsNeverEquippedTwice()
    {
        // Two stacks of the same unique ring: only one hand may wear it.
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.RingRight, 100), Item(2, GearSlot.RingLeft, 100)),
            [
                new GearItem(3, "unique", GearSlot.RingRight, 150, 1, true, IsUnique: true, false, 0, 1000, 0),
                new GearItem(3, "unique", GearSlot.RingRight, 150, 1, true, IsUnique: true, false, 0, 1000, 1),
            ],
            JobLevel);

        Assert.Single(upgrades);
    }

    [Fact]
    public void UniqueRingAlreadyWorn_IsNotDoubledOnTheOtherHand()
    {
        var worn = new GearItem(3, "unique", GearSlot.RingRight, 150, 1, true, IsUnique: true, false);
        var equipped = new Dictionary<GearSlot, GearItem?>
        {
            [GearSlot.RingRight] = worn,
            [GearSlot.RingLeft] = Item(2, GearSlot.RingLeft, 100),
        };

        var upgrades = GearSelector.Plan(
            equipped,
            [new GearItem(3, "unique", GearSlot.RingRight, 150, 1, true, IsUnique: true, false, 0, 1000, 0)],
            JobLevel);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void NonUniqueRing_MayBeWornOnBothHands()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.RingRight, 100), Item(2, GearSlot.RingLeft, 100)),
            [
                Item(3, GearSlot.RingRight, 150) with { SourceSlot = 0 },
                Item(3, GearSlot.RingRight, 150) with { SourceSlot = 1 },
            ],
            JobLevel);

        Assert.Equal(2, upgrades.Count);
        Assert.All(upgrades, u => Assert.Equal(3u, u.Item.ItemId));
    }

    [Fact]
    public void RingCandidateInEitherSlotValue_IsTreatedAsARing()
    {
        // The adapter may label a ring stack RingLeft; it is still just "a ring".
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.RingRight, 100), Item(2, GearSlot.RingLeft, 100)),
            [Item(3, GearSlot.RingLeft, 150)],
            JobLevel);

        Assert.Single(upgrades);
    }

    // --- Whole-kit sanity ---

    [Fact]
    public void OneUpgradePerSlot_NeverTwo()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Body, 100)),
            [Item(2, GearSlot.Body, 150), Item(3, GearSlot.Body, 140)],
            JobLevel);

        Assert.Single(upgrades);
    }

    [Fact]
    public void NothingAvailable_PlansNothing()
    {
        Assert.Empty(GearSelector.Plan(Equipped(Item(1, GearSlot.Head, 100)), [], JobLevel));
    }
}
