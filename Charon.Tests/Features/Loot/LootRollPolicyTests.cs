using Charon.Features.Loot;

namespace Charon.Tests.Features.Loot;

public sealed class LootRollPolicyTests
{
    private static LootItem Item(
        bool isCollectible = false, bool alreadyUnlocked = false, bool isTradeable = true,
        bool isGear = false, bool canEquip = true, bool isUpgrade = false,
        bool worseThanEquipped = false, int ilvlBelow = 0, bool isGlamour = false,
        bool weeklyLockout = false, string equipBlocker = "") =>
        new(1, "thing", isCollectible, alreadyUnlocked, isTradeable, isGear, canEquip, isUpgrade,
            worseThanEquipped, ilvlBelow, isGlamour, weeklyLockout, equipBlocker);

    private static LootContext Context(
        bool enabled = true, bool canSell = true, bool strangers = false, int passBelowGap = 30) =>
        new(enabled, canSell, strangers, passBelowGap);

    private static RollAction Roll(LootItem item, LootContext? context = null) =>
        LootRollPolicy.Evaluate(item, context ?? Context()).Action;

    // --- Collectibles ---

    [Fact]
    public void UnownedCollectible_Needs()
    {
        Assert.Equal(RollAction.Need, Roll(Item(isCollectible: true)));
    }

    [Fact]
    public void OwnedTradeableCollectible_GreedsOnAPaidAccount()
    {
        // Worth gil — a paid toon can sell the duplicate.
        var action = Roll(Item(isCollectible: true, alreadyUnlocked: true), Context(canSell: true));
        Assert.Equal(RollAction.Greed, action);
    }

    [Fact]
    public void OwnedTradeableCollectible_PassesOnFreeTrial()
    {
        // A free trial toon cannot trade or use the market board, so a duplicate is dead weight.
        var action = Roll(Item(isCollectible: true, alreadyUnlocked: true), Context(canSell: false));
        Assert.Equal(RollAction.Pass, action);
    }

    [Fact]
    public void OwnedUntradeableCollectible_PassesEvenWhenSellingIsPossible()
    {
        var action = Roll(Item(isCollectible: true, alreadyUnlocked: true, isTradeable: false),
            Context(canSell: true));
        Assert.Equal(RollAction.Pass, action);
    }

    // --- Gear ---

    [Fact]
    public void UpgradeForCurrentJob_Needs()
    {
        Assert.Equal(RollAction.Need, Roll(Item(isGear: true, isUpgrade: true)));
    }

    [Fact]
    public void UnequippableGear_Passes()
    {
        Assert.Equal(RollAction.Pass, Roll(Item(isGear: true, canEquip: false)));
    }

    [Fact]
    public void UnequippableGear_SaysWhichGateRefusedIt()
    {
        // "this job can't wear it" covers four different gates, and one of them is just being too
        // low level — which reads as a bug on a Lv100 drop that the toon will wear eventually.
        var d = LootRollPolicy.Evaluate(
            Item(isGear: true, canEquip: false, equipBlocker: "needs level 100 (this job is 90)"),
            Context());

        Assert.Equal(RollAction.Pass, d.Action);
        Assert.Equal("needs level 100 (this job is 90)", d.Reason);
    }

    [Fact]
    public void UnequippableGear_WithNoBlockerGiven_StillExplainsItself()
    {
        var d = LootRollPolicy.Evaluate(Item(isGear: true, canEquip: false), Context());
        Assert.NotEmpty(d.Reason);
    }

    [Fact]
    public void GearWorseThanEquipped_Passes()
    {
        Assert.Equal(RollAction.Pass, Roll(Item(isGear: true, worseThanEquipped: true)));
    }

    [Fact]
    public void GearFarBelowCurrentItemLevel_Passes()
    {
        Assert.Equal(RollAction.Pass, Roll(Item(isGear: true, ilvlBelow: 45)));
    }

    [Fact]
    public void GearJustInsideTheGap_DoesNotPassOnThatRule()
    {
        // The gap is a threshold, not a range — 30 below with a gap of 30 is not "more than".
        Assert.Equal(RollAction.Greed, Roll(Item(isGear: true, ilvlBelow: 30)));
    }

    [Fact]
    public void SidegradeGear_Greeds()
    {
        Assert.Equal(RollAction.Greed, Roll(Item(isGear: true)));
    }

    // --- Ordering: first match wins ---

    [Fact]
    public void CantEquip_BeatsUpgrade()
    {
        // Contradictory inputs shouldn't be possible, but the order must still be deterministic.
        var action = Roll(Item(isGear: true, canEquip: false, isUpgrade: true));
        Assert.Equal(RollAction.Pass, action);
    }

    [Fact]
    public void UpgradeForThisJob_BeatsTheItemLevelGap()
    {
        Assert.Equal(RollAction.Need, Roll(Item(isGear: true, isUpgrade: true, ilvlBelow: 500)));
    }

    [Fact]
    public void CollectibleRules_BeatGearRules()
    {
        var action = Roll(Item(isCollectible: true, isGear: true, canEquip: false));
        Assert.Equal(RollAction.Need, action);
    }

    // --- Weekly lockout and the master switch ---

    [Fact]
    public void WeeklyLockout_IsNeverAnswered()
    {
        var d = LootRollPolicy.Evaluate(Item(weeklyLockout: true, isCollectible: true), Context());
        Assert.Equal(RollAction.DontRoll, d.Action);
        Assert.Contains("lockout", d.Reason);
    }

    [Fact]
    public void Disabled_NeverAnswers()
    {
        Assert.Equal(RollAction.DontRoll, Roll(Item(isCollectible: true), Context(enabled: false)));
    }

    // --- Manners: never Need in front of strangers ---

    [Fact]
    public void StrangersPresent_DowngradeNeedToGreed()
    {
        var d = LootRollPolicy.Evaluate(Item(isCollectible: true), Context(strangers: true));
        Assert.Equal(RollAction.Greed, d.Action);
        Assert.Contains("non-fleet", d.Reason);
    }

    [Fact]
    public void StrangersPresent_DoNotTurnAPassIntoAGreed()
    {
        // The stranger rule only ever downgrades — it must never raise a roll.
        var action = Roll(Item(isGear: true, canEquip: false), Context(strangers: true));
        Assert.Equal(RollAction.Pass, action);
    }

    [Fact]
    public void StrangersPresent_LeaveGreedAlone()
    {
        Assert.Equal(RollAction.Greed, Roll(Item(isGear: true), Context(strangers: true)));
    }

    [Fact]
    public void StrangersPresent_StillDontRollLockouts()
    {
        Assert.Equal(RollAction.DontRoll,
            Roll(Item(weeklyLockout: true, isCollectible: true), Context(strangers: true)));
    }

    // --- Every outcome explains itself (these reasons surface in the Debug line) ---

    [Fact]
    public void EveryDecisionCarriesAReason()
    {
        LootItem[] cases =
        [
            Item(isCollectible: true),
            Item(isCollectible: true, alreadyUnlocked: true),
            Item(isGear: true, canEquip: false),
            Item(isGear: true, isUpgrade: true),
            Item(isGear: true, worseThanEquipped: true),
            Item(isGear: true, ilvlBelow: 99),
            Item(isGlamour: true),
            Item(),
            Item(weeklyLockout: true),
        ];

        Assert.All(cases, c => Assert.NotEmpty(LootRollPolicy.Evaluate(c, Context()).Reason));
    }
}
