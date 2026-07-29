using System;

namespace Charon.Features.Fleet;

/// <summary>Whether to adopt a broadcast fleet-leader designation, and why not if not.</summary>
public sealed record LeaderChangeDecision(bool Accept, string Reason);

/// <summary>
/// Decides whether to adopt a fleet-leader designation broadcast by another box. Pure logic.
///
/// Designating the leader on one box and having the rest follow is the whole point — setting the
/// same name by hand on eight clients is exactly the sort of chore that ends up half-done, leaving
/// boxes that silently ignore fleet commands.
///
/// The trust gate is what keeps that safe: only a toon on the LAN roster (or the manual whitelist)
/// can nominate, and only a roster toon can BE nominated. A malformed or unknown frame changes
/// nothing.
/// </summary>
public static class FleetLeaderPolicy
{
    /// <param name="sender">Character that sent the designation.</param>
    /// <param name="nominee">Character being designated (may differ from the sender).</param>
    /// <param name="localName">This character — never used to reject, only to allow self-nomination.</param>
    /// <param name="currentLeader">Leader configured on this box right now.</param>
    /// <param name="isTrusted">LAN roster + whitelist membership test.</param>
    public static LeaderChangeDecision Evaluate(
        string sender,
        string nominee,
        string localName,
        string currentLeader,
        Func<string, bool> isTrusted)
    {
        if (sender.Length == 0 || nominee.Length == 0)
            return new LeaderChangeDecision(false, "ignored — malformed designation");

        if (!isTrusted(sender))
            return new LeaderChangeDecision(false, $"ignored — '{sender}' is not a fleet toon");

        // The nominee must be someone we recognise, or we would end up obeying a name that can
        // never match a real toon. Nominating ourselves is always fine.
        var nomineeKnown = isTrusted(nominee)
                           || (localName.Length > 0 && nominee.Equals(localName, StringComparison.OrdinalIgnoreCase));
        if (!nomineeKnown)
            return new LeaderChangeDecision(false, $"ignored — '{nominee}' is not a fleet toon");

        if (nominee.Equals(currentLeader, StringComparison.OrdinalIgnoreCase))
            return new LeaderChangeDecision(false, "already the fleet leader");

        return new LeaderChangeDecision(true, $"fleet leader set to {nominee} (by {sender})");
    }
}
