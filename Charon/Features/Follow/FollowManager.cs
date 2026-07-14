using System;
using System.Numerics;

namespace Charon.Features.Follow;

/// <summary>Immutable snapshot of the follow settings, rebuilt from <see cref="CharonConfig"/>.</summary>
public sealed record FollowConfig(float FollowDistance, bool StopInBossFight)
{
    public static FollowConfig From(CharonConfig config) => new(config.FollowDistance, config.FollowStopInBossFight);
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
/// </summary>
public sealed class FollowManager
{
    /// <summary>Hysteresis: start moving only past FollowDistance + this, so tiny leader shifts don't twitch.</summary>
    internal const float MoveDeadband = 1.5f;

    private FollowConfig _config;

    public FollowManager(FollowConfig config) => _config = config;

    /// <summary>Leader we're following ("" = not following).</summary>
    public string LeaderName { get; private set; } = string.Empty;

    public bool Following => LeaderName.Length > 0;

    public void UpdateConfig(FollowConfig config) => _config = config;

    public void StartFollowing(string leaderName) => LeaderName = leaderName?.Trim() ?? string.Empty;

    public void Stop() => LeaderName = string.Empty;

    /// <param name="leaderPos">Leader's world position, or null when not resolvable (out of zone/range).</param>
    /// <param name="selfPos">Local player's world position.</param>
    /// <param name="inCombat">Local player is in combat.</param>
    /// <param name="hasActiveModule">A BMR boss module is active (its StateMachine has an active state).</param>
    /// <param name="localBusy">Local player can't be driven right now (dead, cutscene, zoning, being carried, pillion-boarding).</param>
    public FollowDecision Evaluate(Vector3? leaderPos, Vector3 selfPos, bool inCombat, bool hasActiveModule, bool localBusy)
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

        var distance = Horizontal(selfPos, leaderPos.Value);
        if (distance <= _config.FollowDistance + MoveDeadband)
            return new FollowDecision(FollowAction.Hold, default, $"following {LeaderName} ({distance:F1}y)");

        return new FollowDecision(FollowAction.Move, leaderPos.Value, $"following {LeaderName} ({distance:F1}y)");
    }

    /// <summary>XZ-plane distance — leaders may sit above/below on ramps and mounts.</summary>
    private static float Horizontal(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
