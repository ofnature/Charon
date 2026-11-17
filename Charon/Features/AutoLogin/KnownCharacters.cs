using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Charon.Features.AutoLogin;

/// <summary>
/// A character that has logged in on this PC, as the login window offers it. Job, level and role are
/// what it was on when last seen — decoration for its card, never used to find it (that is the
/// content id). Role is ClassJob.Role: 1 tank, 2 melee, 3 ranged or caster, 4 healer, 0 crafter or
/// gatherer.
/// </summary>
public sealed record KnownCharacter(ulong ContentId, string Name, string World, DateTime SeenUtc,
    string Job = "", int Level = 0, int Role = 0);

/// <summary>
/// The list of characters the login window offers. It lives in a file of its own, shared by every
/// client on the PC, so the rules for combining two copies are here and tested: one entry per
/// content id, the most recently seen copy wins (a rename or a world transfer replaces the old
/// label), and the order is alphabetical so a button stays where it was.
/// </summary>
public static class KnownCharacters
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static IReadOnlyList<KnownCharacter> Merge(IEnumerable<KnownCharacter> first, IEnumerable<KnownCharacter> second) =>
        first.Concat(second)
            .Where(Valid)
            .Select(c => c with { World = c.World ?? string.Empty, Job = c.Job ?? string.Empty }) // an older file has neither
            .GroupBy(c => c.ContentId)
            .Select(g => g.OrderByDescending(c => c.SeenUtc).First())
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.World, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ContentId)
            .ToList();

    public static IReadOnlyList<KnownCharacter> Without(IEnumerable<KnownCharacter> list, ulong contentId) =>
        Merge(list.Where(c => c.ContentId != contentId), Array.Empty<KnownCharacter>());

    public static string Label(string name, string world) =>
        string.IsNullOrWhiteSpace(world) ? name : $"{name}@{world}";

    /// <summary>The second line of a card: the world and the level, whichever are known.</summary>
    public static string Subtitle(string world, int level)
    {
        var lv = level > 0 ? $"Lv {level}" : string.Empty;
        if (string.IsNullOrWhiteSpace(world))
            return lv;
        return lv.Length == 0 ? world : $"{world} · {lv}";
    }

    /// <summary>Up to two initials, for a card whose job is not known.</summary>
    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    /// <summary>How long ago the character was last seen logged in, in one short unit.</summary>
    public static string Ago(TimeSpan since)
    {
        if (since < TimeSpan.FromMinutes(1))
            return "just now";
        if (since < TimeSpan.FromHours(1))
            return $"{(int)since.TotalMinutes}m ago";
        if (since < TimeSpan.FromHours(48))
            return $"{(int)since.TotalHours}h ago";
        return $"{(int)since.TotalDays}d ago";
    }

    public static string ToJson(IEnumerable<KnownCharacter> list) =>
        JsonSerializer.Serialize(Merge(list, Array.Empty<KnownCharacter>()), Json);

    /// <summary>An unreadable or empty file is an empty list, never an error.</summary>
    public static IReadOnlyList<KnownCharacter> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<KnownCharacter>();
        try
        {
            var list = JsonSerializer.Deserialize<List<KnownCharacter?>>(json);
            return list == null
                ? Array.Empty<KnownCharacter>()
                : Merge(list.Where(c => c != null).Select(c => c!), Array.Empty<KnownCharacter>());
        }
        catch (JsonException)
        {
            return Array.Empty<KnownCharacter>();
        }
    }

    private static bool Valid(KnownCharacter c) => c.ContentId != 0 && !string.IsNullOrWhiteSpace(c.Name);
}
