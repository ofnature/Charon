using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Consumables;

/// <summary>
/// One bag stack of a use-it-and-it-is-gone item. <paramref name="Value"/> is the item action's
/// Data[0] — for the MGP family, the points the card grants — and is used only to say what a run
/// would be worth; nothing is decided by it.
/// </summary>
public sealed record ConsumableItem(
    uint ItemId,
    string Name,
    uint ActionKind,
    int Quantity,
    bool Untradable,
    int Value,
    int Container,
    short Slot);

/// <summary>
/// Picks the bag items that are worth using outright. Pure logic — no Dalamud types.
///
/// TWO gates, both required. The kind must be a VERIFIED one (<see cref="ConsumableKinds.Known"/>),
/// and the item must be UNTRADABLE — which is the user's own reason for wanting this and is worth
/// encoding rather than assuming: a tradable copy of a known kind has a market value that using it
/// would destroy, so it is left alone even though the kind is allowed. An untradable one is dead
/// weight in the bags until it is used.
/// </summary>
public static class ConsumablePolicy
{
    /// <summary>Everything worth using, in a stable display order (biggest value first).</summary>
    public static List<ConsumableItem> Usable(IEnumerable<ConsumableItem> bagItems) =>
        bagItems
            .Where(i => i.ItemId != 0
                        && i.Quantity > 0
                        && i.Untradable
                        && ConsumableKinds.Known.Contains(i.ActionKind))
            .OrderByDescending(i => i.Value)
            .ThenBy(i => i.Name, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Container)
            .ThenBy(i => i.Slot)
            .ToList();

    /// <summary>
    /// The next stack to use, or null. Deterministic (the display order), so the same bags always
    /// yield the same next pick and a refused item can be skipped by the caller without reshuffling.
    /// </summary>
    public static ConsumableItem? Next(IEnumerable<ConsumableItem> bagItems, IReadOnlySet<uint> skip) =>
        Usable(bagItems).FirstOrDefault(i => !skip.Contains(i.ItemId));

    /// <summary>What a full run would be worth, summed over every usable stack.</summary>
    public static long TotalValue(IEnumerable<ConsumableItem> bagItems) =>
        Usable(bagItems).Sum(i => (long)i.Value * i.Quantity);

    /// <summary>The one-line summary for the page and the Debug line.</summary>
    public static string Summarize(IReadOnlyList<ConsumableItem> usable, long totalValue)
    {
        if (usable.Count == 0)
            return "nothing to use in the bags";

        var stacks = usable.Sum(i => i.Quantity);
        var worth = totalValue > 0 ? $" — {totalValue:N0} {ConsumableKinds.Describe(usable[0].ActionKind)}" : string.Empty;
        return $"{stacks} item{(stacks == 1 ? "" : "s")} to use{worth}";
    }
}
