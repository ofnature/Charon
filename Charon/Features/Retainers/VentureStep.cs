using System;
using System.Collections.Generic;

namespace Charon.Features.Retainers;

/// <summary>Which retainer window is in front of us right now.</summary>
public enum VentureScreen
{
    /// <summary>Nothing of ours is open — the only safe default.</summary>
    None,

    /// <summary>A SelectString: either the retainer's own menu or the venture category list.</summary>
    Menu,

    /// <summary>RetainerTaskAsk — the "send them on this venture?" confirmation.</summary>
    TaskAsk,

    /// <summary>RetainerTaskResult — the finished venture's report.</summary>
    TaskResult,

    /// <summary>
    /// RetainerTaskList — the venture picker, open for the retainer currently being served. This is where
    /// a PLANNED venture gets chosen, which is the one click AutoRetainer never makes: it either reassigns
    /// the venture already there or takes quick exploration.
    /// </summary>
    TaskList,
}

public enum VentureAction
{
    /// <summary>Do nothing this tick.</summary>
    None,

    /// <summary>Pick a SelectString entry (see <see cref="VentureDecision.EntryIndex"/>).</summary>
    SelectEntry,

    /// <summary>Collect the report AND send them out again on the same venture.</summary>
    Reassign,

    /// <summary>Collect the report only.</summary>
    Confirm,

    /// <summary>Confirm the new venture assignment.</summary>
    Assign,

    /// <summary>Leave the retainer: pick the menu's Quit entry, so the run ends back at the retainer list.</summary>
    Quit,

    /// <summary>Choose the planned venture in the venture list (see <c>VentureStep.Decide</c>'s wantedTaskId).</summary>
    PickVenture,
}

public sealed record VentureDecision(VentureAction Action, int EntryIndex, string Reason)
{
    public static VentureDecision Nothing(string reason) => new(VentureAction.None, -1, reason);
}

/// <summary>
/// The menu entry texts, read from the game's own Addon sheet so matching is language-independent
/// (the doctrine the gil tools and Doman Donate already use).
///
/// 2386 and 2387 are NOT two spellings of one entry — they are the retainer's STATE, and that
/// difference is the whole safety of this feature. 2386 "Assign venture." means nothing is
/// assigned, so sending them out is free and correct. 2387 "Assign venture. (In progress)" means
/// they are ALREADY OUT, and clicking it assigns a REPLACEMENT venture and charges venture
/// currency again — verified live: a reassign that had just re-sent a retainer was immediately
/// followed by the assist opening the category list to send her out a second time.
/// AssignInProgress exists only so that case can be recognised and refused.
///
/// Quick Exploration is NOT an Addon row — it is the summoning bell's own dialogue
/// (custom/000/CmnDefRetainerCall_00010 row 402, COLUMN 1; column 0 is the internal key).
/// </summary>
public sealed record VentureMenuText(
    string ViewReport,
    string AssignIdle,
    string AssignInProgress,
    string QuickExploration,
    /// <summary>The menu's last entry, "Quit." — how a run leaves the retainer instead of parking in its menu.</summary>
    string Quit = "",
    /// <summary>"View venture report. (Complete on 27/8 8:00)" — the dated variant of <see cref="ViewReport"/>.</summary>
    string ViewReportDated = "");

/// <summary>
/// Decides the ONE next click inside an already-open retainer window. Pure logic — no Dalamud types.
///
/// This layer is deliberately incapable of starting anything: it never opens a bell, never picks a
/// retainer, and only ever answers "given what is on screen right now, what is the single next
/// click?". That is the whole design. AutoRetainer's lock-in comes from enqueuing an entire round
/// and draining the queue regardless of what the player does; there is no queue here to drain, so
/// disarming takes effect on the very next tick and closing the window ends it outright.
///
/// Two paths, both ending with the retainer back out on a venture:
///   finished venture -> "view venture report" -> Reassign (collects AND re-sends the same venture)
///   idle retainer    -> "assign venture" -> "quick exploration" -> Assign
/// </summary>
public static class VentureStep
{
    /// <summary>
    /// Whether the bell session has ended. Time-based ON PURPOSE: selecting a retainer CLOSES the
    /// retainer list, so "no retainer window is open right now" is not an ending - it is the
    /// normal gap between one window and the next. Only nothing being open for a while means the
    /// player walked away. Reading a closed list as the end disarmed the assist the instant a
    /// retainer was picked, which is the bug this guards.
    /// </summary>
    public static bool SessionEnded(TimeSpan sinceAnyRetainerWindow, TimeSpan grace) =>
        sinceAnyRetainerWindow > grace;

