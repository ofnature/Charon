using System.Linq;
using Charon.Features.Eureka;

namespace Charon.Tests.Features.Eureka;

public sealed class EurekaSpawnsTests
{
    // Some fixed real moment to sweep from (2026-11-05 00:00 UTC).
    private const long Start = 1_793_836_800;

    [Fact]
    public void EveryTrigger_PointsAtANotoriousMonsterInItsOwnZone()
    {
        Assert.Equal(65, EurekaFateData.Triggers.Length);
        foreach (var trigger in EurekaFateData.Triggers)
        {
            var fate = EurekaFates.Find(trigger.FateId);
            Assert.NotNull(fate);
            Assert.Equal(EurekaFateKind.NotoriousMonster, fate!.Value.Kind);
            Assert.Equal(fate.Value.Territory, trigger.Territory);
            Assert.NotEmpty(trigger.TriggerIds);
        }
    }

    [Fact]
    public void EveryWeatherAnNmNeeds_ActuallyHappensInItsZone()
    {
        // A weather the zone never rolls would mean an NM that can never spawn: a data error.
        foreach (var trigger in EurekaFateData.Triggers)
        {
            var zone = EurekaWeather.Rates[trigger.Territory].Select(r => r.Weather).ToHashSet();
            if (trigger.NmWeather != 0)
                Assert.Contains(trigger.NmWeather, zone);
            if (trigger.TriggerWeather != 0)
                Assert.Contains(trigger.TriggerWeather, zone);
        }
    }

    [Fact]
    public void WeatherRates_AddUpTo100()
    {
        Assert.All(EurekaWeather.Rates.Values, r => Assert.Equal(100, r.Sum(x => x.Chance)));
    }

    [Fact]
    public void Night_IsEorzea19To6()
    {
        Assert.False(EorzeaClock.IsNight(18 * EorzeaClock.BellSeconds));
        Assert.True(EorzeaClock.IsNight(19 * EorzeaClock.BellSeconds));
        Assert.True(EorzeaClock.IsNight(5 * EorzeaClock.BellSeconds));
        Assert.False(EorzeaClock.IsNight(6 * EorzeaClock.BellSeconds));
    }

    [Fact]
    public void Weather_IsConstantThroughEachEightBellPeriod()
    {
        for (var period = 0; period < 50; period++)
        {
            var begin = Start / EorzeaClock.WeatherPeriodSeconds * EorzeaClock.WeatherPeriodSeconds
                        + period * EorzeaClock.WeatherPeriodSeconds;
            var first = EorzeaClock.WeatherAt(EurekaZones.Pyros, begin);
            Assert.Equal(first, EorzeaClock.WeatherAt(EurekaZones.Pyros, begin + EorzeaClock.WeatherPeriodSeconds - 1));
        }
    }

    [Fact]
    public void Weather_OnlyRollsWhatTheZoneHas()
    {
        var zone = EurekaWeather.Rates[EurekaZones.Hydatos].Select(r => r.Weather).ToHashSet();
        for (var i = 0; i < 500; i++)
            Assert.Contains(EorzeaClock.WeatherAt(EurekaZones.Hydatos, Start + i * EorzeaClock.WeatherPeriodSeconds), zone);
        Assert.Equal(0u, EorzeaClock.WeatherAt(132, Start));
    }

    [Fact]
    public void Pazuzu_NeedsGalesAtNight_AndItsWindowsAreExactlyThat()
    {
        var pazuzu = EurekaSpawns.ForFate(1329)!.Value;
        Assert.Equal("Shadow Wraith", pazuzu.TriggerName);
        Assert.Equal("Gales · night", pazuzu.Needs());

        // Sweep a day of starting points: every window found must hold at its start, must not hold the bell
        // before it (unless it is already open), and must have closed at its end.
        for (var i = 0; i < 200; i++)
        {
            var now = Start + i * 431;
            var window = EurekaSpawns.NextWindow(pazuzu, now);
            Assert.NotNull(window);
            var (open, close) = window!.Value;
            Assert.True(close > open);
            Assert.True(pazuzu.MetAt(open));
            Assert.False(pazuzu.MetAt(close));
            Assert.True(pazuzu.MetAt(close - 1));
            if (open > now)
                Assert.False(pazuzu.MetAt(open - 1));
            Assert.True(EorzeaClock.IsNight(open));
            Assert.Equal(EurekaWeather.Gales, EorzeaClock.WeatherAt(EurekaZones.Anemos, open));
        }
    }

    [Fact]
    public void AnNmWithNoConditions_IsAlwaysOpen()
    {
        var sabo = EurekaSpawns.ForFate(1332)!.Value;
        Assert.Equal("", sabo.Needs());
        var window = EurekaSpawns.NextWindow(sabo, Start + 77);
        Assert.True(window!.Value.Start <= Start + 77);
    }

    [Fact]
    public void TriggerWeather_IsNamedWithItsMob()
    {
        // Jahannam: the NM needs nothing, its Typhoon Sprite only comes out in Gales.
        Assert.Equal("Gales for the Typhoon Sprite", EurekaSpawns.ForFate(1345)!.Value.Needs());
    }

    [Fact]
    public void ATriggerMob_IsOnlyATriggerInItsOwnZone()
    {
        Assert.Equal((ushort)1353, EurekaSpawns.ForMob(EurekaZones.Pagos,
            EurekaSpawns.ForFate(1353)!.Value.TriggerIds[0])!.Value.FateId);
        Assert.Null(EurekaSpawns.ForMob(EurekaZones.Pyros, EurekaSpawns.ForFate(1353)!.Value.TriggerIds[0]));
    }

    [Fact]
    public void Describe_SaysOpenOrHowLong()
    {
        Assert.Equal("open, 12m left", EurekaSpawns.Describe((100, 100 + 720), 100));
        Assert.Equal("in 1h 05m", EurekaSpawns.Describe((100 + 3900, 9000), 100));
        Assert.Equal("not in the next 35h", EurekaSpawns.Describe(null, 100));
    }
}
