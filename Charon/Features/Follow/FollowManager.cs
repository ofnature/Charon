using System;
using System.Numerics;

namespace Charon.Features.Follow;

/// <summary>Immutable snapshot of the follow settings, rebuilt from <see cref="CharonConfig"/>.</summary>
public sealed record FollowConfig(float FollowDistance, bool StopInBossFight, float CombatLeash)
{
    public static FollowConfig From(CharonConfig config) =>
        new(config.FollowDistance, config.FollowStopInBossFight, config.FollowCombatLeash);
}

/// <summary>What the follower should do this tick.</summary>
public enum FollowAction
{
    /// <summary>No leader set — nothing to do.</summary>
    Idle,

    /// <summary>Following, but not moving this tick (arrived, gated, or leader absent). Release any path.</summary>
    Hold,

    /// <summary>Path toward <see cref="FollowDecision.Target"/>.</summary>
    Move,
}

public sealed record FollowDecision(FollowAction Action, Vector3 Target, string Status);

/// <summary>
/// Decides whether a commanded toon should move toward its leader this tick. Pure logic — no
/// Dalamud types (Vector3 is System.Numerics). BMR-style: the follow session persists through
/// every pause and RESUMES automatically when the gate clears (the caller re-evaluates each
/// tick), ending only on an explicit Stop.
///
/// Hard gate (BMR parity, refined per user): pause only when IN COMBAT **and** a boss module is
/// loaded (both true) — pre-pull (module loaded, not engaged) and normal non-boss combat keep
/// following. The instant the boss aggroes, the caller releases movement so BMR's AI takes over.
///
/// Ordinary combat is not gated, but it IS given slack — see the flex leash in
/// <see cref="Evaluate"/>. Heeling a melee toon to 2.5y while the leader moves means it never
/// stays on its target long enough to attack.
/// </summary>
public sealed class FollowManager
{
    /// <summary>Hysteresis: start moving only past FollowDistance + this, so tiny leader shifts don't twitch.</summary>
    internal const float MoveDeadband = 1.5f;

    /// <summary>
    /// A leader position change larger than this in one tick isn't walking — it's a portal,
    /// teleport stone, lift or other interact-object relocation. Used to force an immediate
    /// reachability re-check instead of blindly pathing at the new spot.
    /// </summary>
    internal const float TeleportJumpYalms = 30f;

    /// <summary>
    /// How close the leader must have been standing to US for their DISAPPEARANCE to read as a
    /// transition rather than simply walking out of range.
    ///
    /// A Spatial Rift (and any portal that moves you clean across a large zone) drops the leader
    /// out of the object table entirely, so there is no "jump" to measure — one tick they are
    /// beside you, the next they do not exist. Someone who walked out of range was far away and
    /// getting farther first; someone who was 5y from you and then gone did not walk anywhere.
    /// </summary>
    internal const float VanishNearYalms = 30f;

    private FollowConfig _config;
    private Vector3? _lastLeaderPos;

    /// <summary>
    /// True while we are closing a leash break in combat. The leash needs hysteresis: if breaking
    /// out at the leash distance also stopped at the leash distance, a follower parked exactly at
    /// the boundary would start and stop every tick. Once the leash breaks we close all the way to
    /// the normal follow distance, then go slack again.
    /// </summary>
    private bool _closingLeash;

    /// <summary>
    /// Where the leader stood JUST BEFORE a detected teleport jump — i.e. the spot they walked
    /// to and clicked. The portal/lift they used is right there, so this is a precise hint for
    /// "which object do I need to interact with to follow them", far safer than guessing at
    /// whatever interactable happens to be near us. Null when no jump is pending.
    /// </summary>
    public Vector3? PortalHint { get; private set; }

    /// <summary>Clear the portal hint (leader reachable again, or we took the portal).</summary>
    public void ClearPortalHint() => PortalHint = null;

    public FollowManager(FollowConfig config) => _config = config;

    /// <summary>Leader we're following ("" = not following).</summary>
    public string LeaderName { get; private set; } = string.Empty;

    public bool Following => LeaderName.Length > 0;

    public void UpdateConfig(FollowConfig config) => _config = config;

    public void StartFollowing(string leaderName)
    {
        LeaderName = leaderName?.Trim() ?? string.Empty;
        _lastLeaderPos = null;
        PortalHint = null;
        _closingLeash = false;
    }

    public void Stop()
    {
        LeaderName = string.Empty;
        _lastLeaderPos = null;
        PortalHint = null;
        _closingLeash = false;
    }

