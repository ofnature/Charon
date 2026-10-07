using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.DeepDungeon;

/// <summary>Palace Pal's own numbering (its <c>ClientLocation.EType</c>), so imported rows map 1:1.</summary>
public enum SpotKind : byte
{
    Trap = 1,
    Hoard = 2,
}

/// <summary>A place a trap or an accursed hoard has been seen on some run of this floor set.</summary>
public readonly record struct Spot(SpotKind Kind, float X, float Y, float Z, bool Seen);

/// <summary>
/// Every place a trap or hoard has been seen, per territory. Pure — no Dalamud types.
///
/// THE IDEA (facts from Palace Pal, which is AGPL-3.0 — nothing of its code is reused): a deep
/// dungeon floor set draws its traps and hoards from a FIXED pool of positions per layout, so
/// every spot one has ever appeared on is a spot one may appear on again. Traps are server-side
/// and invisible until a Pomander of Sight (see the ESP note), which is what makes a list of
/// likely spots worth having.
///
/// Positions are deduplicated on Palace Pal's 0.2-yalm grid (coordinates × 5, truncated), so two
/// sightings of the same trap — or the same spot imported and then seen live — count once.
/// "Seen" means this fleet saw it with its own eyes rather than importing it.
/// </summary>
public sealed class SpotStore
{
    private readonly Dictionary<uint, Dictionary<(SpotKind, int, int, int), Spot>> _byTerritory = new();

    public int Count { get; private set; }

