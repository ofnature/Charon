using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Fleet;

/// <summary>Whether to hand party leadership back to the fleet leader, and why not if not.</summary>
public sealed record PromoteDecision(bool Promote, string Reason);

/// <summary>
/// Decides whether to promote the fleet leader back to party leader. Pure logic.
///
/// The case this exists for: when a player disconnects the game keeps them in the party but moves
/// leadership to someone else — usually a bot. They reconnect and are no longer leading their own
/// fleet, which quietly breaks anything that depends on the leader holding leadership.
///
/// Only the CURRENT party leader can promote, so this runs on whichever box inherited it. A bot
/// holding leadership hands it back; a box that isn't leading can do nothing but wait.
/// </summary>
public static class PartyLeaderPolicy
{
    /// <param name="localIsPartyLeader">This character currently holds party leadership.</param>
    /// <param name="fleetLeader">Configured fleet leader (see <see cref="FleetLeaderPolicy"/>).</param>
    /// <param name="partyMembers">Current party, including ourselves.</param>
    /// <param name="isOnline">LAN-roster liveness test; unknown toons are treated as online.</param>
    public static PromoteDecision Evaluate(
        bool enabled,
        bool localIsPartyLeader,
        string fleetLeader,
        string localName,
        IReadOnlyList<string> partyMembers,
        Func<string, bool> isOnline)
    {
        if (!enabled)
            return new PromoteDecision(false, "disabled");

        if (fleetLeader.Length == 0)
            return new PromoteDecision(false, "no fleet leader set");

        // Nobody can promote themselves. If we ARE the fleet leader we either already hold it or we
        // are waiting for whichever box inherited leadership to hand it back.
        if (localName.Length > 0 && fleetLeader.Equals(localName, StringComparison.OrdinalIgnoreCase))
            return new PromoteDecision(false, localIsPartyLeader
                ? "we are the fleet leader and hold party lead"
                : "waiting — another box holds party lead");

        if (!localIsPartyLeader)
            return new PromoteDecision(false, "not party leader — cannot promote");

        if (!partyMembers.Any(m => m.Equals(fleetLeader, StringComparison.OrdinalIgnoreCase)))
            return new PromoteDecision(false, $"{fleetLeader} is not in this party");

        // A disconnected leader stays in the party, so presence alone doesn't mean they're back.
        if (!isOnline(fleetLeader))
            return new PromoteDecision(false, $"waiting — {fleetLeader} is still offline");

        return new PromoteDecision(true, $"promoting {fleetLeader} back to party leader");
    }
}
