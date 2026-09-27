namespace Charon.Features.Retainers;

/// <summary>What the retainer roster read actually established.</summary>
public enum RosterState
{
    /// <summary>The game has not handed the roster over right now. NOT the same as having none.</summary>
    NotLoaded,

    /// <summary>The roster is loaded and genuinely holds no retainers.</summary>
    Empty,

    /// <summary>Loaded, with retainers in it.</summary>
    Present,
}

/// <summary>
/// Separates "the roster is empty" from "the roster is not loaded". Pure — no Dalamud types.
///
/// The game RELOADS its retainer roster while a retainer is being dismissed at a bell, and for that
/// moment <c>RetainerManager</c> reports not-ready and every read comes back empty. A refresh pass
/// that took that as "this character has no retainers" switched itself off halfway through a live
/// bell cycle — the first retainer captured, the second never asked for, and the status reading
/// "refresh stopped — no retainers on this character" on a character with two.
///
/// It is the same mistake this codebase keeps meeting in new clothes: a TRANSIENT nothing read as
/// a DEFINITIVE none. Only a loaded roster can say "none" — and even then, NOT once this pass
/// has already seen retainers: a character's roster cannot drop to zero in the middle of a bell
/// session, so an empty read after a non-empty one is a reload whatever the loaded flag says.
/// (Which of the two the game does mid-dismissal was never pinned down; this holds either way.)
/// </summary>
public static class RetainerRoster
{
    public static RosterState Classify(bool loaded, int count, bool seenThisPass = false) =>
        count > 0 && loaded ? RosterState.Present
        : !loaded || seenThisPass ? RosterState.NotLoaded
        : RosterState.Empty;
}
