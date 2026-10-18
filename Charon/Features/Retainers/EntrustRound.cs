using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Retainers;

/// <summary>One stack a retainer held when last read: the item key (<see cref="EntrustRound.Key"/>) and how many.</summary>
public sealed record HeldStack(uint Key, int Qty);

/// <summary>Who an entrust round would visit, and why.</summary>
/// <param name="Matches">Items in the player's bags this retainer already holds; -1 when her contents were never read.</param>
/// <param name="Items">The matching item keys (<see cref="EntrustRound.Key"/>) — empty when never read.</param>
public sealed record EntrustVisit(string Name, int Matches, IReadOnlyList<uint> Items);

/// <summary>
/// "Entrust duplicates to every retainer": who is worth opening. Pure — no Dalamud types.
///
/// The moving is the GAME'S OWN Entrust Duplicates (her bags' button, then its confirm list), which
/// only ever entrusts items she already holds a stack of — exactly the rule wanted, decided by the game.
/// This only chooses the visits: a retainer whose last snapshot shares an item with the bags, and a
/// retainer whose contents were never read (she might, and opening her also fills the snapshot). One
/// whose snapshot shares nothing is skipped — a summon that could only end in a greyed-out button.
///
/// Items are compared as KEYS, quality included (<see cref="Key"/>): an HQ stack never takes NQ units or
/// the reverse, so a retainer holding NQ ore is no match for the HQ ore in the bags. CRYSTALS are left
/// out on both sides: the first live use (2026-10-18) offered two retainers "2 matching items" each and
/// the game's button then found nothing to entrust — both sides hold shards, and the count was them.
///
/// And only what can actually go ONTO her stack counts (second live look, same day, once the tooltip named
/// the items): Thavnairian Sarouel and Frontier Shoes (StackSize 1 — gear never stacks, so there is no
/// stack to add to), and a dye (StackSize 999) the game also passed over — a full stack is the likely
/// reason, NOT confirmed. So an item counts only when it stacks (<c>StackSize</c> > 1) AND she holds a
/// stack of it with room left — a full 999 takes nothing more.
/// An unknown stack size (0, sheet unreadable) is not used to rule anything out.
///
/// Like a venture round, the next retainer is asked of live state each time, never a list decided up
/// front; a retainer is visited at most once per round.
/// </summary>
public static class EntrustRound
{
    /// <summary>The game's own HQ item-id convention: an HQ item is its id plus one million.</summary>
    public const uint HqOffset = 1_000_000;

    public static uint Key(uint itemId, bool hq) => hq ? itemId + HqOffset : itemId;

    public static uint ItemIdOf(uint key) => key % HqOffset;

    public static bool IsHq(uint key) => key >= HqOffset;

    /// <summary>Shards, crystals and clusters (item ids 2-19) — never counted as a match.</summary>
    public static bool IsCrystal(uint key) => ItemIdOf(key) is >= 2 and <= 19;

    /// <param name="retainers">This character's retainers, in list order.</param>
    /// <param name="snapshots">The stacks each retainer held when last read; a missing name means never read.</param>
    /// <param name="bagItems">Item keys in the player's bags right now.</param>
    /// <param name="stackSize">An item id's stack size from the sheet; 0 when unknown.</param>
    public static IReadOnlyList<EntrustVisit> Plan(
        IReadOnlyList<string> retainers,
        IReadOnlyDictionary<string, IReadOnlyCollection<HeldStack>> snapshots,
        IReadOnlyCollection<uint> bagItems,
        Func<uint, int> stackSize)
    {
        var bags = bagItems.Where(k => !IsCrystal(k) && stackSize(ItemIdOf(k)) != 1).ToHashSet();
        var visits = new List<EntrustVisit>();
        foreach (var name in retainers)
        {
            if (!snapshots.TryGetValue(name, out var held))
            {
                visits.Add(new EntrustVisit(name, -1, []));
                continue;
            }

            var matches = held
                .Where(s => bags.Contains(s.Key) && HasRoom(s.Qty, stackSize(ItemIdOf(s.Key))))
                .Select(s => s.Key)
                .Distinct()
                .OrderBy(k => k)
                .ToList();
            if (matches.Count > 0)
                visits.Add(new EntrustVisit(name, matches.Count, matches));
        }

        return visits;
    }

    /// <summary>A stack can take more units — or its size is unknown, which rules nothing out.</summary>
    public static bool HasRoom(int qty, int stackSize) => stackSize <= 0 || (stackSize > 1 && qty < stackSize);

    /// <summary>The next retainer to open: the first planned visit not yet made this round, or null when done.</summary>
    public static string? Next(IReadOnlyList<EntrustVisit> plan, IReadOnlyCollection<string> visited)
    {
        foreach (var visit in plan)
        {
            if (!visited.Contains(visit.Name))
                return visit.Name;
        }

        return null;
    }
}
