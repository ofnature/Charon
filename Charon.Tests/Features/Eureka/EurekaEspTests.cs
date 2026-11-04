using System.Linq;
using System.Numerics;
using Charon.Features.Eureka;

namespace Charon.Tests.Features.Eureka;

public sealed class EurekaEspTests
{
    [Fact]
    public void AListedMob_IsFoundInItsOwnZone()
    {
        // Pyros Wolf: "Blood" in the bestiary sheet's Pyros tab.
        var fact = EurekaMobs.Find(EurekaZones.Pyros, 7775);
        Assert.NotNull(fact);
        Assert.Equal(EurekaAggro.LowHp, fact!.Value.Aggro);
    }

    [Fact]
    public void AFactListedForAnotherZone_NeverApplies()
    {
        // Henbane is listed for Pyros only; the same id elsewhere is not covered by that listing.
        Assert.NotNull(EurekaMobs.Find(EurekaZones.Pyros, 7835));
        Assert.Null(EurekaMobs.Find(EurekaZones.Anemos, 7835));
    }

    [Fact]
    public void Sprites_AreMagicInEveryZone()
    {
        foreach (var zone in new[] { EurekaZones.Anemos, EurekaZones.Pagos, EurekaZones.Pyros, EurekaZones.Hydatos })
            Assert.Equal(EurekaAggro.Magic, EurekaMobs.Find(zone, 7470)!.Value.Aggro); // Thunderstorm Sprite
    }

    [Fact]
    public void OutsideEureka_NothingIsFound()
    {
        Assert.Null(EurekaMobs.Find(132, 7470));
    }

    [Fact]
    public void AnUnlistedMob_IsUnknown_NotGuessed()
    {
        Assert.Null(EurekaMobs.Find(EurekaZones.Anemos, 1));
    }

    [Fact]
    public void Labels_MarkWhatIsNotConfirmed()
    {
        Assert.Equal("sound", EurekaMobs.Label(EurekaAggro.Sound, confirmed: true));
        Assert.Equal("low HP?", EurekaMobs.Label(EurekaAggro.LowHp, confirmed: false));
        Assert.Equal("aggro unknown", EurekaMobs.Label(EurekaAggro.Unknown, confirmed: false));
    }

    [Fact]
    public void AMobTwoLevelsBelowYou_LeavesYouAlone()
    {
        Assert.True(EurekaAggroRules.IgnoresYou(mobLevel: 8, playerLevel: 10));
        Assert.False(EurekaAggroRules.IgnoresYou(mobLevel: 9, playerLevel: 10));
        Assert.False(EurekaAggroRules.IgnoresYou(mobLevel: 12, playerLevel: 10));
    }

    [Fact]
    public void AnUnreadLevel_IsNeverSafe()
    {
        Assert.False(EurekaAggroRules.IgnoresYou(mobLevel: 1, playerLevel: 0));
        Assert.False(EurekaAggroRules.IgnoresYou(mobLevel: 0, playerLevel: 30));
    }

    [Fact]
    public void TheData_HasNoDuplicateKeys_AndNoUnknownRows()
    {
        var keys = EurekaData.Mobs.Select(m => (m.Territory, m.NameId)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain(EurekaData.Mobs, m => m.Aggro == EurekaAggro.Unknown);
        Assert.All(EurekaData.Mobs, m => Assert.True(m.Territory == 0 || EurekaZones.IsEureka(m.Territory)));
    }

    [Fact]
    public void SpotCounts_MatchTheirSources()
    {
        Assert.Equal(39, EurekaData.ElementalSpots[EurekaZones.Anemos].Length);
        Assert.Equal(40, EurekaData.ElementalSpots[EurekaZones.Pagos].Length);
        Assert.Equal(35, EurekaData.ElementalSpots[EurekaZones.Pyros].Length);
        Assert.Equal(32, EurekaData.ElementalSpots[EurekaZones.Hydatos].Length);
        Assert.Equal(60, EurekaData.CofferSpots[EurekaZones.Pagos].Length);
        Assert.Equal(60, EurekaData.CofferSpots[EurekaZones.Pyros].Length);
        Assert.Equal(30, EurekaData.CofferSpots[EurekaZones.Hydatos].Length);
        Assert.False(EurekaData.CofferSpots.ContainsKey(EurekaZones.Anemos)); // no bunny FATE there
    }

    [Fact]
    public void Spots_AreFilteredByRange_AndTheNearestWins()
    {
        var table = new System.Collections.Generic.Dictionary<uint, Vector3[]>
        {
            [EurekaZones.Pagos] = [new(0, 0, 10), new(0, 0, 50), new(0, 0, 300)],
        };

        Assert.Equal(2, EurekaSpots.Near(table, EurekaZones.Pagos, Vector3.Zero, 100).Count());
        Assert.Equal(new Vector3(0, 0, 10), EurekaSpots.Nearest(table, EurekaZones.Pagos, Vector3.Zero, 100));
        Assert.Null(EurekaSpots.Nearest(table, EurekaZones.Pagos, Vector3.Zero, 5));
        Assert.Empty(EurekaSpots.Near(table, EurekaZones.Anemos, Vector3.Zero, 1000));
    }
}
