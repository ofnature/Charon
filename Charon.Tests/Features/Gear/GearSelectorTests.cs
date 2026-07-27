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
    public void LowerIlvl_IsNotAnUpgrade()
    {
        var equipped = Equipped(Item(1, GearSlot.Head, 130));

        Assert.Empty(GearSelector.Plan(equipped, [Item(2, GearSlot.Head, 100)], JobLevel));
    }

    [Fact]
    public void SameIlvlSameStats_IsNotAnUpgrade()
    {
        var equipped = Equipped(Item(1, GearSlot.Head, 130, stats: 100));

        Assert.Empty(GearSelector.Plan(equipped, [Item(2, GearSlot.Head, 130, stats: 100)], JobLevel));
    }

    // --- At-cap behaviour: same ilvl, better stats ---

    [Fact]
    public void SameIlvl_ClearlyBetterStats_IsAnUpgrade()
    {
        // The max-level case: every candidate is the same ilvl, so stats have to decide or the
        // feature goes dead at cap.
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Head, 730, stats: 100)),
            [Item(2, GearSlot.Head, 730, stats: 140)],
            JobLevel);

        var upgrade = Assert.Single(upgrades);
        Assert.Equal(2u, upgrade.Item.ItemId);
        Assert.Equal(0, upgrade.IlvlGain); // a pure stat swap
    }

    [Fact]
    public void SameIlvl_MarginallyBetterStats_IsNotWorthASwap()
    {
        // Inside the 5% margin — swapping here would just churn.
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Head, 730, stats: 100)),
            [Item(2, GearSlot.Head, 730, stats: 103)],
            JobLevel);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void SameIlvl_WorseStats_IsNotAnUpgrade()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Head, 730, stats: 140)),
            [Item(2, GearSlot.Head, 730, stats: 100)],
            JobLevel);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void LowerIlvl_IsNeverAnUpgrade_HoweverGoodTheStats()
    {
        // ilvl gates duty entry — trading it away for substats is a real downgrade.
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Head, 730, stats: 10)),
            [Item(2, GearSlot.Head, 725, stats: 9999)],
            JobLevel);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void StatSwap_DoesNotBounceBack()
    {
        // After the swap the old piece sits in the armoury. It must not read as an upgrade over
        // the piece that just replaced it, or the executor would swap them forever.
        var swappedIn = Item(2, GearSlot.Head, 730, stats: 140);
        var swappedOut = Item(1, GearSlot.Head, 730, stats: 100);

        Assert.Empty(GearSelector.Plan(Equipped(swappedIn), [swappedOut], JobLevel));
    }

    [Fact]
    public void UnresolvableStats_NeverChurn()
    {
        // Both score 0 (sheet lookup failed) — equal ilvl must then mean "leave it alone".
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Head, 730)),
            [Item(2, GearSlot.Head, 730)],
            JobLevel);

        Assert.Empty(upgrades);
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

    [Fact]
    public void GatheringGear_IsNeverEquippedOnACombatJob()
    {
        // Gathering/crafting gear sits in the "All Classes" category, so the game DOES let a
        // combat job wear it — it passes fitsJob. Only the stat gate stops it, and without that
        // its higher item level wins outright.
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Body, 100)),
            [Item(2, GearSlot.Body, 300, fitsJob: true) with { StatsFitJob = false }],
            JobLevel);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void GatheringGear_LosesEvenWhenTheSlotIsEmpty()
    {
        // An empty slot is otherwise always an upgrade — wrong-stat gear must still not fill it.
        var upgrades = GearSelector.Plan(
            new Dictionary<GearSlot, GearItem?>(),
            [Item(2, GearSlot.Ears, 300) with { StatsFitJob = false }],
            JobLevel);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void CombatGear_IsStillChosenOverHigherIlvlGatheringGear()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Neck, 100)),
            [
                Item(2, GearSlot.Neck, 300) with { StatsFitJob = false }, // higher ilvl, wrong stats
                Item(3, GearSlot.Neck, 150),                              // the right answer
            ],
            JobLevel);

        Assert.Equal(3u, Assert.Single(upgrades).Item.ItemId);
    }

    // --- Race/sex restricted gear ---

    [Fact]
    public void RaceRestrictedGear_IsNeverProposed()
    {
        // "Roegadyn Bodice" is female-Roegadyn only, yet reads as All Classes at equip level 1 —
        // on anyone else the game silently refuses the equip.
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Body, 1)),
            [Item(2, GearSlot.Body, 5) with { FitsRace = false }],
            JobLevel);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void RaceRestrictedGear_DoesNotFillAnEmptySlotEither()
    {
        // The reported case exactly: a fresh alt with bare slots, where anything otherwise wins.
        var upgrades = GearSelector.Plan(
            new Dictionary<GearSlot, GearItem?>(),
            [Item(2, GearSlot.Body, 5) with { FitsRace = false }],
            JobLevel);

        Assert.Empty(upgrades);
    }

    [Fact]
    public void WearableGear_IsStillChosenAlongsideRestrictedGear()
    {
        var upgrades = GearSelector.Plan(
            new Dictionary<GearSlot, GearItem?>(),
            [
                Item(2, GearSlot.Body, 15) with { FitsRace = false }, // higher ilvl, unwearable
                Item(3, GearSlot.Body, 5),
            ],
            JobLevel);

        Assert.Equal(3u, Assert.Single(upgrades).Item.ItemId);
    }

    // --- Wrong main stat (the "All Classes" accessory trap) ---

    [Fact]
    public void RightMainStat_BeatsHigherIlvlWithTheWrongOne()
    {
        // Augmented Shire Conservator's Choker is ilvl 270 with Dexterity — the game lets a
        // Paladin wear it, but the main stat is dead weight, so a lower-ilvl STR piece wins.
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Neck, 100)),
            [
                Item(2, GearSlot.Neck, 270) with { HasJobMainStat = false },
                Item(3, GearSlot.Neck, 240),
            ],
            JobLevel);

        Assert.Equal(3u, Assert.Single(upgrades).Item.ItemId);
    }

    [Fact]
    public void WrongMainStat_StillFillsAnEmptySlot_WhenNothingElseExists()
    {
        // Some vitality beats a bare slot — off-stat gear is ranked last, not banned.
        var upgrades = GearSelector.Plan(
            new Dictionary<GearSlot, GearItem?>(),
            [Item(2, GearSlot.Neck, 270) with { HasJobMainStat = false }],
            JobLevel);

        Assert.Single(upgrades);
    }

    [Fact]
    public void WrongMainStat_LosesEvenAtAMuchHigherIlvl()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.Body, 100)),
            [
                Item(2, GearSlot.Body, 600) with { HasJobMainStat = false },
                Item(3, GearSlot.Body, 130),
            ],
            JobLevel);

        Assert.Equal(3u, Assert.Single(upgrades).Item.ItemId);
    }

    [Fact]
    public void RightMainStat_PreferredForRingsToo()
    {
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.RingRight, 100), Item(2, GearSlot.RingLeft, 100)),
            [
                Item(3, GearSlot.RingRight, 270) with { HasJobMainStat = false },
                Item(4, GearSlot.RingRight, 240),
            ],
            JobLevel);

        // Worst hand takes the on-stat ring first; the off-stat one still fills the other hand.
        Assert.Equal(2, upgrades.Count);
        Assert.Equal(4u, upgrades.First(u => u.Item.HasJobMainStat).Item.ItemId);
    }

    [Fact]
    public void GatheringRing_IsNotEquippedIntoEitherHand()
    {
        // Rings go through their own pool — the stat gate has to apply there too.
        var upgrades = GearSelector.Plan(
            Equipped(Item(1, GearSlot.RingRight, 100), Item(2, GearSlot.RingLeft, 100)),
            [Item(3, GearSlot.RingRight, 300) with { StatsFitJob = false }],
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
