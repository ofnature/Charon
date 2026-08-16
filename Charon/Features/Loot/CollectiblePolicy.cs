using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Loot;

/// <summary>
/// One bag item that the game treats as an unlockable collectible — a mount, minion, Triple Triad
/// card, orchestrion roll, emote, hairstyle and so on.
///
/// <paramref name="Category"/> is the item's own UI category name. It classifies mounts, minions,
/// Triple Triad cards and orchestrion rolls cleanly, but emotes and hairstyles BOTH read as
/// "Miscellany" (verified against the sheet), so it cannot separate those two. Distinct categories
/// are logged from live bags so per-type filtering can be built on observed values rather than
/// assumed ones.
/// </summary>
public sealed record CollectibleItem(
    uint ItemId,
    string Name,
    string Category,
    uint ActionKind,
    bool Unlocked,
    int Container,
    short Slot);

/// <summary>
/// Picks out collectibles sitting unlearned in the bags. Pure logic — no Dalamud types.
///
/// These accumulate without any looting involved: MSQ rewards, trust runs and AutoDuty runs hand
/// items straight over, so an unattended toon can sit on a pile of unlearned minions and cards for
/// weeks. Learning is left to an explicit per-item click; nothing here consumes anything.
///
/// Duplicates never appear, because the game refuses to relearn something already unlocked — which
/// is also why no "don't consume something sellable" guard is needed: an item that would be worth
/// selling is one you already own, and it is filtered out by <see cref="CollectibleItem.Unlocked"/>.
/// </summary>
public static class CollectiblePolicy
{
    /// <summary>
    /// Unlearned collectibles, in a stable display order. Only KNOWN collectible kinds qualify —
    /// unlock state alone is not enough, because the game reports an ordinary Potion as "not
    /// unlocked" too (see <see cref="CollectibleKinds"/>).
    /// </summary>
    public static List<CollectibleItem> Unlearned(IEnumerable<CollectibleItem> bagItems) =>
        bagItems
            .Where(i => i.ItemId != 0 && !i.Unlocked && CollectibleKinds.Known.Contains(i.ActionKind))
            .OrderBy(i => i.Category, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, System.StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Container)
            .ThenBy(i => i.Slot)
            .ToList();

    /// <summary>
    /// The next item the auto-collect toggle should consume, or null. Only kinds that are safe
    /// to learn unprompted (see <see cref="CollectibleKinds.ManualOnly"/>) and usable HERE —
    /// a shard outside the Occult Crescent is skipped, not blocked on. Deterministic order (the
    /// display order), so the same bags always yield the same next pick.
    /// </summary>
    public static CollectibleItem? NextAutoCollect(IEnumerable<CollectibleItem> bagItems, uint territoryId) =>
        Unlearned(bagItems).FirstOrDefault(i =>
            CollectibleKinds.IsAutoCollectSafe(i.ActionKind)
            && CollectibleKinds.CanCollectHere(i.ActionKind, territoryId));

    /// <summary>
    /// Whether this item can actually be collected in the given territory. Zone-restricted kinds
    /// (phantom job shards) are still LISTED elsewhere so you can see you are holding one — only the
    /// button is withheld, because a button that fails everywhere but one map is worse than none.
    /// </summary>
    public static bool CanCollectHere(CollectibleItem item, uint territoryId) =>
        CollectibleKinds.CanCollectHere(item.ActionKind, territoryId);

    /// <summary>
    /// Bag items whose ItemAction kind is not recognised, for the diagnostic log. Mounts, Triple
    /// Triad cards and emotes are expected here until their Action values are observed and added to
    /// <see cref="CollectibleKinds.Known"/> — that is how the allowlist grows safely.
    /// </summary>
    public static List<CollectibleItem> UnknownKinds(IEnumerable<CollectibleItem> bagItems) =>
        bagItems
            .Where(i => i.ItemId != 0 && !CollectibleKinds.Known.Contains(i.ActionKind))
            .GroupBy(i => i.ActionKind)
            .Select(g => g.First())
            .OrderBy(i => i.ActionKind)
            .ToList();
}
