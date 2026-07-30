using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Fleet;

/// <summary>
/// One party member as the promote logic needs to see them. <paramref name="Slot"/> is the 1-based
/// party position, which is what the game's placeholder syntax (<c>&lt;1&gt;</c>…<c>&lt;8&gt;</c>)
/// addresses.
/// </summary>
public sealed record FleetPartyMember(string Name, int Slot, bool SameZone);

/// <summary>Whether to hand party leadership back, and why not if not.</summary>
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
///
/// SAME ZONE is required: the game refuses <c>/leader</c> for a member who isn't here ("unavailable
/// at this time"), and a party list shows out-of-zone members with unknown level and HP. Verified
/// in-game — attempting it cross-zone just spams chat errors.
/// </summary>
public static class PartyLeaderPolicy
{
    /// <param name="localIsPartyLeader">This character currently holds party leadership.</param>
    /// <param name="fleetLeader">Configured fleet leader (see <see cref="FleetLeaderPolicy"/>).</param>
    /// <param name="party">Current party, including ourselves, with slots and zone presence.</param>
    /// <param name="isOnline">LAN-roster liveness test; unknown toons are treated as online.</param>
    public static PromoteDecision Evaluate(
        bool enabled,
        bool localIsPartyLeader,
        string fleetLeader,
        string localName,
        IReadOnlyList<FleetPartyMember> party,
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

        var target = party.FirstOrDefault(m => m.Name.Equals(fleetLeader, StringComparison.OrdinalIgnoreCase));
        if (target == null)
            return new PromoteDecision(false, $"{fleetLeader} is not in this party");

        // A disconnected toon STAYS in the party, so presence alone doesn't mean they're back.
        if (!isOnline(target.Name))
            return new PromoteDecision(false, $"waiting — {fleetLeader} is still offline");

        if (!target.SameZone)
            return new PromoteDecision(false, $"waiting — {fleetLeader} is in another zone");

        return new PromoteDecision(true, $"promoting {fleetLeader} back to party leader");
    }
}
