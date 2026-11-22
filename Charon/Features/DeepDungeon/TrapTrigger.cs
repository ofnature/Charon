namespace Charon.Features.DeepDungeon;

/// <summary>
/// A trap going off, read from the game's own log message — the hook-free stand-in for Palace Pal's
/// "trap exploded" detector, which hooked a function found by byte pattern (requested 2026-11-21, relayed
/// from the Mnemosyne session). Pure.
///
/// The rows are the LogMessage sheet's (checked against the game files): 7224 landmine, 7225 luring,
/// 7226 enfeebling, 7227 impeding, 7228 toading, 9210 odder, 10278 owlet, 11247 fae — each "The X trap is
/// triggered...".
///
/// THE MESSAGE REACHES ONLY THE CLIENT OF THE PLAYER WHO STEPPED ON THE TRAP — unlike the pomander
/// messages of the same family (7222, 7254), which every party member gets. Seen live 2026-11-22 in a party
/// of four: an impeding trap (7227) went off under one toon, whose client received it; a second toon of the
/// same party stood 10.2 yalms away, was silenced by that same trap, and its client received nothing. So
/// "the trap is where I am standing when the message comes" holds on every client that sees it, in a party
/// or alone, and the spot is recorded there. One sample: a transforming trap, which hits only its stepper
/// (<see cref="HitsOnlyTheStepper"/>), is the cross-check — its message should only ever come to the toon
/// that is transformed a moment later, and the log says whether it did.
/// </summary>
public static class TrapTrigger
{
    public static bool IsTriggered(uint logMessageId) =>
        logMessageId is >= 7224 and <= 7228 or 9210 or 10278 or 11247;

    /// <summary>
    /// The transforming traps — toading 7228, odder 9210, owlet 10278, fae 11247 — hit ONE player, the one
    /// who stepped on them; the landmine, the enfeebling trap and the rest hit an area (user, 2026-11-21).
    /// So a transforming trap names its own stepper: whoever is transformed a moment later.
    /// </summary>
    public static bool HitsOnlyTheStepper(uint logMessageId) =>
        logMessageId is 7228 or 9210 or 10278 or 11247;

    /// <summary>How long after the message the transformation is waited for.</summary>
    public static readonly System.TimeSpan TransformWindow = System.TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// After a transforming trap: it was under us if we were not transformed when the message came and are
    /// now. Used only to check the message against what happened — the spot is recorded either way.
    /// </summary>
    public static bool WeSteppedOnIt(uint logMessageId, int transformBefore, int transformNow) =>
        HitsOnlyTheStepper(logMessageId) && transformBefore == 0 && transformNow != 0;
}
