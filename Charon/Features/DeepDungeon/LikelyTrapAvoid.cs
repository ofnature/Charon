using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Charon.Features.Follow;

namespace Charon.Features.DeepDungeon;

/// <summary>
/// Which likely trap spots a walking route is asked to keep clear of, and how they are handed to the
/// pathfinder (requested 2026-11-22: "navigating around trap locations and potential trap locations").
/// Pure.
///
/// A REVEALED trap is a fact and the pathfinder already goes round it. A LIKELY spot is history — a place
/// a trap has stood before on this floor set — so it is only ever a preference: the pathfinder is asked
/// to keep off it where there is another way, and walks through where there is none (a corridor with a
/// spot in it is still the only way on). That is why these go out as "avoid" circles, never as walls.
/// </summary>
public static class LikelyTrapAvoid
{
    /// <summary>The same clearance a route keeps from a revealed trap.</summary>
    public const float Radius = TrapRoute.Clearance;

    /// <summary>Spots further than this from the player are not sent: no route planned now reaches them.</summary>
    public const float Range = 60f;

    /// <summary>
    /// The most circles sent at once. The older dungeons carry a thousand imported spots per floor set;
    /// the nearest ones are the ones a route can meet, and a short list keeps each request small.
    /// </summary>
    public const int MaxCircles = 32;

    /// <summary>
    /// The spots as one flat list — x, y, z, radius for each — nearest to <paramref name="me"/> first,
    /// at most <see cref="MaxCircles"/> of them.
    /// </summary>
    public static float[] Circles(IEnumerable<Vector3> spots, Vector3 me)
    {
        var nearest = spots
            .Where(s => Vector3.DistanceSquared(s, me) <= Range * Range)
            .OrderBy(s => Vector3.DistanceSquared(s, me))
            .Take(MaxCircles);

        var flat = new List<float>();
        foreach (var spot in nearest)
        {
            flat.Add(spot.X);
            flat.Add(spot.Y);
            flat.Add(spot.Z);
            flat.Add(Radius);
        }

        return [.. flat];
    }

    /// <summary>How many circles a flat list holds.</summary>
    public static int Count(float[] circles) => circles.Length / 4;
}