    /// <summary>
    /// Adds a spot, or upgrades an existing one to Seen. Returns true when anything changed, which
    /// is what tells the caller there is something new to save.
    /// </summary>
    public bool Add(uint territory, SpotKind kind, float x, float y, float z, bool seen)
    {
        if (kind is not (SpotKind.Trap or SpotKind.Hoard) || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return false;

        if (!_byTerritory.TryGetValue(territory, out var spots))
            _byTerritory[territory] = spots = new();

        var key = (kind, Grid(x), Grid(y), Grid(z));
        if (spots.TryGetValue(key, out var existing))
        {
            if (!seen || existing.Seen)
                return false;

            spots[key] = existing with { Seen = true };
            return true;
        }

        spots[key] = new Spot(kind, x, y, z, seen);
        Count++;
        return true;
    }

    public IReadOnlyCollection<Spot> In(uint territory) =>
        _byTerritory.TryGetValue(territory, out var spots) ? spots.Values : Array.Empty<Spot>();

    public (int Traps, int Hoards) CountIn(uint territory)
    {
        var spots = In(territory);
        return (spots.Count(s => s.Kind == SpotKind.Trap), spots.Count(s => s.Kind == SpotKind.Hoard));
    }

    /// <summary>Adds everything from <paramref name="other"/>; returns how many spots changed.</summary>
    public int Merge(SpotStore other)
    {
        var changed = 0;
        foreach (var (territory, spots) in other._byTerritory)
        {
            foreach (var spot in spots.Values)
            {
                if (Add(territory, spot.Kind, spot.X, spot.Y, spot.Z, spot.Seen))
                    changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// The saved shape: territory → rows of [kind, x, y, z, seen]. Plain numbers keep 38k spots to
    /// a couple of megabytes, and the file stays readable by anything that reads JSON.
    /// </summary>
    public Dictionary<string, List<float[]>> ToRows() =>
        _byTerritory.ToDictionary(
            t => t.Key.ToString(),
            t => t.Value.Values.Select(s => new[] { (float)s.Kind, s.X, s.Y, s.Z, s.Seen ? 1f : 0f }).ToList());

    public static SpotStore FromRows(Dictionary<string, List<float[]>>? rows)
    {
        var store = new SpotStore();
        if (rows == null)
            return store;

        foreach (var (territory, list) in rows)
        {
            if (!uint.TryParse(territory, out var id) || list == null)
                continue;

            foreach (var row in list)
            {
                if (row is { Length: >= 5 })
                    store.Add(id, (SpotKind)(byte)row[0], row[1], row[2], row[3], row[4] != 0f);
            }
        }

        return store;
    }

    /// <summary>Palace Pal's position key: coordinates × 5, truncated toward zero.</summary>
    private static int Grid(float v) => (int)(v * 5f);
}

/// <summary>Something the game says in the log that settles what a floor holds.</summary>
public enum FloorNews
{
    None,

    /// <summary>Pomander of Sight: every trap on the floor is now visible for real.</summary>
    TrapsRevealed,

    /// <summary>Pomander of Safety: every trap on the floor is gone.</summary>
    TrapsRemoved,

    /// <summary>Intuition: the hoard is on this floor (and now visible).</summary>
    HoardHere,

    /// <summary>Intuition: there is no hoard on this floor.</summary>
    NoHoard,

    /// <summary>The hoard was dug up.</summary>
    HoardFound,
}

/// <summary>
/// The game's log text for each <see cref="FloorNews"/>, read from the LogMessage sheet in the
/// client's language. Rows (verified on XIVAPI): 7256 "The map for this floor has been revealed!",
/// 7255 "All the traps in this floor have disappeared!", 7272 "You sense the Accursed Hoard calling
/// you...", 7273 "You do not sense the call of the Accursed Hoard in this floor...", 7274 "You
/// discover a piece of the Accursed Hoard!".
/// </summary>
public sealed record FloorNewsTexts(
    string TrapsRevealed, string TrapsRemoved, string HoardHere, string NoHoard, string HoardFound)
{
    public const uint TrapsRevealedRow = 7256;
    public const uint TrapsRemovedRow = 7255;
    public const uint HoardHereRow = 7272;
    public const uint NoHoardRow = 7273;
    public const uint HoardFoundRow = 7274;

    /// <summary>Ends-with, as the game may prefix the line. Blank sheet text never matches anything.</summary>
    public FloorNews Classify(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return FloorNews.None;

        if (Ends(message, TrapsRevealed)) return FloorNews.TrapsRevealed;
        if (Ends(message, TrapsRemoved)) return FloorNews.TrapsRemoved;
        if (Ends(message, NoHoard)) return FloorNews.NoHoard; // before HoardHere: never let a near-miss match the wrong one
        if (Ends(message, HoardHere)) return FloorNews.HoardHere;
        if (Ends(message, HoardFound)) return FloorNews.HoardFound;
        return FloorNews.None;
    }

    private static bool Ends(string message, string text) =>
        !string.IsNullOrWhiteSpace(text) && message.TrimEnd().EndsWith(text.Trim(), StringComparison.Ordinal);
}

/// <summary>
/// What this floor is already known to hold — decides whether the "likely" spots are still worth
/// drawing. Pure; reset on every floor change.
///
/// Once Sight has revealed the traps (or Safety removed them), a likely-trap ring can only mislead:
/// the real ones are drawn by the ESP or there are none. Likewise once Intuition has answered, the
/// real hoard (if any) is drawn with its line and the guesses should go.
/// </summary>
public sealed class FloorIntel
{
    public bool TrapsSettled { get; private set; }
    public bool HoardSettled { get; private set; }
    public FloorNews Last { get; private set; } = FloorNews.None;

    public void Apply(FloorNews news)
    {
        switch (news)
        {
            case FloorNews.TrapsRevealed:
            case FloorNews.TrapsRemoved:
                TrapsSettled = true;
                break;
            case FloorNews.HoardHere:
            case FloorNews.NoHoard:
            case FloorNews.HoardFound:
                HoardSettled = true;
                break;
            default:
                return;
        }

        Last = news;
    }

    public void Reset()
    {
        TrapsSettled = false;
        HoardSettled = false;
        Last = FloorNews.None;
    }

    public bool Shows(SpotKind kind) => kind == SpotKind.Trap ? !TrapsSettled : !HoardSettled;

    public string Describe()
    {
        var traps = TrapsSettled ? "traps known" : "traps unknown";
        var hoard = HoardSettled ? "hoard known" : "hoard unknown";
        return $"{traps}, {hoard}";
    }
}
