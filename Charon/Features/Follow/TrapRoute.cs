using System;
using System.Collections.Generic;
using System.Numerics;

namespace Charon.Features.Follow;

/// <summary>
/// Keeping a follower's route off revealed deep-dungeon traps. Pure — System.Numerics only.
///
/// The idea is Deep Ducking's (Aethertek's deep dungeon plugin — product page only, its automation is
/// private): revealed traps are left out of walking routes, and a route with no safe way round is
/// REPORTED rather than walked silently. Charon already knows every revealed trap (they are in the
/// object table once Sight shows them) and plans with vnavmesh/Ariadne, which return a route as
/// waypoints; this answers the geometric questions about that route. Distances are measured on the
/// ground plane (X/Z) with a height band, because a trap one floor up or down is no threat.
/// </summary>
public static class TrapRoute
{
    /// <summary>How close a route may pass a revealed trap: its marker radius (1.7y) plus a body and a margin.</summary>
    public const float Clearance = 2.5f;

    /// <summary>A trap this far above or below the route is on another level.</summary>
    public const float HeightBand = 3f;

    /// <summary>The first leg of <paramref name="path"/> that passes within <paramref name="clearance"/> of a trap.</summary>
    public static (int Leg, Vector3 Trap)? FirstCrossing(IReadOnlyList<Vector3> path, IReadOnlyList<Vector3> traps,
        float clearance = Clearance)
    {
        for (var i = 0; i + 1 < path.Count; i++)
        {
            foreach (var trap in traps)
            {
                if (DistanceToLeg(trap, path[i], path[i + 1]) < clearance && WithinHeight(trap, path[i], path[i + 1]))
                    return (i, trap);
            }
        }

        return null;
    }

    /// <summary>Ground-plane distance from <paramref name="p"/> to the leg a→b.</summary>
    public static float DistanceToLeg(Vector3 p, Vector3 a, Vector3 b)
    {
        var a2 = new Vector2(a.X, a.Z);
        var ab = new Vector2(b.X, b.Z) - a2;
        var ap = new Vector2(p.X, p.Z) - a2;
        var lengthSquared = ab.LengthSquared();
        var t = lengthSquared < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(ap, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(a2 + ab * t, new Vector2(p.X, p.Z));
    }

    /// <summary>
    /// Two points to pass the trap by, one on each side of the leg a→b, <paramref name="offset"/> from
    /// the trap at right angles to the leg — the nearer side to where the leg already runs comes first.
    /// </summary>
    public static (Vector3 First, Vector3 Second) Sidesteps(Vector3 a, Vector3 b, Vector3 trap, float offset)
    {
        var along = new Vector2(b.X - a.X, b.Z - a.Z);
        along = along.LengthSquared() < 1e-6f ? Vector2.UnitX : Vector2.Normalize(along);
        var across = new Vector2(-along.Y, along.X);

        var left = new Vector3(trap.X + across.X * offset, trap.Y, trap.Z + across.Y * offset);
        var right = new Vector3(trap.X - across.X * offset, trap.Y, trap.Z - across.Y * offset);

        // Which side of the trap the leg passes on: keep to it, it is the shorter way round.
        var legSide = (a.X - trap.X) * across.X + (a.Z - trap.Z) * across.Y
                      + (b.X - trap.X) * across.X + (b.Z - trap.Z) * across.Y;
        return legSide >= 0 ? (left, right) : (right, left);
    }

    /// <summary>The path with <paramref name="via"/> inserted after waypoint <paramref name="leg"/>.</summary>
    public static List<Vector3> Splice(IReadOnlyList<Vector3> path, int leg, Vector3 via)
    {
        var spliced = new List<Vector3>(path.Count + 1);
        for (var i = 0; i < path.Count; i++)
        {
            spliced.Add(path[i]);
            if (i == leg)
                spliced.Add(via);
        }

        return spliced;
    }

    /// <summary>
    /// The path cut so it ends <paramref name="range"/> short of its last point, measured along the path —
    /// a follower stops at follow distance, it does not walk into the leader.
    /// </summary>
    public static List<Vector3> StopShort(IReadOnlyList<Vector3> path, float range)
    {
        var result = new List<Vector3>(path);
        var remaining = range;
        while (result.Count >= 2 && remaining > 0)
        {
            var last = result[^1];
            var before = result[^2];
            var length = Vector3.Distance(before, last);
            if (length <= remaining)
            {
                result.RemoveAt(result.Count - 1);
                remaining -= length;
                continue;
            }

            result[^1] = Vector3.Lerp(last, before, remaining / length);
            break;
        }

        return result;
    }

    private static bool WithinHeight(Vector3 trap, Vector3 a, Vector3 b) =>
        trap.Y >= Math.Min(a.Y, b.Y) - HeightBand && trap.Y <= Math.Max(a.Y, b.Y) + HeightBand;
}
