using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Eureka;

/// <summary>Weather sheet row ids for the weathers Eureka uses (verified with Lumina against the four zones'
/// WeatherRate rows 91/94/96/100).</summary>
public static class EurekaWeather
{
    public const uint FairSkies = 2;
    public const uint Fog = 4;
    public const uint Gales = 6;
    public const uint Showers = 8;
    public const uint Thunder = 9;
    public const uint Thunderstorms = 10;
    public const uint HeatWaves = 14;
    public const uint Snow = 15;
    public const uint Blizzards = 16;
    public const uint Gloom = 17;
    public const uint UmbralWind = 49;

    /// <summary>Each zone's weather roll, in sheet order: (weather, chance). Chances add up to 100.</summary>
    public static readonly IReadOnlyDictionary<uint, (uint Weather, int Chance)[]> Rates =
        new Dictionary<uint, (uint, int)[]>
        {
            [EurekaZones.Anemos] = [(FairSkies, 30), (Gales, 30), (Showers, 30), (Snow, 10)],
            [EurekaZones.Pagos] = [(FairSkies, 10), (Fog, 18), (HeatWaves, 18), (Snow, 18), (Thunder, 18), (Blizzards, 18)],
            [EurekaZones.Pyros] = [(FairSkies, 10), (HeatWaves, 18), (Thunder, 18), (Blizzards, 18), (UmbralWind, 18), (Snow, 18)],
            [EurekaZones.Hydatos] = [(FairSkies, 12), (Showers, 22), (Gloom, 22), (Thunderstorms, 22), (Snow, 22)],
        };

    public static string Name(uint weather) => weather switch
    {
        FairSkies => "Fair Skies",
        Fog => "Fog",
        Gales => "Gales",
        Showers => "Showers",
        Thunder => "Thunder",
        Thunderstorms => "Thunderstorms",
        HeatWaves => "Heat Waves",
        Snow => "Snow",
        Blizzards => "Blizzards",
        Gloom => "Gloom",
        UmbralWind => "Umbral Wind",
        _ => $"weather {weather}",
    };
}

/// <summary>
/// Eorzea time and the deterministic weather roll. A bell (Eorzea hour) is 175 real seconds and a weather
/// period is 8 bells (1,400 s). The roll is the game's standard one — the same code as Mercury's
/// <c>OccultWeatherForecast.CalculateForecastTarget</c>.
/// </summary>
public static class EorzeaClock
{
    public const long BellSeconds = 175;
    public const long WeatherPeriodSeconds = 8 * BellSeconds;

    public static int Hour(long unixSeconds) => (int)(unixSeconds / BellSeconds % 24);

    /// <summary>Night is Eorzea 19:00 to 05:59 (EurekaHelper's rule — when the Ashkin come out).</summary>
    public static bool IsNight(long unixSeconds)
    {
        var hour = Hour(unixSeconds);
        return hour >= 19 || hour < 6;
    }

    public static int ForecastTarget(long unixSeconds)
    {
        var bell = unixSeconds / BellSeconds;
        var increment = (uint)((bell + 8 - (bell % 8)) % 24);
        var totalDays = unchecked((uint)(unixSeconds / 4_200));
        var calculationBase = unchecked((totalDays * 100u) + increment);
        var step1 = unchecked((calculationBase << 11) ^ calculationBase);
        var step2 = unchecked((step1 >> 8) ^ step1);
        return (int)(step2 % 100u);
    }

    /// <summary>The weather in a Eureka zone at a moment, or 0 outside Eureka.</summary>
    public static uint WeatherAt(uint territory, long unixSeconds)
    {
        if (!EurekaWeather.Rates.TryGetValue(territory, out var rates))
            return 0;
        var target = ForecastTarget(unixSeconds);
        var threshold = 0;
        foreach (var (weather, chance) in rates)
        {
            threshold += chance;
            if (target < threshold)
                return weather;
        }

        return rates[^1].Weather;
    }
}

