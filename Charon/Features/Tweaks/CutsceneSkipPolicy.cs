using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Tweaks;

public enum CutsceneSkipAction
{
    None,

    /// <summary>Press Escape: the game answers with its "Skip cutscene?" prompt.</summary>
    PressEscape,

    /// <summary>Choose "Yes." on the prompt.</summary>
    ConfirmYes,
}

public readonly record struct CutsceneSkipDecision(CutsceneSkipAction Action, string Reason, int EntryIndex = -1);

/// <summary>
/// Skip cutscenes on an unattended toon. Pure — no Dalamud types.
///
/// The FLOW is the game's own and is what a player does: Escape during a cutscene raises a "Skip
/// cutscene?" prompt, and "Yes." skips it. The idea is TextAdvance's ("Automatic ESC press during
/// cutscene" + "Automatic cutscene skip confirmation") — it ships with NO licence, so nothing of it was
/// reused: TextAdvance presses Escape through ECommons' code hook and matches the prompt against a
/// hand-kept list of strings per language. Charon sends the key to its own window (the AfkGuard / QTE
/// mechanism) and matches the prompt against the GAME'S OWN text — Addon rows 281 / 5882 "Skip
/// cutscene?" and 3 / 4472 / 4621 / 8195 "Yes." (XIVAPI) — read in the client's language.
///
/// Escape is only ever sent inside a cutscene, never while a dialogue box or any list is up (that is
/// someone else's moment), at most every <see cref="PressEvery"/>, and only <see cref="MaxPresses"/>
/// times per cutscene — an unskippable one is reported, not hammered.
/// </summary>
public static class CutsceneSkipPolicy
{
    public static readonly TimeSpan PressEvery = TimeSpan.FromSeconds(2);

    public const int MaxPresses = 4;

    public static CutsceneSkipDecision Decide(
        bool enabled,
        bool inCutscene,
        bool talkUp,
        int skipPromptYesIndex,
        bool otherListUp,
        TimeSpan sinceLastPress,
        int pressesThisCutscene)
    {
        if (!enabled)
            return new(CutsceneSkipAction.None, "off");

        if (!inCutscene)
            return new(CutsceneSkipAction.None, "waiting for a cutscene");

        // The prompt first: it is the answer to our own Escape.
        if (skipPromptYesIndex >= 0)
            return new(CutsceneSkipAction.ConfirmYes, "skipping the cutscene", skipPromptYesIndex);

        if (talkUp)
            return new(CutsceneSkipAction.None, "dialogue on screen — leaving it to the dialogue advance");

        if (otherListUp)
            return new(CutsceneSkipAction.None, "a choice is on screen — not pressing Escape over it");

        if (pressesThisCutscene >= MaxPresses)
            return new(CutsceneSkipAction.None, "this cutscene does not offer a skip");

        if (sinceLastPress < PressEvery)
            return new(CutsceneSkipAction.None, "asked to skip — waiting for the prompt");

        return new(CutsceneSkipAction.PressEscape, "asking to skip (Escape)");
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
