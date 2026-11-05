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

/// <summary>How much elemental EXP a mob is worth to you, by level difference (Console Games Wiki,
/// "Elemental EXP"): more for a mob above you, peaking at 6 levels higher; nothing once the gap is more
/// than 8 either way; and only a kill at or above your level starts or continues an EXP chain.</summary>
public enum EurekaExpBand
{
    /// <summary>A level was not read.</summary>
    Unknown,

    /// <summary>More than 8 levels apart.</summary>
    None,

    /// <summary>Below you: some EXP, no chain.</summary>
    Low,

    /// <summary>Your level up to 5 above: good EXP, and it chains.</summary>
    Chain,

    /// <summary>6 to 8 above: the most EXP a kill gives, and it chains.</summary>
    Peak,
}

public static class EurekaExp
{
    public const int PeakLevelsAbove = 6;
    public const int MaxLevelGap = 8;

    public static EurekaExpBand Band(int mobLevel, int playerLevel)
    {
        if (mobLevel <= 0 || playerLevel <= 0)
            return EurekaExpBand.Unknown;
        var above = mobLevel - playerLevel;
        if (Math.Abs(above) > MaxLevelGap)
            return EurekaExpBand.None;
        if (above >= PeakLevelsAbove)
            return EurekaExpBand.Peak;
        return above >= 0 ? EurekaExpBand.Chain : EurekaExpBand.Low;
    }

    public static string Label(EurekaExpBand band) => band switch
    {
        EurekaExpBand.Peak => "peak EXP",
        EurekaExpBand.Chain => "EXP, chains",
        EurekaExpBand.Low => "low EXP",
        EurekaExpBand.None => "no EXP",
        _ => "",
    };
}

public enum EurekaFateKind
{
    NotoriousMonster,
    Bunny,

    /// <summary>Hydatos's Ovni and the Baldesion Arsenal support FATE.</summary>
    Special,
}

/// <summary>One Eureka FATE (<see cref="EurekaFateData"/>). <paramref name="Boss"/> is empty for bunny FATEs.</summary>
public readonly record struct EurekaFateInfo(ushort FateId, uint Territory, string Boss, int Level, EurekaFateKind Kind);

public static class EurekaFates
{
    private static readonly Dictionary<ushort, EurekaFateInfo> ById = Build();

    private static Dictionary<ushort, EurekaFateInfo> Build()
    {
        var map = new Dictionary<ushort, EurekaFateInfo>();
        foreach (var fate in EurekaFateData.Fates)
            map.TryAdd(fate.FateId, fate);
        return map;
    }

    public static EurekaFateInfo? Find(ushort fateId) => ById.TryGetValue(fateId, out var info) ? info : null;

    /// <summary>Dalamud's FateState: 3 = Preparing, 4 = Running. Ending/Ended/Failed are over.</summary>
    public static bool IsLive(int state) => state is 3 or 4;

    /// <summary>"NM Ash Dragon Lv22 · 45% · 230y". A FATE missing from the table still gets its own name.</summary>
    public static string Label(EurekaFateInfo? info, string fateName, int progress, float distance)
    {
        var what = info switch
        {
            { Kind: EurekaFateKind.Bunny } => $"Bunny FATE · {fateName}",
            { Boss.Length: > 0 } i => $"{(i.Kind == EurekaFateKind.NotoriousMonster ? "NM" : "FATE")} {i.Boss} Lv{i.Level}",
            _ => $"FATE {fateName}",
        };
        return $"{what} · {progress}% · {distance:0}y";
    }
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
