using System;
using System.Collections.Generic;

namespace Charon.Features.Spawns;

/// <summary>A nearby mob as the scanner reports it — no Dalamud types cross this line.</summary>
public readonly record struct NearbyMob(uint EntityId, string Name, float Distance);

/// <summary>One watched mob, the moment it was first seen.</summary>
public sealed record SpawnSighting(uint EntityId, string Name, DateTime SeenUtc, float Distance, ushort Territory);

/// <summary>
/// Watches for mobs on a name watchlist appearing near us. Pure logic — the adapter hands it a
/// list of nearby mobs each scan and it answers which of them are NEW.
///
/// "New" means an entity id we have not logged since the last <see cref="Reset"/> (a zone change).
/// A client genuinely cannot tell a fresh spawn from a mob that merely streamed into render range
/// — both look identical from the object table — so the window says as much rather than the code
/// pretending otherwise. Each entity id is announced ONCE, which is what keeps walking back and
/// forth past the same mob from filling the log.
/// </summary>
public sealed class SpawnWatcher
{
    /// <summary>Sightings kept; oldest fall off. A log, not a database.</summary>
    public const int MaxHistory = 200;

    private readonly HashSet<uint> _logged = new();
    private readonly List<SpawnSighting> _history = new();

    /// <summary>Newest first.</summary>
    public IReadOnlyList<SpawnSighting> History => _history;

    /// <summary>Case-insensitive substring match, so "croakadile" finds "Croakadile".</summary>
    public static bool IsWatched(string name, IReadOnlyList<string> watchlist)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        foreach (var entry in watchlist)
        {
            if (!string.IsNullOrWhiteSpace(entry)
                && name.Contains(entry.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Record any watched mob we have not logged yet and return just those. Adding a name to the
    /// watchlist therefore reports mobs already standing nearby, which answers "is it up?" as
    /// well as "did it spawn?".
    /// </summary>
    public IReadOnlyList<SpawnSighting> Observe(
        IReadOnlyList<NearbyMob> nearby, IReadOnlyList<string> watchlist, DateTime nowUtc, ushort territory)
    {
        List<SpawnSighting>? fresh = null;

        foreach (var mob in nearby)
        {
            if (!IsWatched(mob.Name, watchlist) || !_logged.Add(mob.EntityId))
                continue;

            var sighting = new SpawnSighting(mob.EntityId, mob.Name, nowUtc, mob.Distance, territory);
            _history.Insert(0, sighting);
            (fresh ??= new List<SpawnSighting>()).Add(sighting);
        }

        if (_history.Count > MaxHistory)
            _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);

        return (IReadOnlyList<SpawnSighting>?)fresh ?? Array.Empty<SpawnSighting>();
    }

    /// <summary>Forget what we have already logged — entity ids only mean anything within a zone.</summary>
    public void Reset() => _logged.Clear();

    public void ClearHistory()
    {
        _history.Clear();
        _logged.Clear();
    }
}