    /// <summary>
    /// Feed the leader's position each tick. Returns true when they TRANSITIONED — took a portal,
    /// spatial rift, teleport stone or lift rather than walking — so the caller should re-check
    /// reachability immediately instead of trusting a cached result.
    ///
    /// Two shapes, because a transition does not always leave the leader visible:
    /// - they jump more than <see cref="TeleportJumpYalms"/> in one tick (short hop, same map);
    /// - they VANISH from the object table while standing right next to us, which is what a rift
    ///   across a large zone looks like. That case used to read as "walked out of range" and was
    ///   ignored, so the follower stood there reporting "not in zone" forever.
    /// </summary>
    /// <param name="selfPos">Our own position — the yardstick for whether a disappearance is credible.</param>
    public bool NoteLeaderPosition(Vector3? leaderPos, Vector3 selfPos)
    {
        var previous = _lastLeaderPos;
        _lastLeaderPos = leaderPos;

        if (previous == null)
            return false; // first sighting — nothing to compare

        if (leaderPos == null)
        {
            // Gone from the object table. Only a disappearance from CLOSE BY is a transition; from
            // far away it is ordinary render-range loss and clicking things would be a guess.
            // Fires once: _lastLeaderPos is now null, so the next tick takes the branch above.
            if (Vector3.Distance(previous.Value, selfPos) > VanishNearYalms)
                return false;

            PortalHint = previous;
            return true;
        }

        if (Vector3.Distance(previous.Value, leaderPos.Value) <= TeleportJumpYalms)
            return false;

        PortalHint = previous; // they clicked something right here
        return true;
    }

    /// <param name="leaderPos">Leader's world position, or null when not resolvable (out of zone/range).</param>
    /// <param name="selfPos">Local player's world position.</param>
    /// <param name="inCombat">Local player is in combat.</param>
    /// <param name="hasActiveModule">A BMR boss module is active (its StateMachine has an active state).</param>
    /// <param name="localBusy">Local player can't be driven right now (dead, cutscene, zoning, being carried, pillion-boarding).</param>
    /// <param name="leaderReachable">Navmesh says we can actually walk there (false = portal/disconnected island).</param>
    public FollowDecision Evaluate(Vector3? leaderPos, Vector3 selfPos, bool inCombat, bool hasActiveModule,
        bool localBusy, bool leaderReachable = true)
    {
        if (!Following)
            return new FollowDecision(FollowAction.Idle, default, "idle");

        if (localBusy)
            return new FollowDecision(FollowAction.Hold, default, "paused");

        // The one hard gate: in an actual boss fight, hand movement to BMR.
        if (_config.StopInBossFight && inCombat && hasActiveModule)
            return new FollowDecision(FollowAction.Hold, default, "holding — boss fight (BMR has movement)");

        if (leaderPos == null)
            return new FollowDecision(FollowAction.Hold, default, $"waiting — {LeaderName} not in zone");

        // Portal case: visible on the map but on a disconnected navmesh island. Walking at it
        // forever helps nobody — hold and say so; re-checked each tick, so coming back resumes.
        if (!leaderReachable)
            return new FollowDecision(FollowAction.Hold, default,
                $"waiting — {LeaderName} unreachable (portal/instance?)");

        var distance = Horizontal(selfPos, leaderPos.Value);
        var arriveAt = _config.FollowDistance + MoveDeadband;

        // Flex leash: in ordinary (non-boss) combat, stop heeling the toon. Trailing 2.5y behind a
        // moving leader drags a melee off its target and it never lands a hit. Instead go slack out
        // to the leash distance and let it fight; only a leader genuinely leaving pulls it along.
        // A leash tighter than the follow distance would be meaningless, so it never goes below it.
        var leash = MathF.Max(_config.CombatLeash, arriveAt);
        var slack = inCombat && leash > arriveAt;

        if (!inCombat)
            _closingLeash = false; // combat over — back to tight follow

        // While slack and not already closing, only the leash counts; once broken, close fully.
        var threshold = slack && !_closingLeash ? leash : arriveAt;

        // Hold and Move MUST NOT read the same. They used to both say "following X (12.3y)", so a
        // follower that had decided to move but wasn't actually moving was indistinguishable from
        // one correctly standing still — which is the whole question when it stops keeping up.
        if (distance <= threshold)
        {
            _closingLeash = false;
            return new FollowDecision(FollowAction.Hold, default, slack
                ? $"in combat — holding position, {LeaderName} within leash ({distance:F1}/{leash:F0}y)"
                : $"in position — {LeaderName} {distance:F1}y away");
        }

        if (slack)
            _closingLeash = true;

        return new FollowDecision(FollowAction.Move, leaderPos.Value, slack
            ? $"closing — {LeaderName} left the leash ({distance:F1}y)"
            : $"moving to {LeaderName} ({distance:F1}y)");
    }

    /// <summary>XZ-plane distance — leaders may sit above/below on ramps and mounts.</summary>
    private static float Horizontal(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
