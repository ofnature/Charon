using System;
using System.Collections.Generic;

namespace Charon.Features.AutoAccept;

/// <summary>
/// Decides whether a duty pop may be commenced automatically. Pure logic — no Dalamud types.
///
/// The rule is deliberately strict: only when this is OUR fleet queueing together, i.e. we are
/// in a real party (2+) and EVERY other member is a trusted LAN toon. A party containing anyone
/// untrusted — or a solo/matched queue, where the pop arrives before the party forms — is never
/// auto-commenced, so a random Duty Finder pop always waits for the player.
/// </summary>
public static class DutyAcceptPolicy
{
    public static bool ShouldAutoCommence(
        bool enabled,
        IReadOnlyList<string> partyMemberNames,
        string localName,
        Func<string, bool> isTrusted)
    {
        if (!enabled || partyMemberNames.Count < 2)
            return false;

        var others = 0;
        foreach (var name in partyMemberNames)
        {
            if (name.Length == 0)
                return false; // unreadable member — refuse rather than guess
            if (name.Equals(localName, StringComparison.OrdinalIgnoreCase))
                continue;

            others++;
            if (!isTrusted(name))
                return false; // a stranger in the party — never auto-commence
        }

        return others > 0;
    }
}
