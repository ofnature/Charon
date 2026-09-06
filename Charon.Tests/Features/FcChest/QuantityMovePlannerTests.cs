using System.Collections.Generic;
using System.Linq;
using Charon.Features.FcChest;

namespace Charon.Tests.Features.FcChest;

public sealed class QuantityMovePlannerTests
{
    private const int Chest = 20001; // stand-in container ids — the planner never interprets them
    private const int Bag = 0;

    private static InvStack ChestStack(short slot, int qty, uint itemId = 100, bool hq = false)
        => new(Chest, slot, itemId, qty, 999, hq);

    private static InvStack BagStack(short slot, int qty, uint itemId = 100, bool hq = false, int max = 999)
        => new(Bag, slot, itemId, qty, max, hq);

    // --- PlanWithdraw ---

    [Fact]
    public void Withdraw_FillsBagPartialsBeforeEmptySlots()
    {
        var moves = QuantityMovePlanner.PlanWithdraw(
            chestStacks: [ChestStack(0, 500)],
            bagPartials: [BagStack(3, 990)], // 9 units of space
            freeBagSlots: [(Bag, 10)],
            amount: 50);

        Assert.Equal(2, moves.Count);
        Assert.Equal(9, moves[0].Quantity);
        Assert.Equal(3, moves[0].DstSlot);
        Assert.Equal(41, moves[1].Quantity);
        Assert.Equal(10, moves[1].DstSlot);
        Assert.Equal(50, QuantityMovePlanner.TotalUnits(moves));
    }

    [Fact]
    public void Withdraw_TakesFromSmallestChestStacksFirst()
    {
        var moves = QuantityMovePlanner.PlanWithdraw(
            chestStacks: [ChestStack(0, 999), ChestStack(1, 5)],
            bagPartials: [],
            freeBagSlots: [(Bag, 0), (Bag, 1)],
            amount: 5);

        var move = Assert.Single(moves);
        Assert.Equal(1, move.SrcSlot); // the 5-stack covers the ask — the 999 pile is untouched
        Assert.True(move.WholeStack); // whole source stack to an empty slot
    }

    [Fact]
    public void Withdraw_PartialFromAStack_IsNotWholeStack()
    {
        var moves = QuantityMovePlanner.PlanWithdraw(
            chestStacks: [ChestStack(0, 100)],
            bagPartials: [],
            freeBagSlots: [(Bag, 0)],
            amount: 30);

        var move = Assert.Single(moves);
        Assert.Equal(30, move.Quantity);
        Assert.False(move.WholeStack);
    }

    [Fact]
    public void Withdraw_NoRoom_PlansOnlyWhatFits()
    {
        var moves = QuantityMovePlanner.PlanWithdraw(
            chestStacks: [ChestStack(0, 100)],
            bagPartials: [BagStack(0, 995)], // 4 of space
            freeBagSlots: [],
            amount: 50);

        Assert.Equal(4, QuantityMovePlanner.TotalUnits(moves));
    }

    [Fact]
    public void Withdraw_SpansMultipleChestStacks()
    {
        var moves = QuantityMovePlanner.PlanWithdraw(
            chestStacks: [ChestStack(0, 10), ChestStack(1, 10)],
            bagPartials: [],
            freeBagSlots: [(Bag, 0), (Bag, 1)],
            amount: 15);

        Assert.Equal(2, moves.Count);
        Assert.Equal(15, QuantityMovePlanner.TotalUnits(moves));
    }

    // --- PlanDepositAll ---

    [Fact]
    public void Deposit_TopsUpMatchingPartialThenEmpty()
    {
        var moves = QuantityMovePlanner.PlanDepositAll(
            bagStacks: [BagStack(0, 100)],
            chestPartials: [ChestStack(5, 950)], // 49 of space
            emptyChestSlots: [(Chest, 9)],
            allowPartial: true);

        Assert.Equal(2, moves.Count);
        Assert.Equal(49, moves[0].Quantity);
        Assert.False(moves[0].WholeStack);
        Assert.Equal(51, moves[1].Quantity);
        Assert.False(moves[1].WholeStack); // remainder of a split source is not the whole stack
    }

    [Fact]
    public void Deposit_WholeStackToEmpty_MarkedWholeStack()
    {
        var moves = QuantityMovePlanner.PlanDepositAll(
            bagStacks: [BagStack(0, 100)],
            chestPartials: [],
            emptyChestSlots: [(Chest, 0)],
            allowPartial: true);

        var move = Assert.Single(moves);
        Assert.True(move.WholeStack);
    }

    [Fact]
    public void Deposit_HqNeverMergesIntoNq()
    {
        var moves = QuantityMovePlanner.PlanDepositAll(
            bagStacks: [BagStack(0, 10, hq: true)],
            chestPartials: [ChestStack(5, 500, hq: false)],
            emptyChestSlots: [(Chest, 9)],
            allowPartial: true);

        var move = Assert.Single(moves);
        Assert.True(move.WholeStack); // straight to the empty slot, no cross-quality merge
        Assert.Equal(9, move.DstSlot);
    }

    [Fact]
    public void Deposit_WithoutQuantityNative_SkipsPartialFills()
    {
        var moves = QuantityMovePlanner.PlanDepositAll(
            bagStacks: [BagStack(0, 100)],
            chestPartials: [ChestStack(5, 950)],
            emptyChestSlots: [(Chest, 9)],
            allowPartial: false);

        var move = Assert.Single(moves);
        Assert.True(move.WholeStack);
        Assert.Equal(100, move.Quantity);
    }

    [Fact]
    public void Deposit_ChestFull_SkipsTheRemainder()
    {
        var moves = QuantityMovePlanner.PlanDepositAll(
            bagStacks: [BagStack(0, 100), BagStack(1, 200, itemId: 101)],
            chestPartials: [],
            emptyChestSlots: [(Chest, 0)],
            allowPartial: true);

        var move = Assert.Single(moves); // only the first stack found a slot
        Assert.Equal(100u, move.ItemId);
    }

    [Fact]
    public void Deposit_TwoBagStacksShareOnePartial()
    {
        var moves = QuantityMovePlanner.PlanDepositAll(
            bagStacks: [BagStack(0, 10), BagStack(1, 10)],
            chestPartials: [ChestStack(5, 985)], // 14 of space
            emptyChestSlots: [(Chest, 9)],
            allowPartial: true);

        Assert.Equal(3, moves.Count);
        Assert.Equal(10, moves[0].Quantity); // first stack fully into the partial
        Assert.Equal(4, moves[1].Quantity);  // second tops it off
        Assert.Equal(6, moves[2].Quantity);  // remainder to the empty slot
        Assert.Equal(20, moves.Sum(m => m.Quantity));
    }
}
