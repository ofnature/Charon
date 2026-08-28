using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.FcChest;

/// <summary>One bag stack considered for saddlebag entrusting.</summary>
public sealed record SaddlebagCandidate(uint ItemId, int Container, short Slot, int Quantity, bool IsUnique);

/// <summary>
/// Picks which bag stacks to entrust to the chocobo saddlebag: exactly the items the saddlebag
/// ALREADY holds (same duplicates-only doctrine as the FC chest entrust — the saddlebag copy is
/// the "seed" that marks an item as belonging there; nothing new is ever moved in). Pure logic —
/// no Dalamud types. Behaviour matches PandorasBox's Saddlebag Entrust Duplicates
/// (BSD-3-Clause), minus their bug of also entrusting unique items' partners: unique items are
/// skipped outright.
/// </summary>
public static class SaddlebagDuplicates
{
    public static List<SaddlebagCandidate> FindEntrustable(
        IEnumerable<SaddlebagCandidate> bagStacks, IReadOnlySet<uint> saddlebagItemIds) =>
        bagStacks
            .Where(s => s.ItemId != 0 && !s.IsUnique && saddlebagItemIds.Contains(s.ItemId))
            .OrderBy(s => s.Container)
            .ThenBy(s => s.Slot)
            .ToList();
}
