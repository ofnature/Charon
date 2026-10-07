using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.DeepDungeon;

/// <summary>One pomander (or protomander) someone in the party used.</summary>
public sealed record PomanderUse(int Floor, uint ItemId, string Item, string User, DateTime AtUtc)
{
    /// <summary>The floor its effect lands on — the next one for the pomanders that say so.</summary>
    public int EffectFloor => PomanderLog.ActsOnNextFloor(ItemId) ? Floor + 1 : Floor;
}

/// <summary>
/// The run's pomander uses, and which of them are in force on a given floor. Pure — no Dalamud types.
///
/// Source: the game's own log message row 7254, whose template is
/// <c>&lt;ifself(lnum1,You use,&lt;pcname(lnum1)&gt; uses)&gt; a &lt;sheet(DeepDungeonItem,lnum2,1)&gt;.</c>
/// (read from the game data) — so the pomander is a DeepDungeonItem row id carried as a number, and
/// detection needs no text matching in any language.
///
/// Timing is the item's own tooltip (DeepDungeonItem, verified on XIVAPI): Affluence ("increases the
/// number of treasure coffers in the NEXT floor"), Flight ("decreases the number of enemies in the
/// next floor"), Alteration ("changes the enemy occupying a random room in the next floor into a mimic
/// or a mandragora"), and Devotion ("guarantees a votive candelabra in the next floor") act on the
/// floor AFTER the one they were used on; every other pomander acts where it is used.
/// </summary>
public sealed class PomanderLog
{
    public const uint UseLogMessageId = 7254;

    /// <summary>DeepDungeonItem rows: Pomander of Affluence 5, Flight 6, Alteration 7; Protomander of
    /// Affluence 27, Flight 28, Alteration 29; Pomander of Devotion 38.</summary>
    private static readonly HashSet<uint> NextFloorItems = [5, 6, 7, 27, 28, 29, 38];

    /// <summary>The highest DeepDungeonItem row (Pomander of Devotion) — anything above is not a pomander.</summary>
    public const uint MaxItemId = 38;

    private const int Capacity = 200;

    private readonly List<PomanderUse> _uses = new();

    public IReadOnlyList<PomanderUse> Uses => _uses;

    public static bool ActsOnNextFloor(uint itemId) => NextFloorItems.Contains(itemId);

    public static bool IsPomander(long value) => value is >= 1 and <= MaxItemId;

    public void Record(PomanderUse use)
    {
        _uses.Add(use);
        if (_uses.Count > Capacity)
            _uses.RemoveAt(0);
    }

    /// <summary>A new run starts clean: floor numbers repeat across runs and must not collide.</summary>
    public void Clear() => _uses.Clear();

    /// <summary>Uses whose effect lands on <paramref name="floor"/>, in the order they were used.</summary>
    public IReadOnlyList<PomanderUse> ActiveOn(int floor) => _uses.Where(u => u.EffectFloor == floor).ToList();

    /// <summary>Uses on <paramref name="floor"/> that will only take effect on the next one.</summary>
    public IReadOnlyList<PomanderUse> Pending(int floor) =>
        _uses.Where(u => u.Floor == floor && u.EffectFloor == floor + 1).ToList();

    /// <summary>
    /// "pomander of sight, pomander of alteration" — or empty. WHO used it is deliberately not shown:
    /// the message's source entity read back as this client's own toon whoever used it (seen live
    /// 2026-10-06, user: "its just showing the toons name not who used it").
    /// </summary>
    public static string Describe(IEnumerable<PomanderUse> uses) =>
        string.Join(", ", uses.Select(u => u.Item));
}
