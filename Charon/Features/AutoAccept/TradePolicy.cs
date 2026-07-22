using System;

namespace Charon.Features.AutoAccept;

/// <summary>Mirror of the game's trade phases (kept Dalamud-free for testability).</summary>
public enum TradePhase
{
    NotTrading,
    RequestPending,
    SelectingGoods,
    LockedIn,
    WaitingForConfirmation,
    Confirmed,
}

/// <summary>What the automation should do this tick.</summary>
public enum TradeAction
{
    /// <summary>Leave the trade alone.</summary>
    None,

    /// <summary>Click the Trade button (lock our side in), mirroring the partner.</summary>
    LockIn,

    /// <summary>Answer Yes on the "Complete trade?" confirmation.</summary>
    Confirm,
}

/// <summary>
/// Decides whether to mirror a LAN toon's trade actions. Pure logic — no Dalamud types.
///
/// Hard gate: the trade partner (read from the trade window itself) must be a TRUSTED LAN toon.
/// A stranger's trade window is never touched, so the irreversible steps can only ever fire
/// between the user's own characters. We also only ever move FORWARD when the partner has
/// already committed — we lock in after they lock in, never before, and never cancel.
/// </summary>
public static class TradePolicy
{
    public static TradeAction Decide(
        bool enabled,
        bool partnerTrusted,
        TradePhase local,
        TradePhase remote,
        bool confirmDialogOpen)
    {
        if (!enabled || !partnerTrusted)
            return TradeAction.None;

        // The partner clicked Trade; mirror them so both sides are locked in.
        if (remote == TradePhase.LockedIn && local == TradePhase.SelectingGoods)
            return TradeAction.LockIn;

        // Answer the "Complete trade?" prompt — but ONLY when BOTH sides have locked in, which
        // is exactly when that prompt appears. The prompt is a generic SelectYesno, so requiring
        // the partner (remote) to have committed too guards against auto-answering some unrelated
        // yes/no that happens to be open mid-trade.
        if (confirmDialogOpen
            && local is TradePhase.LockedIn or TradePhase.WaitingForConfirmation or TradePhase.Confirmed
            && remote is TradePhase.LockedIn or TradePhase.WaitingForConfirmation or TradePhase.Confirmed)
            return TradeAction.Confirm;

        return TradeAction.None;
    }
}
