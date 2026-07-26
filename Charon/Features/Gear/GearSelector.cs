using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Gear;

/// <summary>
/// Equipment slot, valued as the game's EquippedItems container index (verified layout) so the
/// adapter can cast straight to a slot number. SoulCrystal is deliberately absent — Charon never
/// swaps job stones.
/// </summary>
public enum GearSlot
{
    MainHand = 0,
    OffHand = 1,
    Head = 2,
    Body = 3,
    Hands = 4,
    Waist = 5,
    Legs = 6,
    Feet = 7,
    Ears = 8,
    Neck = 9,
    Wrists = 10,
    RingRight = 11,
    RingLeft = 12,
}

/// <summary>
/// One piece of gear, either currently worn or a candidate sitting in a bag/armoury container.
/// <paramref name="Container"/>/<paramref name="SourceSlot"/> are -1 for worn items.
///
/// The sheet lookups that need Lumina (job category, equip-slot category, stat weighting) are
/// resolved by the adapter and arrive here already reduced to <see cref="FitsJob"/>,
/// <see cref="Slot"/> and <see cref="StatScore"/> — that keeps selection pure and testable.
/// </summary>
public sealed record GearItem(
    uint ItemId,
    string Name,
    GearSlot Slot,
    int ItemLevel,
    int EquipLevel,
    bool FitsJob,
    bool IsUnique,
    bool BlocksOffHand,
    int StatScore = 0,
    int Container = -1,
    short SourceSlot = -1);

/// <summary>One planned equip: put <paramref name="Item"/> into <paramref name="Slot"/>.</summary>
public sealed record GearUpgrade(GearSlot Slot, GearItem Item, GearItem? Replacing)
{
    /// <summary>Item levels gained (the whole ilvl when filling an empty slot).</summary>
    public int IlvlGain => Item.ItemLevel - (Replacing?.ItemLevel ?? 0);
}

/// <summary>
/// Picks the best available gear per slot for the current job. Pure logic — no Dalamud types.
///
/// Ranking is item level descending, then a job-weighted stat score, then item id (so two boxes
/// running the same inventory always agree). An EMPTY slot is always an upgrade; an equal-ilvl
/// item never is (no churn for a sidegrade).
///
/// The executor equips ONE upgrade per pass and re-plans afterwards, so this returning a whole
/// list is a preview convenience — nothing downstream replays a stale batch.
/// </summary>
public static class GearSelector
{
    /// <summary>Slots considered, in equip order. Rings come last so the pair resolves together.</summary>
    private static readonly GearSlot[] SingleSlots =
    [
        GearSlot.MainHand, GearSlot.OffHand, GearSlot.Head, GearSlot.Body, GearSlot.Hands,
        GearSlot.Waist, GearSlot.Legs, GearSlot.Feet, GearSlot.Ears, GearSlot.Neck, GearSlot.Wrists,
    ];

    public static bool IsRing(GearSlot slot) => slot is GearSlot.RingRight or GearSlot.RingLeft;

    /// <summary>
    /// Every upgrade available right now. <paramref name="equipped"/> maps a slot to the worn item
    /// (absent/null = empty slot); <paramref name="candidates"/> are bag + armoury stacks. Ring
    /// candidates may use either ring slot value — both are treated as "a ring".
    /// </summary>
    public static List<GearUpgrade> Plan(
        IReadOnlyDictionary<GearSlot, GearItem?> equipped,
        IEnumerable<GearItem> candidates,
        int jobLevel)
    {
        var eligible = candidates
            .Where(c => c.FitsJob && c.EquipLevel <= jobLevel && c.ItemId != 0)
            .ToList();

        var upgrades = new List<GearUpgrade>();

        foreach (var slot in SingleSlots)
        {
            // A two-handed weapon owns the offhand slot — anything we put there would be unequipped
            // again. Judge by the mainhand we are ABOUT to wear, falling back to the worn one.
            if (slot == GearSlot.OffHand)
            {
                var mainHand = upgrades.FirstOrDefault(u => u.Slot == GearSlot.MainHand)?.Item
                               ?? Worn(equipped, GearSlot.MainHand);
                if (mainHand?.BlocksOffHand == true)
                    continue;
            }

            var worn = Worn(equipped, slot);
            var best = eligible.Where(c => c.Slot == slot).OrderBy(c => c, Ranking).FirstOrDefault();
            if (best != null && Beats(best, worn))
                upgrades.Add(new GearUpgrade(slot, best, worn));
        }

        upgrades.AddRange(PlanRings(equipped, eligible));
        return upgrades;
    }

    /// <summary>
    /// Rings are one pool feeding two slots: fill the WORSE ring slot first with the best ring, then
    /// the other slot with the best of what's left. A unique ring can only be worn once, so once it
    /// is spoken for (or already on the other hand) every stack of it leaves the pool.
    /// </summary>
    private static List<GearUpgrade> PlanRings(
        IReadOnlyDictionary<GearSlot, GearItem?> equipped,
        List<GearItem> eligible)
    {
        var upgrades = new List<GearUpgrade>();
        var pool = eligible.Where(c => IsRing(c.Slot)).OrderBy(c => c, Ranking).ToList();

        // A unique ring already on one hand can never be doubled up on the other.
        var wornUnique = new HashSet<uint>();
        foreach (var slot in new[] { GearSlot.RingRight, GearSlot.RingLeft })
        {
            var worn = Worn(equipped, slot);
            if (worn is { IsUnique: true })
                wornUnique.Add(worn.ItemId);
        }

        // Worst hand first — that is where the best ring belongs.
        var targets = new[] { GearSlot.RingRight, GearSlot.RingLeft }
            .OrderBy(s => Worn(equipped, s)?.ItemLevel ?? 0)
            .ToList();

        foreach (var slot in targets)
        {
            var worn = Worn(equipped, slot);
            var best = pool.FirstOrDefault(c => !wornUnique.Contains(c.ItemId));
            if (best == null || !Beats(best, worn))
                continue;

            upgrades.Add(new GearUpgrade(slot, best, worn));

            // This exact stack is spoken for; a unique ring takes every copy of itself with it.
            pool.Remove(best);
            if (best.IsUnique)
            {
                wornUnique.Add(best.ItemId);
                pool.RemoveAll(c => c.ItemId == best.ItemId);
            }
        }

        return upgrades;
    }

    /// <summary>Empty slot = always an upgrade; equal ilvl = never (a sidegrade is not worth a swap).</summary>
    private static bool Beats(GearItem candidate, GearItem? worn) =>
        worn == null || candidate.ItemLevel > worn.ItemLevel;

    private static GearItem? Worn(IReadOnlyDictionary<GearSlot, GearItem?> equipped, GearSlot slot) =>
        equipped.TryGetValue(slot, out var item) ? item : null;

    /// <summary>ilvl, then job-weighted stats, then item id — total and deterministic across boxes.</summary>
    private static readonly IComparer<GearItem> Ranking = Comparer<GearItem>.Create((a, b) =>
    {
        var byIlvl = b.ItemLevel.CompareTo(a.ItemLevel);
        if (byIlvl != 0)
            return byIlvl;

        var byStats = b.StatScore.CompareTo(a.StatScore);
        return byStats != 0 ? byStats : a.ItemId.CompareTo(b.ItemId);
    });
}
