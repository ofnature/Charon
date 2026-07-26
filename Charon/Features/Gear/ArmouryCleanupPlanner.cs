using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Gear;

/// <summary>One armoury stack considered for eviction.</summary>
public sealed record ArmouryItem(uint ItemId, string Name, int Container, short Slot, bool IsSoulCrystal = false);

/// <summary>
/// Plans "clear junk out of the armoury": move every armoury item that no saved gearset references
/// back into the main bags. Pure logic — no Dalamud types.
///
/// Gearsets are the main protection, so the caller must pass the full set (every existing gearset,
/// all slots). Three things are never evicted regardless:
/// - Items on the user's keep list — the per-item veto from the cleanup preview.
/// - Soul crystals — a job you have no gearset for would otherwise lose its stone.
/// - Anything at all when the gearset set is EMPTY: that reads as "gearsets not loaded yet"
///   far more often than "this character genuinely has none", and guessing wrong empties the
///   whole armoury.
/// </summary>
public static class ArmouryCleanupPlanner
{
    /// <summary>Armoury stacks to move out to the bags, in container order.</summary>
    public static List<ArmouryItem> Plan(
        IReadOnlyList<ArmouryItem> armoury,
        IReadOnlyCollection<uint> gearsetItemIds,
        IReadOnlyCollection<uint> keepItemIds)
    {
        var keep = new HashSet<uint>(keepItemIds);

        return Unregistered(armoury, gearsetItemIds)
            .Where(i => !keep.Contains(i.ItemId))
            .ToList();
    }

    /// <summary>
    /// Every armoury stack no gearset references, keep list INCLUDED — the cleanup preview lists
    /// these so an item can be vetoed (and un-vetoed) instead of silently disappearing.
    /// </summary>
    public static List<ArmouryItem> Unregistered(
        IReadOnlyList<ArmouryItem> armoury,
        IReadOnlyCollection<uint> gearsetItemIds)
    {
        if (gearsetItemIds.Count == 0)
            return new List<ArmouryItem>();

        var registered = new HashSet<uint>(gearsetItemIds);

        return armoury
            .Where(i => i.ItemId != 0 && !i.IsSoulCrystal && !registered.Contains(i.ItemId))
            .ToList();
    }
}
