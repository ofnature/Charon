using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.FcChest;

/// <summary>One occupied inventory/chest slot.</summary>
public sealed record ItemStack(uint ItemId, string Name, int Quantity, int Container, short Slot);

/// <summary>One planned whole-stack move (the game's MoveItemSlot has no quantity parameter).</summary>
public sealed record ChestMove(uint ItemId, string Name, int Quantity, int SrcContainer, short SrcSlot);

/// <summary>
/// Plans FC-chest entrust/withdraw operations for ONE chest page. Pure logic — no Dalamud types.
///
/// Granularity is WHOLE STACKS: the game's MoveItemSlot API moves entire slots (verified — no
/// quantity parameter; unit-level "all but 1" would need stack splitting, deliberately out of
/// scope). The keep-minimum-1 rules therefore read:
/// - ENTRUST: only items that ALREADY exist on the selected page are entrusted (never seeds the
///   chest with new items, and the page seed is the guaranteed kept copy — an item can never
///   leave your holdings entirely).
/// - WITHDRAW: for items with multiple stacks on the page, withdraw all but the LAST stack;
///   a single-stack item is left untouched (the seed).
/// Only the given page's stacks are ever considered — other pages are structurally untouchable.
/// </summary>
public static class FcChestPlanner
{
    /// <summary>Both operations require the chest UI open (server session) AND that page's data loaded.</summary>
    public static bool CanExecute(bool chestOpen, bool pageLoaded) => chestOpen && pageLoaded;

    /// <summary>Inventory stacks of items already present on the chest page, in inventory order.</summary>
    public static List<ChestMove> PlanEntrust(
        IReadOnlyList<ItemStack> inventory,
        IReadOnlyList<ItemStack> chestPage)
    {
        var seeded = new HashSet<uint>(chestPage.Select(s => s.ItemId));

        return inventory
            .Where(s => s.ItemId != 0 && s.Quantity > 0 && seeded.Contains(s.ItemId))
            .Select(s => new ChestMove(s.ItemId, s.Name, s.Quantity, s.Container, s.Slot))
            .ToList();
    }

    /// <summary>Per-item withdraw: all stacks of ONE item except the last (the seed stays).</summary>
    public static List<ChestMove> PlanWithdrawItem(IReadOnlyList<ItemStack> chestPage, uint itemId) =>
        PlanWithdraw(chestPage.Where(s => s.ItemId == itemId).ToList());

    /// <summary>Chest-page stacks to withdraw: everything except the last stack of each item.</summary>
    public static List<ChestMove> PlanWithdraw(IReadOnlyList<ItemStack> chestPage)
    {
        var moves = new List<ChestMove>();

        foreach (var group in chestPage.Where(s => s.ItemId != 0 && s.Quantity > 0).GroupBy(s => s.ItemId))
        {
            var stacks = group.ToList();
            if (stacks.Count < 2)
                continue; // single stack = the seed, always stays

            foreach (var stack in stacks.Take(stacks.Count - 1))
                moves.Add(new ChestMove(stack.ItemId, stack.Name, stack.Quantity, stack.Container, stack.Slot));
        }

        return moves;
    }
}
