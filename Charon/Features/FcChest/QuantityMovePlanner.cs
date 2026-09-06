using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.FcChest;

/// <summary>One stack somewhere (chest page or player bag), as the planner sees it.</summary>
public sealed record InvStack(int Container, short Slot, uint ItemId, int Quantity, int MaxStack, bool Hq);

/// <summary>
/// One native move of <paramref name="Quantity"/> units. <paramref name="WholeStack"/> means the
/// source stack moves entirely to an EMPTY slot — executable via plain MoveItemSlot, no quantity
/// native needed; everything else requires the quantity-taking native.
/// </summary>
public sealed record PlannedMove(uint ItemId, int SrcContainer, short SrcSlot,
    int DstContainer, short DstSlot, int Quantity, bool WholeStack);

/// <summary>
/// Plans quantity-accurate FC chest transfers. Pure — containers come in as snapshots, moves come
/// out; the adapter executes and verifies. Doctrine shared with the rest of the chest tools:
/// fill matching partial stacks FIRST (no new slots wasted), then claim empty slots; never plan
/// more than the amount asked for; a plan that cannot fully fit still moves what fits.
/// </summary>
public static class QuantityMovePlanner
{
    /// <summary>
    /// Withdraw exactly <paramref name="amount"/> units of one item from chest stacks into bags:
    /// merge into bag stacks of the same item/quality with space first, then empty bag slots.
    /// </summary>
    public static IReadOnlyList<PlannedMove> PlanWithdraw(
        IReadOnlyList<InvStack> chestStacks,
        IReadOnlyList<InvStack> bagPartials,
        IReadOnlyList<(int Container, short Slot)> freeBagSlots,
        int amount)
    {
        var moves = new List<PlannedMove>();
        if (amount <= 0 || chestStacks.Count == 0)
            return moves;

        // Take from the smallest chest stacks first — clears slots sooner and never breaks a
        // big stack when a small one covers the ask.
        var sources = chestStacks.Where(s => s.Quantity > 0).OrderBy(s => s.Quantity).ToList();
        var partials = new Queue<(InvStack Stack, int Space)>(
            bagPartials.Where(p => p.Quantity < p.MaxStack)
                .OrderByDescending(p => p.Quantity)
                .Select(p => (p, p.MaxStack - p.Quantity)));
        var empties = new Queue<(int Container, short Slot)>(freeBagSlots);

        var remaining = amount;
        foreach (var src in sources)
        {
            if (remaining <= 0)
                break;

            var takeFromStack = Math.Min(remaining, src.Quantity);
            var slotLeft = src.Quantity;

            while (takeFromStack > 0)
            {
                if (partials.Count > 0)
                {
                    var (dst, space) = partials.Dequeue();
                    var move = Math.Min(takeFromStack, space);
                    moves.Add(new PlannedMove(src.ItemId, src.Container, src.Slot,
                        dst.Container, dst.Slot, move, WholeStack: false));
                    takeFromStack -= move;
                    remaining -= move;
                    slotLeft -= move;
                    if (space - move > 0)
                        partials = Requeue(partials, (dst with { Quantity = dst.Quantity + move }, space - move));
                    continue;
                }

                if (empties.Count > 0)
                {
                    var dst = empties.Dequeue();
                    moves.Add(new PlannedMove(src.ItemId, src.Container, src.Slot,
                        dst.Container, dst.Slot, takeFromStack,
                        WholeStack: takeFromStack == slotLeft));
                    remaining -= takeFromStack;
                    takeFromStack = 0;
                    continue;
                }

                break; // nowhere left to put it — partial plan
            }

            if (partials.Count == 0 && empties.Count == 0 && remaining > 0)
                break;
        }

        return moves;
    }

    /// <summary>
    /// Deposit every bag stack into the chest: fill matching partial chest stacks first, then
    /// whole stacks into empty chest slots. With <paramref name="allowPartial"/> false (the
    /// quantity native unavailable), only the whole-stack-to-empty moves are planned.
    /// </summary>
    public static IReadOnlyList<PlannedMove> PlanDepositAll(
        IReadOnlyList<InvStack> bagStacks,
        IReadOnlyList<InvStack> chestPartials,
        IReadOnlyList<(int Container, short Slot)> emptyChestSlots,
        bool allowPartial)
    {
        var moves = new List<PlannedMove>();
        var partialsByItem = chestPartials
            .Where(p => p.Quantity < p.MaxStack)
            .GroupBy(p => (p.ItemId, p.Hq))
            .ToDictionary(g => g.Key, g => new Queue<(InvStack Stack, int Space)>(
                g.OrderByDescending(p => p.Quantity).Select(p => (p, p.MaxStack - p.Quantity))));
        var empties = new Queue<(int Container, short Slot)>(emptyChestSlots);

        foreach (var src in bagStacks)
        {
            var left = src.Quantity;

            if (allowPartial && partialsByItem.TryGetValue((src.ItemId, src.Hq), out var queue))
            {
                while (left > 0 && queue.Count > 0)
                {
                    var (dst, space) = queue.Dequeue();
                    var move = Math.Min(left, space);
                    moves.Add(new PlannedMove(src.ItemId, src.Container, src.Slot,
                        dst.Container, dst.Slot, move, WholeStack: false));
                    left -= move;
                    if (space - move > 0)
                        queue = partialsByItem[(src.ItemId, src.Hq)] =
                            Requeue(queue, (dst with { Quantity = dst.Quantity + move }, space - move));
                }
            }

            if (left <= 0)
                continue;

            if (empties.Count == 0)
                continue; // chest full — skip the remainder, report via the shortfall count

            var slot = empties.Dequeue();
            moves.Add(new PlannedMove(src.ItemId, src.Container, src.Slot,
                slot.Container, slot.Slot, left, WholeStack: left == src.Quantity));
        }

        return moves;
    }

    /// <summary>How many units of the plan's item actually move (for previews/confirm text).</summary>
    public static int TotalUnits(IReadOnlyList<PlannedMove> moves) => moves.Sum(m => m.Quantity);

    private static Queue<T> Requeue<T>(Queue<T> queue, T front)
    {
        var rebuilt = new Queue<T>();
        rebuilt.Enqueue(front);
        foreach (var item in queue)
            rebuilt.Enqueue(item);
        return rebuilt;
    }
}