/// <summary>What spawns one notorious monster (<see cref="EurekaFateData.Triggers"/>). A weather of 0 means
/// none is needed.</summary>
public readonly record struct EurekaNmTrigger(
    ushort FateId,
    uint Territory,
    string TriggerName,
    uint[] TriggerIds,
    uint NmWeather,
    uint TriggerWeather,
    bool NightOnly)
{
    /// <summary>"Gales · night", or "" when nothing is needed.</summary>
    public string Needs()
    {
        var parts = new List<string>();
        if (NmWeather != 0)
            parts.Add(EurekaWeather.Name(NmWeather));
        if (TriggerWeather != 0 && TriggerWeather != NmWeather)
            parts.Add($"{EurekaWeather.Name(TriggerWeather)} for the {TriggerName}");
        if (NightOnly)
            parts.Add("night");
        return string.Join(" · ", parts);
    }

    /// <summary>True when every condition holds at this moment: the trigger mob is out and the NM can pop.</summary>
    public bool MetAt(long unixSeconds)
    {
        if (NightOnly && !EorzeaClock.IsNight(unixSeconds))
            return false;
        var weather = EorzeaClock.WeatherAt(Territory, unixSeconds);
        return (NmWeather == 0 || weather == NmWeather) && (TriggerWeather == 0 || weather == TriggerWeather);
    }
}

public static class EurekaSpawns
{
    /// <summary>How far ahead a window is searched: 30 Eorzea days, about 35 real hours.</summary>
    public const int SearchBells = 24 * 30;

    private static readonly Dictionary<ushort, EurekaNmTrigger> ByFate =
        EurekaFateData.Triggers.ToDictionary(t => t.FateId);

    private static readonly Dictionary<(uint Territory, uint NameId), EurekaNmTrigger> ByMob = BuildByMob();

    private static Dictionary<(uint, uint), EurekaNmTrigger> BuildByMob()
    {
        var map = new Dictionary<(uint, uint), EurekaNmTrigger>();
        foreach (var trigger in EurekaFateData.Triggers)
            foreach (var id in trigger.TriggerIds)
                map.TryAdd((trigger.Territory, id), trigger);
        return map;
    }

    public static EurekaNmTrigger? ForFate(ushort fateId) => ByFate.TryGetValue(fateId, out var t) ? t : null;

    /// <summary>The NM this mob's kills spawn, in this zone only.</summary>
    public static EurekaNmTrigger? ForMob(uint territory, uint nameId) =>
        ByMob.TryGetValue((territory, nameId), out var t) ? t : null;

    public static IEnumerable<EurekaNmTrigger> InZone(uint territory) =>
        EurekaFateData.Triggers.Where(t => t.Territory == territory);

    /// <summary>
    /// The window in which this NM can next be spawned: (start, end) in unix seconds, with start &lt;= now when
    /// it is open right now; null when none starts within <see cref="SearchBells"/>. Night and weather both
    /// change only on bell boundaries, so stepping bell by bell is exact.
    /// </summary>
    public static (long Start, long End)? NextWindow(EurekaNmTrigger trigger, long unixSeconds)
    {
        var bell = unixSeconds / EorzeaClock.BellSeconds * EorzeaClock.BellSeconds;
        long? start = null;
        for (var i = 0; i <= SearchBells; i++)
        {
            var at = bell + i * EorzeaClock.BellSeconds;
            var met = trigger.MetAt(at);
            if (met && start == null)
                start = at;
            else if (!met && start != null)
                return (start.Value, at);
        }

        return start == null ? null : (start.Value, bell + (SearchBells + 1) * EorzeaClock.BellSeconds);
    }

    /// <summary>"open, 12m left" / "in 1h 05m" / "not in the next 35h".</summary>
    public static string Describe((long Start, long End)? window, long unixSeconds)
    {
        if (window is not { } w)
            return "not in the next 35h";
        return w.Start <= unixSeconds
            ? $"open, {Span(w.End - unixSeconds)} left"
            : $"in {Span(w.Start - unixSeconds)}";
    }

    public static string Span(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";
    }
}
