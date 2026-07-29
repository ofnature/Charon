using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Fleet;

/// <summary>Why a fleet leave-duty command was or wasn't obeyed — surfaced in the Debug section.</summary>
public sealed record DutyExitDecision(bool Leave, string Reason);

/// <summary>
/// Decides whether to obey a "fleet leader says leave the duty" command. Pure logic — no Dalamud
/// types.
///
/// Explicitly commanded, never inferred: a leader's territory changing cannot distinguish leaving a
/// duty from disconnecting, and the two want opposite responses (a disconnect keeps them in the
/// party and reassigns leadership, so the run may well continue; leaving is deliberate and final).
///
/// Three gates that matter more than the convenience:
/// - The command must come from the CONFIGURED fleet leader. Any other toon — an alt that somehow
///   published, or a stale frame — is ignored, so one stray broadcast cannot empty a duty.
/// - The leader must be IN OUR PARTY. The relay reaches every box on the LAN, including toons off
///   running a different dungeon; the command is only ours to obey if we're grouped with the leader.
/// - Every other party member must be a trusted LAN toon. In a matched duty this means the fleet
///   never walks out on strangers (which earns a penalty), regardless of what the relay says.
/// </summary>
public static class DutyExitPolicy
{
    /// <summary>
    /// <paramref name="commandLeader"/> is the issuer from the relay frame; <paramref name="fleetLeader"/>
    /// is this box's configured fleet leader. <paramref name="partyMembers"/> includes ourselves.
    /// </summary>
    public static DutyExitDecision Evaluate(
        bool enabled,
        bool boundByDuty,
        string commandLeader,
        string fleetLeader,
        string localName,
        IReadOnlyList<string> partyMembers,
        Func<string, bool> isTrusted)
    {
        if (!enabled)
            return new DutyExitDecision(false, "disabled");

        if (fleetLeader.Length == 0)
            return new DutyExitDecision(false, "no fleet leader set");

        if (!commandLeader.Equals(fleetLeader, StringComparison.OrdinalIgnoreCase))
            return new DutyExitDecision(false, $"ignored — '{commandLeader}' is not the fleet leader");

        // The leader's own box handles its exit locally (the relay never delivers to its publisher),
        // so a frame naming us is either a loop or a misconfiguration. Either way, do nothing.
        if (localName.Length > 0 && commandLeader.Equals(localName, StringComparison.OrdinalIgnoreCase))
            return new DutyExitDecision(false, "ignored — that is us");

        if (!boundByDuty)
            return new DutyExitDecision(false, "not in a duty");

        // Scope the command to the LEADER'S OWN PARTY. The relay is a LAN broadcast, so it also
        // reaches toons off doing their own thing — a separate group of bots running a different
        // dungeon passes every other gate here (all trusted, same configured leader) and would walk
        // out of a run nobody asked it to leave. Being grouped with the leader is what makes the
        // command ours to obey.
        if (!partyMembers.Any(m => m.Equals(commandLeader, StringComparison.OrdinalIgnoreCase)))
            return new DutyExitDecision(false, $"not in {commandLeader}'s party — staying");

        var strangers = partyMembers
            .Where(m => m.Length > 0
                        && !m.Equals(localName, StringComparison.OrdinalIgnoreCase)
                        && !isTrusted(m))
            .ToList();

        if (strangers.Count > 0)
            return new DutyExitDecision(false, $"held — {strangers.Count} non-fleet member(s) in the party");

        return new DutyExitDecision(true, "leaving duty (fleet leader)");
    }
}
