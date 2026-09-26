using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Retainers;

/// <summary>What a fetch pass should do on this tick.</summary>
public enum FetchAction
{
    /// <summary>Nothing yet — the reason says what it is waiting for.</summary>
    None,

    /// <summary>Move one stack from the open retainer's bags into the player's.</summary>
    Move,

    /// <summary>The quantity asked for has been moved.</summary>
    Done,

    /// <summary>Stop and say why: the request cannot be served, and waiting will not change that.</summary>
    Abort,
}

/// <param name="Container">The retainer container the stack is in (an <c>InventoryType</c> value).</param>
/// <param name="Slot">The slot inside that container.</param>
public sealed record FetchSlot(int Container, int Slot, uint ItemId, int Quantity, bool Hq);

public sealed record FetchDecision(FetchAction Action, int Container, int Slot, int Quantity, bool Hq, string Reason)
{
    public static FetchDecision None(string reason) => new(FetchAction.None, 0, 0, 0, false, reason);

    public static FetchDecision Done(string reason) => new(FetchAction.Done, 0, 0, 0, false, reason);

    public static FetchDecision Abort(string reason) => new(FetchAction.Abort, 0, 0, 0, false, reason);

    public static FetchDecision Move(FetchSlot slot, string reason) =>
        new(FetchAction.Move, slot.Container, slot.Slot, slot.Quantity, slot.Hq, reason);
}

/// <summary>
/// The decision layer for fetching items back out of a retainer: ONE move per tick, decided from what is
/// true right now rather than from a plan drawn up earlier.
///
/// The store's answer chose WHICH retainer and how much; everything after that is read live, because the
/// gap between the two is exactly where a stale answer would hurt. A stack that no longer holds what the
/// snapshot said aborts the pass with that fact rather than moving whatever happens to be in the slot —
/// the alternative is handing the player the wrong items, which is worse than not finishing.
///
/// Pure: the caller hands in the slots it can see, so every rule here is testable without a client.
/// </summary>
public static class RetainerFetchStep
{
    public static FetchDecision Decide(
        bool armed,
        string? openRetainer,
        string? plannedRetainer,
        uint itemId,
        int wanted,
        bool hqOnly,
        int movedNq,
        int movedHq,
        IReadOnlyList<FetchSlot> slots,
        int freePlayerSlots,
        string? refusal)
    {
        if (!armed)
            return FetchDecision.None("no fetch armed");

        // The store already said this cannot be done — repeating that here keeps the reason in one voice.
        if (!string.IsNullOrEmpty(refusal))
            return FetchDecision.Abort(refusal);

        if (wanted <= 0)
            return FetchDecision.Abort("nothing was asked for");

        if (openRetainer == null || openRetainer.Length == 0)
        {
            return FetchDecision.None(plannedRetainer is { Length: > 0 }
                ? $"waiting — open {plannedRetainer} at a bell"
                : "waiting — open a retainer at a bell");
        }

        if (plannedRetainer is { Length: > 0 } &&
            !string.Equals(plannedRetainer, openRetainer, StringComparison.OrdinalIgnoreCase))
        {
            // The wrong retainer being open is not a failure: the player is at the bell and can fix it.
            return FetchDecision.None($"waiting for {plannedRetainer} — that is where the store last saw it");
        }

        var moved = hqOnly ? movedHq : movedNq + movedHq;
        var remaining = wanted - moved;
        if (remaining <= 0)
            return FetchDecision.Done($"moved {movedNq} normal and {movedHq} high-quality");

        var candidates = slots.Where(s => s.ItemId == itemId && s.Quantity > 0).ToList();

        // Normal quality first when the caller took either: NQ is what a stack of materials usually is,
        // and leaving it behind while taking HQ would be the surprising way round.
        candidates = hqOnly
            ? candidates.Where(s => s.Hq).OrderByDescending(s => s.Quantity).ToList()
            : [.. candidates.OrderBy(s => s.Hq).ThenByDescending(s => s.Quantity)];

        if (candidates.Count == 0)
        {
            return FetchDecision.Abort($"the retainer no longer holds that item — the store's answer was from "
                + $"{Describe(plannedRetainer)} and is stale");
        }

        if (freePlayerSlots <= 0)
            return FetchDecision.Abort("no free bag slot for what would come back");

        var pick = candidates[0];

        // Whole stacks only: splitting needs a quantity dialog, and a fetch that stops to ask is worse than
        // one that brings a stack and says how much it brought.
        return FetchDecision.Move(pick, $"moving {pick.Quantity}{(pick.Hq ? " HQ" : string.Empty)} from slot "
            + $"{pick.Slot} (stack size, not a partial)");
    }

    private static string Describe(string? retainer) => retainer is { Length: > 0 } ? retainer : "an earlier pass";
}
