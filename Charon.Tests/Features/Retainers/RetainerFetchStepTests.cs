using System.Collections.Generic;
using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public class RetainerFetchStepTests
{
    private const uint Manganese = 36165;
    private const int RetainerPage1 = 31; // InventoryType.RetainerPage1 — the container value decides nothing here

    private static readonly List<FetchSlot> TwoStacks =
    [
        new(RetainerPage1, 3, Manganese, 65, false),
        new(RetainerPage1, 9, Manganese, 12, true),
    ];

    private static FetchDecision Decide(
        bool armed = true,
        string? open = "T'sala",
        string? planned = "T'sala",
        int wanted = 100,
        bool hqOnly = false,
        int movedNq = 0,
        int movedHq = 0,
        IReadOnlyList<FetchSlot>? slots = null,
        int freeSlots = 5,
        string? refusal = null) =>
        RetainerFetchStep.Decide(armed, open, planned, Manganese, wanted, hqOnly, movedNq, movedHq,
            slots ?? TwoStacks, freeSlots, refusal);

    [Fact]
    public void NotArmed_DoesNothing()
    {
        var decision = Decide(armed: false);

        Assert.Equal(FetchAction.None, decision.Action);
        Assert.Equal("no fetch armed", decision.Reason);
    }

    [Fact]
    public void AStoreRefusal_AbortsWithTheStoresOwnReason()
    {
        var decision = Decide(refusal: "T'sala holds 4 of the 100 wanted");

        Assert.Equal(FetchAction.Abort, decision.Action);
        Assert.Contains("holds 4 of the 100", decision.Reason);
    }

    [Fact]
    public void NoRetainerOpen_WaitsAndNamesTheOneToOpen()
    {
        var decision = Decide(open: null);

        Assert.Equal(FetchAction.None, decision.Action);
        Assert.Equal("waiting — open T'sala at a bell", decision.Reason);
    }

    [Fact]
    public void TheWrongRetainerOpen_WaitsRatherThanFailing()
    {
        // The player is at the bell and can fix this, so it is a wait, not an abort.
        var decision = Decide(open: "T'sola");

        Assert.Equal(FetchAction.None, decision.Action);
        Assert.Contains("waiting for T'sala", decision.Reason);
    }

    [Fact]
    public void TheQuantityMoved_CompletesThePass()
    {
        var decision = Decide(movedNq: 100);

        Assert.Equal(FetchAction.Done, decision.Action);
        Assert.Contains("moved 100 normal", decision.Reason);
    }

    [Fact]
    public void HighQualityOnly_CountsOnlyHighQualityTowardsTheQuantity()
    {
        // 100 normal moved is nothing when the caller asked for HQ.
        var decision = Decide(hqOnly: true, movedNq: 100, movedHq: 0);

        Assert.Equal(FetchAction.Move, decision.Action);
        Assert.True(decision.Hq);
    }

    [Fact]
    public void NormalQualityIsTakenBeforeHighQuality()
    {
        var decision = Decide();

        Assert.Equal(FetchAction.Move, decision.Action);
        Assert.Equal(3, decision.Slot);
        Assert.False(decision.Hq);
        Assert.Equal(65, decision.Quantity);
    }

    [Fact]
    public void HighQualityOnly_NeverTakesANormalStack()
    {
        var decision = Decide(hqOnly: true);

        Assert.Equal(FetchAction.Move, decision.Action);
        Assert.Equal(9, decision.Slot);
        Assert.True(decision.Hq);
    }

    [Fact]
    public void WithOnlyHighQualityLeft_ANormalFetchTopsUpFromIt()
    {
        var decision = Decide(slots: [new FetchSlot(RetainerPage1, 9, Manganese, 12, true)]);

        Assert.Equal(FetchAction.Move, decision.Action);
        Assert.True(decision.Hq);
    }

    [Fact]
    public void AStaleSlot_AbortsRatherThanMovingWhateverIsThere()
    {
        // The store said the retainer holds it; the bags say otherwise. Moving "whatever is in slot 3" is
        // how a player ends up with the wrong items, so this stops and says so.
        var decision = Decide(slots: [new FetchSlot(RetainerPage1, 3, 9999, 65, false)]);

        Assert.Equal(FetchAction.Abort, decision.Action);
        Assert.Contains("no longer holds that item", decision.Reason);
        Assert.Contains("stale", decision.Reason);
    }

    [Fact]
    public void NoBagSpace_AbortsRatherThanDroppingIt()
    {
        var decision = Decide(freeSlots: 0);

        Assert.Equal(FetchAction.Abort, decision.Action);
        Assert.Contains("no free bag slot", decision.Reason);
    }

    [Fact]
    public void MovingSaysItTakesTheWholeStack()
    {
        var decision = Decide();

        Assert.Contains("stack size, not a partial", decision.Reason);
    }

    [Fact]
    public void AskingForNothingAborts()
    {
        var decision = Decide(wanted: 0);

        Assert.Equal(FetchAction.Abort, decision.Action);
    }
}
