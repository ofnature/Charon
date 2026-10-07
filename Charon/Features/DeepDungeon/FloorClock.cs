using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.DeepDungeon;

/// <summary>A deep-dungeon coffer colour.</summary>
public enum CofferKind
{
    Bronze,
    Silver,
    Gold,
}

/// <summary>What the floor's traps are doing, from the pomanders used on it.</summary>
public enum TrapStatus
{
    /// <summary>Hidden and live — nobody has used Sight or Safety here.</summary>
    Active,

    /// <summary>Sight revealed them.</summary>
    Visible,

    /// <summary>Safety removed them.</summary>
    Inactive,
}

/// <summary>
/// The floor panel's numbers: time per floor, the respawn countdown, trap status and floor effects.
/// Pure — no Dalamud types.
///
/// Ported from NecroLens (MIT, Jukkales/NecroLens — <c>DeepDungeonContentInfo</c>, <c>FloorDetails</c>,
/// <c>MainWindow</c>), adapted with attribution. The RESPAWN INTERVALS are NecroLens's curated data —
/// the game files do not carry them — keyed by InstanceContent row, which was VERIFIED on XIVAPI (60001
/// "the Palace of the Dead (Floors 1-10)" … 60050 "Pilgrim's Traverse (Stones 91-100)"). Their model:
/// enemies respawn every interval counted from the moment the floor began, boss floors (every tenth) have
/// none, and Eureka Orthos floor 99 has none either.
/// </summary>
public sealed class FloorClock
{
    private readonly Dictionary<int, TimeSpan> _floorTimes = new();
    private int _floor = -1;
    private DateTime _floorStartUtc;

    public int Floor => _floor;
    public DateTime FloorStartUtc => _floorStartUtc;

    /// <summary>Called every tick with the live floor; a change closes the old floor's time.</summary>
    public void Track(int floor, DateTime nowUtc)
    {
        if (floor == _floor)
            return;

        if (_floor > 0)
            _floorTimes[_floor] = nowUtc - _floorStartUtc;

        _floor = floor;
        _floorStartUtc = nowUtc;
    }

    public void Clear()
    {
        _floorTimes.Clear();
        _floor = -1;
    }

    /// <summary>Time on <paramref name="floor"/>: running for the current one, final for a finished one, null if never visited.</summary>
    public TimeSpan? TimeOn(int floor, DateTime nowUtc) =>
        floor == _floor ? nowUtc - _floorStartUtc
        : _floorTimes.TryGetValue(floor, out var time) ? time
        : null;

    /// <summary>The ten floors of the set <paramref name="floor"/> belongs to (31 → 31..40).</summary>
    public static IReadOnlyList<int> FloorSet(int floor)
    {
        var start = (Math.Max(floor, 1) - 1) / 10 * 10 + 1;
        return Enumerable.Range(start, 10).ToList();
    }

    /// <summary>Seconds between respawns for this floor set, or null when the set is not known.</summary>
    public static int? RespawnSeconds(uint contentId) => contentId switch
    {
        // Palace of the Dead
        60001 => 40,
        >= 60002 and <= 60004 => 60,
        60005 => 120,
        >= 60006 and <= 60009 => 60,
        60010 => 120,
        >= 60011 and <= 60015 => 90,
        >= 60016 and <= 60020 => 300,
        // Heaven-on-High
        >= 60021 and <= 60023 => 60,
        >= 60024 and <= 60030 => 600,
        // Eureka Orthos
        >= 60031 and <= 60033 => 60,
        >= 60034 and <= 60040 => 600,
        // Pilgrim's Traverse
        >= 60041 and <= 60043 => 60,
        >= 60044 and <= 60050 => 600,
        _ => null,
    };

    /// <summary>
    /// Which coffer colour can be a MIMIC on this floor set — NecroLens's <c>MimicChests</c> column of the
    /// same table: bronze on the first three sets, silver on floors 31-60 (Palace of the Dead only 31-40),
    /// gold above. Null when the set is not known. In a bronze set the mimic is the separate
    /// "Treasure Coffer" object (2006020), which Charon never opens anyway; in a silver or gold set it is
    /// an ordinary-looking coffer of that colour, which is why auto-open has to skip the colour itself
    /// (user, 2026-10-06: "im getting some silver chest turning into a mimic").
    /// </summary>
    public static CofferKind? MimicCoffer(uint contentId) => contentId switch
    {
        // Palace of the Dead
        >= 60001 and <= 60003 => CofferKind.Bronze,
        60004 => CofferKind.Silver,
        >= 60005 and <= 60020 => CofferKind.Gold,
        // Heaven-on-High, Eureka Orthos, Pilgrim's Traverse share one shape
        >= 60021 and <= 60023 or >= 60031 and <= 60033 or >= 60041 and <= 60043 => CofferKind.Bronze,
        >= 60024 and <= 60026 or >= 60034 and <= 60036 or >= 60044 and <= 60046 => CofferKind.Silver,
        >= 60027 and <= 60030 or >= 60037 and <= 60040 or >= 60047 and <= 60050 => CofferKind.Gold,
        _ => null,
    };

    public static bool HasRespawns(uint contentId, int floor) =>
        floor % 10 != 0 && !(contentId is >= 60031 and <= 60040 && floor == 99);

    /// <summary>Seconds until the next respawn, counting whole intervals from the floor's start.</summary>
    public static int SecondsToRespawn(DateTime floorStartUtc, DateTime nowUtc, int interval)
    {
        if (interval <= 0)
            return 0;

        var elapsed = Math.Max(0, (int)(nowUtc - floorStartUtc).TotalSeconds);
        return interval - (elapsed % interval);
    }

    /// <summary>Safety beats Sight: once the traps are gone, having seen them first changes nothing.</summary>
    public static TrapStatus Traps(IEnumerable<PomanderUse> activeHere)
    {
        var items = activeHere.Select(u => u.ItemId).ToHashSet();
        if (items.Contains(1) || items.Contains(23))
            return TrapStatus.Inactive;
        if (items.Contains(2) || items.Contains(24))
            return TrapStatus.Visible;
        return TrapStatus.Active;
    }

    /// <summary>
    /// NecroLens's six floor effects, each covering the pomander AND the protomander row (DeepDungeonItem,
    /// verified): Affluence 5/27, Flight 6/28, Alteration 7/29, Safety 1/23, Sight 2/24, Fortune 9/31.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, uint[] Items)> Effects =
    [
        ("Affluence", [5, 27]),
        ("Flight", [6, 28]),
        ("Alteration", [7, 29]),
        ("Safety", [1, 23]),
        ("Sight", [2, 24]),
        ("Fortune", [9, 31]),
    ];

    public static bool Has(IEnumerable<PomanderUse> uses, uint[] items) => uses.Any(u => items.Contains(u.ItemId));

    /// <summary>"03:19" — minutes can run past 59 rather than wrap.</summary>
    public static string Format(TimeSpan time) =>
        $"{(int)Math.Max(0, time.TotalMinutes):00}:{Math.Max(0, time.Seconds):00}";
}
