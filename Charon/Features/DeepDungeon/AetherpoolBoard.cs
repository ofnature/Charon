using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.DeepDungeon;

/// <summary>One toon's aetherpool gear in one deep dungeon, as that toon's own client reported it.</summary>
public sealed record AetherpoolEntry(string Name, int Dungeon, int Arm, int Armor, DateTime SeenUtc);

/// <summary>
/// The fleet's aetherpool levels. Pure — no Dalamud types.
///
/// A toon's aetherpool arm and armor are readable only by its own client (the deep dungeon struct holds
/// the local player's alone), so each box reports its own over the LAN relay while it is inside a deep
/// dungeon and this keeps the latest word from each.
///
/// The levels are per KIND of deep dungeon (Palace of the Dead, Heaven-on-High, ... each has its own), so
/// an entry is only ever shown beside the same kind. And a box that has gone quiet is DROPPED from the
/// view, not shown with its last numbers: the levels change as coffers are opened, and silence means
/// "not known", never "unchanged".
/// </summary>
public sealed class AetherpoolBoard
{
    /// <summary>Three missed 30-second reports, plus slack.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(95);

    /// <summary>The game caps aetherpool gear at +99.</summary>
    public const int MaxLevel = 99;

    private readonly Dictionary<string, AetherpoolEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records a report. False when it is not a plausible one (nothing is stored).</summary>
    public bool Upsert(string name, int dungeon, int arm, int armor, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || dungeon <= 0 || !Plausible(arm) || !Plausible(armor))
            return false;

        _entries[name] = new AetherpoolEntry(name, dungeon, arm, armor, nowUtc);
        return true;
    }

    /// <summary>Everyone heard from recently in this kind of deep dungeon, by name.</summary>
    public IReadOnlyList<AetherpoolEntry> For(int dungeon, DateTime nowUtc) =>
        _entries.Values
            .Where(e => e.Dungeon == dungeon && nowUtc - e.SeenUtc <= MaxAge)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The last report from this toon, however old, or null if none was heard this session.</summary>
    public AetherpoolEntry? Last(string name) => _entries.TryGetValue(name, out var entry) ? entry : null;

    /// <summary>
    /// What went UP between a toon's previous report and this one, as a chat line, or null when nothing did.
    /// A first report is not an increase (there is nothing to compare with), nor is a report from another kind
    /// of deep dungeon, nor a level going down.
    /// </summary>
    public static string? Increase(AetherpoolEntry? before, string name, int dungeon, int arm, int armor)
    {
        if (before == null || before.Dungeon != dungeon)
            return null;

        var parts = new List<string>(2);
        if (arm > before.Arm)
            parts.Add($"arm +{arm} (was +{before.Arm})");
        if (armor > before.Armor)
            parts.Add($"armor +{armor} (was +{before.Armor})");
        return parts.Count == 0 ? null : $"{name}'s aetherpool {string.Join(", ", parts)}";
    }

    public static bool Plausible(int level) => level is >= 0 and <= MaxLevel;

    /// <summary>"+15 / +15" — arm first, as the game lists them.</summary>
    public static string Describe(int arm, int armor) => $"+{arm} / +{armor}";
}
