using System;
using System.Collections.Generic;
using System.Numerics;

namespace Charon.Features.Eureka;

/// <summary>How a Eureka mob notices you, as far as the public sources say (see <see cref="EurekaData"/>).</summary>
public enum EurekaAggro
{
    /// <summary>No source says. Most of Anemos and Pagos.</summary>
    Unknown,

    /// <summary>A cone in front of the mob.</summary>
    Sight,

    /// <summary>"True sight" in the bestiary sheet. Its meaning is disputed (sees through stealth, or
    /// notices anyone close); drawn as sight, labelled as itself.</summary>
    TrueSight,

    /// <summary>A circle: footsteps. Walk past, don't run.</summary>
    Sound,

    Proximity,

    /// <summary>Sprites: reacts to spellcasting nearby.</summary>
    Magic,

    /// <summary>Ashkin and the sheet's "blood": reacts to a player on low HP. The threshold is not
    /// published (guesses run 20%-80%).</summary>
    LowHp,
}

/// <summary>One mob's aggro fact. <paramref name="Territory"/> 0 = every Eureka zone (the sprite rule).
/// <paramref name="Confirmed"/> = named per mob in a source; false = a family rule or a single weak source.</summary>
public readonly record struct EurekaMobFact(uint Territory, uint NameId, EurekaAggro Aggro, bool Confirmed);

/// <summary>Eureka ids. All verified against the game's own sheets with Lumina.</summary>
public static class EurekaZones
{
    public const uint Anemos = 732;
    public const uint Pagos = 763;
    public const uint Pyros = 795;
    public const uint Hydatos = 827;

    public static bool IsEureka(uint territory) => territory is Anemos or Pagos or Pyros or Hydatos;

    public static string Name(uint territory) => territory switch
    {
        Anemos => "Anemos",
        Pagos => "Pagos",
        Pyros => "Pyros",
        Hydatos => "Hydatos",
        _ => "not Eureka",
    };

    /// <summary>The friendly elementals (BNpcName "Anemos/Pagos/Pyros/Hydatos elemental"; EurekaHelper's list).</summary>
    public static readonly IReadOnlySet<uint> Elementals = new HashSet<uint> { 7184, 7567, 7764, 8131 };

    /// <summary>Bunny coffers (EObjName "gold/silver/bronze coffer").</summary>
    public const uint GoldCoffer = 2009530;
    public const uint SilverCoffer = 2009531;
    public const uint BronzeCoffer = 2009532;

    /// <summary>Status "Cache Me if You Can": the bunny coffer hunt is on. Eureka Linker gates its coffer
    /// hint on the same status.</summary>
    public const uint CofferHuntStatus = 1531;
}

/// <summary>Lookups over <see cref="EurekaData"/>.</summary>
public static class EurekaMobs
{
    private static readonly Dictionary<(uint Territory, uint NameId), EurekaMobFact> ByKey = Build();

    private static Dictionary<(uint, uint), EurekaMobFact> Build()
    {
        var map = new Dictionary<(uint, uint), EurekaMobFact>();
        foreach (var fact in EurekaData.Mobs)
            map.TryAdd((fact.Territory, fact.NameId), fact);
        return map;
    }

    /// <summary>The fact for this mob in this zone. A zone's own entry wins over an every-zone rule; a
    /// fact listed for another zone never applies (two zones can reuse a name for different mobs).</summary>
    public static EurekaMobFact? Find(uint territory, uint nameId)
    {
        if (!EurekaZones.IsEureka(territory))
            return null;
        if (ByKey.TryGetValue((territory, nameId), out var own))
            return own;
        return ByKey.TryGetValue((0, nameId), out var any) ? any : null;
    }

    /// <summary>"sight", "low HP?" — a "?" marks a fact that is inferred or single-sourced.</summary>
    public static string Label(EurekaAggro aggro, bool confirmed)
    {
        var text = aggro switch
        {
            EurekaAggro.Sight => "sight",
            EurekaAggro.TrueSight => "true sight",
            EurekaAggro.Sound => "sound",
            EurekaAggro.Proximity => "proximity",
            EurekaAggro.Magic => "magic",
            EurekaAggro.LowHp => "low HP",
            _ => "aggro unknown",
        };
        return aggro == EurekaAggro.Unknown || confirmed ? text : text + "?";
    }
}

/// <summary>Whether a mob will come for you at all.</summary>
public static class EurekaAggroRules
{
    /// <summary>Eureka mobs do not attack a player 2 or more elemental levels above them (Console Games
    /// Wiki, Aggression and Eureka pages).</summary>
    public const int LevelsAboveToBeIgnored = 2;

    /// <summary>True when the mob leaves you alone unless you hit it. An unread level (0) is never
    /// read as safe.</summary>
    public static bool IgnoresYou(int mobLevel, int playerLevel) =>
        mobLevel > 0 && playerLevel > 0 && playerLevel - mobLevel >= LevelsAboveToBeIgnored;
}

/// <summary>Spot lists by distance.</summary>
public static class EurekaSpots
{
    public static IEnumerable<Vector3> Near(IReadOnlyDictionary<uint, Vector3[]> table, uint territory,
        Vector3 from, float range)
    {
        if (!table.TryGetValue(territory, out var spots))
            yield break;
        foreach (var spot in spots)
            if (Vector3.Distance(from, spot) <= range)
                yield return spot;
    }

    /// <summary>The closest spot within <paramref name="range"/>, or null.</summary>
    public static Vector3? Nearest(IReadOnlyDictionary<uint, Vector3[]> table, uint territory, Vector3 from, float range)
    {
        Vector3? best = null;
        var bestDistance = range;
        foreach (var spot in Near(table, territory, from, range))
        {
            var distance = Vector3.Distance(from, spot);
            if (distance > bestDistance)
                continue;
            bestDistance = distance;
            best = spot;
        }

        return best;
    }
}