    public static VentureDecision Decide(
        bool armed,
        VentureScreen screen,
        IReadOnlyList<string> entries,
        bool reassignEnabled,
        bool confirmEnabled,
        bool assignEnabled,
        VentureMenuText text,
        uint wantedTaskId = 0,
        bool closeWhenDone = false)
    {
        if (!armed)
            return VentureDecision.Nothing("not armed");

        switch (screen)
        {
            case VentureScreen.None:
                return VentureDecision.Nothing("no retainer window open");

            case VentureScreen.TaskResult:
                // Reassign is collect-and-resend in one click, so it is always preferred: it keeps
                // the venture the player already chose instead of substituting our own idea of one.
                if (reassignEnabled)
                    return new VentureDecision(VentureAction.Reassign, -1, "collecting and re-sending");
                if (confirmEnabled)
                    return new VentureDecision(VentureAction.Confirm, -1, "collecting the report");
                return VentureDecision.Nothing("waiting for the report buttons");

            case VentureScreen.TaskAsk:
                return assignEnabled
                    ? new VentureDecision(VentureAction.Assign, -1, "confirming the venture")
                    : VentureDecision.Nothing("waiting for the assign button");

            case VentureScreen.TaskList:
                // The picker is open. With a plan we choose it; with no plan we do nothing rather than
                // clicking whatever happens to be first in the list — that would assign a venture nobody
                // asked for, at a venture-token cost.
                return wantedTaskId == 0
                    ? VentureDecision.Nothing("no venture planned for this retainer")
                    : new VentureDecision(VentureAction.PickVenture, -1, $"picking planned venture {wantedTaskId}");

            case VentureScreen.Menu:
                // The cycle is over for this retainer, so leave it: without this the run parks in the retainer's
                // menu with the work already done, which is where it used to end. Only reached once a send has
                // actually gone out — closing before that would abandon a collect or a re-send half done.
                if (closeWhenDone)
                {
                    var quit = IndexOf(entries, text.Quit);
                    if (quit >= 0)
                        return new VentureDecision(VentureAction.Quit, quit, "closing the retainer");
                }

                // Order matters: collect before assigning, or a finished venture's rewards would be
                // left sitting in the report while we sent the retainer straight back out. The report has two
                // labels in the client — with a completion date and without — and a both-branches check is how a
                // finished venture stays collectable instead of looking like nothing to do.
                var report = Math.Max(IndexOf(entries, text.ViewReportDated), IndexOf(entries, text.ViewReport));
                if (report >= 0)
                    return new VentureDecision(VentureAction.SelectEntry, report, "opening the venture report");

                var quick = IndexOf(entries, text.QuickExploration);
                if (quick >= 0)
                    return new VentureDecision(VentureAction.SelectEntry, quick, "picking quick exploration");

                // Only START the assign path if we can FINISH it. Without the quick exploration
                // text we would open the venture list and stall there with a window we cannot use,
                // which looks far more broken than leaving an idle retainer alone.
                if (!string.IsNullOrWhiteSpace(text.QuickExploration))
                {
                    var assign = IndexOf(entries, text.AssignIdle);
                    if (assign >= 0)
                        return new VentureDecision(VentureAction.SelectEntry, assign, "opening the venture list");
                }

                // Already out. Clicking the in-progress entry would assign a REPLACEMENT venture
                // and charge for it again, so this retainer is finished as far as we are concerned.
                if (IndexOf(entries, text.AssignInProgress) >= 0)
                    return VentureDecision.Nothing("done — already out on a venture");

                return VentureDecision.Nothing("nothing to do for this retainer");

            default:
                return VentureDecision.Nothing("unknown screen");
        }
    }

    /// <summary>
    /// Entry match. Blank sheet text NEVER matches — an unread sheet row would otherwise match every
    /// entry and click the first thing in the menu, which is how you hand over gil by accident.
    /// </summary>
    private static int IndexOf(IReadOnlyList<string> entries, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return -1;

        for (var i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i], target, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
