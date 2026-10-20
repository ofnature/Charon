using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Tweaks;

public enum CutsceneSkipAction
{
    None,

    /// <summary>Let the cutscene hook raise the game's "Skip cutscene?" prompt (see CutsceneSkipper).</summary>
    Trigger,

    /// <summary>Choose "Yes." on the prompt.</summary>
    ConfirmYes,
}

public readonly record struct CutsceneSkipDecision(CutsceneSkipAction Action, string Reason, int EntryIndex = -1);

/// <summary>
/// Skip cutscenes on an unattended toon. Pure — no Dalamud types.
///
/// The FLOW is the game's own: something raises its "Skip cutscene?" prompt, and "Yes." skips. What
/// raises it is ECommons' AutoCutsceneSkipper (MIT, NightmareXIV/ECommons — ported with attribution in
/// CutsceneSkipper): a hook on the game's cutscene input handler that, for a SKIPPABLE cutscene, runs the
/// original with one branch patched so the game acts as if skip had been pressed. TextAdvance and
/// AutoRetainer ship that same hook.
///
/// A KEY PRESS DOES NOT WORK, and the evidence is in the log (user, 2026-10-19): Escape — and later the
/// box's own Esc binding — posted to the game window four times per cutscene, never once raising the
/// prompt; every "skipped" line came seconds after the fourth press, i.e. after a human pressed it. The
/// prompt itself was always recognised and answered, so confirming is proven; only the trigger changed.
///
/// The prompt is recognised by the GAME'S OWN text — Addon rows 281 / 5882 "Skip cutscene?" and
/// 3 / 4472 / 4621 / 8195 "Yes." (XIVAPI) — read in the client's language, or as a two-entry list holding
/// "Yes." during a cutscene (Odysseus's rule, the fallback when the title cannot be read).
/// </summary>
public static class CutsceneSkipPolicy
{
    public static CutsceneSkipDecision Decide(bool enabled, bool inCutscene, int skipPromptYesIndex, bool otherListUp)
    {
        if (!enabled)
            return new(CutsceneSkipAction.None, "off");

        if (!inCutscene)
            return new(CutsceneSkipAction.None, "waiting for a cutscene");

        // The prompt first: it is the answer to our own trigger.
        if (skipPromptYesIndex >= 0)
            return new(CutsceneSkipAction.ConfirmYes, "skipping the cutscene", skipPromptYesIndex);

        if (otherListUp)
            return new(CutsceneSkipAction.None, "a choice is on screen — not asking to skip over it");

        return new(CutsceneSkipAction.Trigger, "asking to skip");
    }

    /// <summary>Longest lease a caller can take at once — it refreshes well inside it.</summary>
    public const int MaxLeaseSeconds = 300;

    /// <summary>
    /// Whether to skip right now. A live IPC lease (Odysseus's roleplay choice) wins in EITHER direction —
    /// "skip" on a box whose toggle is off, or "watch" on one whose toggle is on; once it expires the toggle
    /// decides again. A TTL, not a switch: a caller that dies stops refreshing and its choice lapses.
    /// </summary>
    public static bool Effective(bool toggle, bool leaseSkip, DateTime leaseUntilUtc, DateTime nowUtc) =>
        nowUtc < leaseUntilUtc ? leaseSkip : toggle;

    /// <summary>
    /// Whether a list on screen during a cutscene is the skip prompt: its title reads "Skip cutscene?", OR
    /// it is a two-entry list with the game's "Yes." in it — Odysseus's rule (during a cutscene the only list
    /// on screen is that one), the fallback for a title the reader cannot reach.
    /// </summary>
    public static bool LooksLikeSkipPrompt(bool titleMatches, int entryCount, int yesIndex) =>
        titleMatches || (entryCount == 2 && yesIndex >= 0);

    /// <summary>True when any of the prompt's texts is the game's "Skip cutscene?" text.</summary>
    public static bool IsSkipPrompt(IEnumerable<string> promptTexts, IReadOnlyCollection<string> skipTexts) =>
        skipTexts.Count > 0 && promptTexts.Any(t => Matches(t, skipTexts));

    /// <summary>The index of the "Yes." entry, or -1. Blank game text never matches anything.</summary>
    public static int YesIndex(IReadOnlyList<string> entries, IReadOnlyCollection<string> yesTexts)
    {
        for (var i = 0; i < entries.Count; i++)
            if (Matches(entries[i], yesTexts))
                return i;
        return -1;
    }

    private static bool Matches(string text, IReadOnlyCollection<string> candidates)
    {
        var trimmed = text.Trim();
        return trimmed.Length > 0
               && candidates.Any(c => c.Trim().Length > 0 && trimmed.Equals(c.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
