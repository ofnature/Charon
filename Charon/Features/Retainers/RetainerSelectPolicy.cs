namespace Charon.Features.Retainers;

/// <summary>What to do next to get the wanted retainer's BAGS open.</summary>
public enum SelectAction
{
    /// <summary>Nothing to do: nothing is wanted, or we are not at a bell.</summary>
    None,

    /// <summary>The wanted retainer's bags are open — the pass that asked for it can work.</summary>
    Ready,

    /// <summary>The wrong retainer's bags are open; close them before anything else.</summary>
    CloseItems,

    /// <summary>Standing in the right retainer's menu: open their items.</summary>
    OpenItems,

    /// <summary>Standing in the WRONG retainer's menu: leave it so the list comes back.</summary>
    QuitOpen,

    /// <summary>The list is up: choose the wanted one.</summary>
    Select,
}

public sealed record SelectDecision(SelectAction Action, string Retainer, string Reason)
{
    public static SelectDecision Nothing(string reason) => new(SelectAction.None, string.Empty, reason);
}

/// <summary>
/// Decides the single next step toward having a particular retainer's BAGS open, at a bell the
/// player is ALREADY standing at. Pure — no Dalamud types.
///
/// Every retainer is reachable from one bell, so consolidating two of them is select A, withdraw,
/// quit, select B, deposit — one session, no walking. The piece worth automating is therefore the
/// SELECTING, and the doctrine that Charon never walks a character to a bell is untouched: with no
/// bell at all this returns None forever.
///
/// THE FULL CYCLE, learned at a real bell: selecting a retainer CLOSES the list and opens their
/// MENU — and a menu is not bags. Their contents only become readable once "Entrust or withdraw
/// items" is chosen, so the cycle is select → open items → (read) → close items → quit → list. A
/// version that stopped at "selected" reported "waiting for a bell" while standing in the menu.
///
/// One step per call, re-decided from live state, so closing the window or switching the toggle off
/// ends it on the next tick.
/// </summary>
public static class RetainerSelectPolicy
{
    public static SelectDecision Decide(
        bool enabled,
        string? wanted,
        string? openRetainer,
        string? menuRetainer,
        bool listVisible,
        bool tidyUp = false)
    {
        if (!enabled)
            return SelectDecision.Nothing("off");

        if (string.IsNullOrWhiteSpace(wanted))
        {
            // The pass is finished. If WE drove this session, put things back the way we found them:
            // close the last retainer's bags and quit, so the player is left at the list, not in
            // someone's inventory. A session the player drove themselves is never touched.
            if (tidyUp && !string.IsNullOrWhiteSpace(openRetainer))
                return new SelectDecision(SelectAction.CloseItems, openRetainer!, $"finished — closing {openRetainer}'s bags");

            if (tidyUp && !string.IsNullOrWhiteSpace(menuRetainer))
                return new SelectDecision(SelectAction.QuitOpen, menuRetainer!, $"finished — leaving {menuRetainer}");

            return SelectDecision.Nothing("nothing is waiting on a retainer");
        }

        // Bags open on the right one: the fetch or refresh pass owns it from here.
        if (Same(openRetainer, wanted))
            return new SelectDecision(SelectAction.Ready, wanted!, $"{wanted} is open");

        // Bags open on someone else — close them before the menu can be used.
        if (!string.IsNullOrWhiteSpace(openRetainer))
            return new SelectDecision(SelectAction.CloseItems, openRetainer!, $"closing {openRetainer}'s bags");

        // In the right retainer's menu: their items are the next click, and the step that was
        // missing — being in the menu is not the same as being in the bags.
        if (Same(menuRetainer, wanted))
            return new SelectDecision(SelectAction.OpenItems, wanted!, $"opening {wanted}'s items");

        // In the wrong retainer's menu: leave, so the list comes back.
        if (!string.IsNullOrWhiteSpace(menuRetainer))
            return new SelectDecision(SelectAction.QuitOpen, menuRetainer!, $"leaving {menuRetainer}");

        if (!listVisible)
            return SelectDecision.Nothing($"waiting for a bell — {wanted} is needed");

        return new SelectDecision(SelectAction.Select, wanted!, $"opening {wanted}");
    }

    /// <summary>
    /// Did the selection LAND — is the retainer now in front of us the one that was chosen?
    /// Any retainer appearing is not enough: the PREVIOUS one lingers for a moment after being
    /// dismissed, and counting her as the new arrival let a second Select fire 0.6s after the first.
    /// </summary>
    public static bool SelectionLanded(string? selecting, string? openRetainer, string? atBell) =>
        Same(openRetainer, selecting) || Same(atBell, selecting);

    /// <summary>
    /// Is the bell back the way we found it, so a session we drove is over? Only when the LIST is
    /// up again. "Nothing of a retainer's is open" is also true for the instant between their bags
    /// closing and their menu returning, and ending the session there stranded the player in the
    /// last retainer's menu — a transient nothing read as a definitive none, again.
    /// </summary>
    public static bool BackAtTheList(bool listVisible, string? openRetainer, string? menuRetainer) =>
        listVisible && string.IsNullOrWhiteSpace(openRetainer) && string.IsNullOrWhiteSpace(menuRetainer);

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
}
