namespace Charon.Features.Loot;

/// <summary>What to do with one loot item.</summary>
public enum RollAction
{
    /// <summary>Leave it entirely — never answer the window for this item.</summary>
    DontRoll,

    Pass,
    Greed,
    Need,
}

public sealed record RollDecision(RollAction Action, string Reason);

/// <summary>
/// One item on the loot window, with everything the rules need already resolved by the adapter.
/// Sheet lookups and inventory comparisons belong there; this record is deliberately plain data so
/// the rules stay pure and testable.
/// </summary>
public sealed record LootItem(
    uint ItemId,
    string Name,
    bool IsCollectible,
    bool AlreadyUnlocked,
    bool IsTradeable,
    bool IsGear,
    bool CanEquip,
    bool IsUpgradeForCurrentJob,
    bool WorseThanEquipped,
    int ItemLevelsBelowCurrent,
    bool IsGlamour,
    bool HasWeeklyLockout);

/// <summary>
/// Rolling context for this character and party.
/// </summary>
public sealed record LootContext(
    bool Enabled,
    bool CanSell,
    bool StrangersInParty,
    int PassBelowIlvlGap);

/// <summary>
/// Decides Need / Greed / Pass for a loot item. Pure logic — no Dalamud types.
///
/// The rules are ordered and the FIRST MATCH WINS, which is the whole point: a pile of independent
/// toggles (the shape most loot plugins take) makes it impossible to predict what a given item will
/// do, which is why they end up needing babysitting. Read down the list and the first matching row
/// is the answer.
///
/// They are also written to be RIGHT IN EVERY SITUATION, so there is nothing to switch on and off
/// between a farm run and a real party — the failure mode of the plugin this replaces was forgetting
/// its state, not lacking options.
///
/// Two contextual facts do the switching that a human would otherwise do by hand:
/// - <see cref="LootContext.CanSell"/> — a duplicate collectible is gil to a paid account and dead
///   weight to a free trial one, which cannot trade or use the market board at all.
/// - <see cref="LootContext.StrangersInParty"/> — Need is downgraded to Greed when anyone outside
///   the fleet is present. Eight bots hitting Need on someone else's drop is how you get reported.
/// </summary>
public static class LootRollPolicy
{
    public static RollDecision Evaluate(LootItem item, LootContext context)
    {
        if (!context.Enabled)
            return new RollDecision(RollAction.DontRoll, "auto-roll disabled");

        // Weekly lockout gear is contested and often personal — never answer for it at all.
        if (item.HasWeeklyLockout)
            return new RollDecision(RollAction.DontRoll, "weekly lockout — left for you");

        var decision = Classify(item, context);

        // Downgrade, never upgrade: a stranger present can turn Need into Greed but nothing here
        // ever raises a roll.
        if (decision.Action == RollAction.Need && context.StrangersInParty)
            return new RollDecision(RollAction.Greed, $"{decision.Reason} — Greed only, non-fleet players present");

        return decision;
    }

    private static RollDecision Classify(LootItem item, LootContext context)
    {
        if (item.IsCollectible)
        {
            if (!item.AlreadyUnlocked)
                return new RollDecision(RollAction.Need, "collectible I don't own yet");

            // Owned already. Worth gil to an account that can sell it, worth nothing otherwise.
            return item is { IsTradeable: true } && context.CanSell
                ? new RollDecision(RollAction.Greed, "already unlocked — sellable")
                : new RollDecision(RollAction.Pass, "already unlocked — can't sell it");
        }

        if (item.IsGear)
        {
            if (!item.CanEquip)
                return new RollDecision(RollAction.Pass, "this job can't wear it");

            if (item.IsUpgradeForCurrentJob)
                return new RollDecision(RollAction.Need, "upgrade for this job");

            if (item.WorseThanEquipped)
                return new RollDecision(RollAction.Pass, "worse than what's equipped");

            if (item.ItemLevelsBelowCurrent > context.PassBelowIlvlGap)
                return new RollDecision(RollAction.Pass,
                    $"{item.ItemLevelsBelowCurrent} item levels below this job");
        }

        if (item.IsGlamour)
            return new RollDecision(RollAction.Greed, "glamour");

        return new RollDecision(RollAction.Greed, "everything else");
    }
}
