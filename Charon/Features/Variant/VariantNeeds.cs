using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Variant;

/// <summary>One toon's route record: the game's bit array as it last reported it.</summary>
/// <param name="Local">This client's own toon — read live, never relayed.</param>
public sealed record ToonRecord(string Name, byte[] Bits, DateTime SeenUtc, bool Local);

/// <summary>
/// The fleet's records, one per toon, as the LAN relay delivered them. Pure — no Dalamud types.
/// A record only ever GROWS in the game (a finished route cannot be unfinished), so an old record is still
/// true as far as it goes; it is kept with its age rather than expired, and the UI shows the age.
/// </summary>
public sealed class FleetVariantRecords
{
    private readonly Dictionary<string, ToonRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Store a toon's record; true when it is new or different from the one held.</summary>
    public bool Upsert(string name, byte[] bits, DateTime nowUtc)
    {
        if (name.Length == 0)
            return false;

        var changed = !_records.TryGetValue(name, out var held) || !held.Bits.AsSpan().SequenceEqual(bits);
        _records[name] = new ToonRecord(name, bits, nowUtc, Local: false);
        return changed;
    }

    public ToonRecord? Get(string name) => _records.TryGetValue(name, out var record) ? record : null;

    public IReadOnlyCollection<ToonRecord> All => _records.Values;

    public int Count => _records.Count;
}

/// <summary>Which routes of one dungeon a toon still needs, grouped by the fork they start from.</summary>
public sealed record ToonNeeds(ToonRecord Toon, IReadOnlyDictionary<VariantPath, IReadOnlyList<int>> ByPath)
{
    public int Total => ByPath.Values.Sum(r => r.Count);
}

/// <summary>
/// Who needs what: a route is worth running for the toons whose records do not have it yet. Pure.
/// </summary>
public static class VariantNeeds
{
    /// <summary>The toons in <paramref name="group"/> whose record lacks this route.</summary>
    public static IReadOnlyList<string> NeededBy(VariantDungeon dungeon, int route, IEnumerable<ToonRecord> group) =>
        group.Where(t => !VariantRecord.IsDone(t.Bits, dungeon.NotebookRow(route))).Select(t => t.Name).ToList();

    /// <summary>For each toon, the routes it still needs on each path.</summary>
    public static IReadOnlyList<ToonNeeds> ByToon(VariantDungeon dungeon, IEnumerable<ToonRecord> group) =>
        group.Select(toon => new ToonNeeds(toon, Enum.GetValues<VariantPath>().ToDictionary(
                path => path,
                path => (IReadOnlyList<int>)dungeon.Routes
                    .Where(r => r.Path == path && !VariantRecord.IsDone(toon.Bits, dungeon.NotebookRow(r.Number)))
                    .Select(r => r.Number)
                    .ToList())))
            .ToList();

    /// <summary>
    /// The route that earns a record for the MOST of the group — one record per toon per run, so this is
    /// the run worth making. Ties go to the secret route (a failed attempt still lands a sibling, see
    /// <see cref="VariantRecord.Suggest"/>), then to the lowest number. Null when nobody needs anything.
    /// </summary>
    public static int? GroupSuggest(VariantDungeon dungeon, IReadOnlyList<ToonRecord> group)
    {
        if (group.Count == 0)
            return null;

        var best = dungeon.Routes
            .Select(r => (Route: r, Count: NeededBy(dungeon, r.Number, group).Count))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ThenByDescending(x => x.Route.Secret)
            .ThenBy(x => x.Route.Number)
            .FirstOrDefault();
        return best.Route?.Number;
    }
}
