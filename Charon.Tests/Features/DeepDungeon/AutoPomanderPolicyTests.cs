using System;
using System.Linq;
using Charon.Features.DeepDungeon;

namespace Charon.Tests.Features.DeepDungeon;

public sealed class AutoPomanderPolicyTests
{
    private static PomanderCandidate Toon(string name, PomanderRole role, float buff = 0f, bool alive = true, uint id = 0) =>
        new(name, id == 0 ? (uint)name.GetHashCode() : id, role, alive, buff);

    private static readonly PomanderCandidate Tank = Toon("Korha", PomanderRole.Tank);
    private static readonly PomanderCandidate Healer = Toon("Saar", PomanderRole.Healer);
    private static readonly PomanderCandidate Dps = Toon("Grakur", PomanderRole.Dps);

    [Fact]
    public void Steel_GoesToTheTankFirst()
    {
        Assert.Equal("Korha", AutoPomanderPolicy.Pick(SelfBuffPomander.Steel, [Dps, Healer, Tank])!.Name);
    }

    [Fact]
    public void Steel_GoesTankThenHealerThenDamage_AsEachIsCovered()
    {
        var tankCovered = Tank with { BuffSecondsLeft = 400f };
        Assert.Equal("Saar", AutoPomanderPolicy.Pick(SelfBuffPomander.Steel, [Dps, Healer, tankCovered])!.Name);

        var healerCovered = Healer with { BuffSecondsLeft = 300f };
        Assert.Equal("Grakur", AutoPomanderPolicy.Pick(SelfBuffPomander.Steel, [Dps, healerCovered, tankCovered])!.Name);
    }

    [Fact]
    public void Strength_GoesDamageThenHealerThenTank()
    {
        Assert.Equal("Grakur", AutoPomanderPolicy.Pick(SelfBuffPomander.Strength, [Tank, Healer, Dps])!.Name);

        var dpsCovered = Dps with { BuffSecondsLeft = 400f };
        Assert.Equal("Saar", AutoPomanderPolicy.Pick(SelfBuffPomander.Strength, [Tank, Healer, dpsCovered])!.Name);

        var healerCovered = Healer with { BuffSecondsLeft = 300f };
        Assert.Equal("Korha", AutoPomanderPolicy.Pick(SelfBuffPomander.Strength, [Tank, healerCovered, dpsCovered])!.Name);
    }

    [Fact]
    public void EveryoneCovered_NobodyUsesIt()
    {
        var party = new[] { Tank, Healer, Dps }.Select(c => c with { BuffSecondsLeft = 120f });
        Assert.Null(AutoPomanderPolicy.Pick(SelfBuffPomander.Steel, party));
    }

    [Fact]
    public void ABuffWithAMinuteOrLessLeft_IsRefreshed_InRoleOrder()
    {
        // The tank's steel is about to run out: it takes the spare one ahead of the unbuffed healer.
        var tankLow = Tank with { BuffSecondsLeft = 60f };
        Assert.Equal("Korha", AutoPomanderPolicy.Pick(SelfBuffPomander.Steel, [Dps, Healer, tankLow])!.Name);

        var tankFine = Tank with { BuffSecondsLeft = 61f };
        Assert.Equal("Saar", AutoPomanderPolicy.Pick(SelfBuffPomander.Steel, [Dps, Healer, tankFine])!.Name);
    }

    [Fact]
    public void ADeadToon_IsPassedOver()
    {
        var deadTank = Tank with { Alive = false };
        Assert.Equal("Saar", AutoPomanderPolicy.Pick(SelfBuffPomander.Steel, [Dps, Healer, deadTank])!.Name);
    }

    [Fact]
    public void TwoOfARole_AreOrderedByName_SoEveryClientAgrees()
    {
        var a = Toon("Alpha", PomanderRole.Dps);
        var b = Toon("bravo", PomanderRole.Dps);

        Assert.Equal("Alpha", AutoPomanderPolicy.Pick(SelfBuffPomander.Strength, [b, a, Tank])!.Name);
        Assert.Equal("Alpha", AutoPomanderPolicy.Pick(SelfBuffPomander.Strength, [Tank, a, b])!.Name);
    }

    [Fact]
    public void AnEmptyParty_PicksNobody()
    {
        Assert.Null(AutoPomanderPolicy.Pick(SelfBuffPomander.Steel, []));
    }

    [Theory]
    [InlineData(4u, SelfBuffPomander.Steel)]
    [InlineData(26u, SelfBuffPomander.Steel)]
    [InlineData(3u, SelfBuffPomander.Strength)]
    [InlineData(25u, SelfBuffPomander.Strength)]
    public void SteelAndStrength_AreRecognised_PomanderAndProtomander(uint item, SelfBuffPomander kind)
    {
        Assert.Equal(kind, AutoPomanderPolicy.KindOf(item));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(14u)]
    [InlineData(36u)]
    public void AnyOtherPomander_IsLeftAlone(uint item)
    {
        Assert.Null(AutoPomanderPolicy.KindOf(item));
    }

    [Theory]
    [InlineData((byte)1, PomanderRole.Tank)]
    [InlineData((byte)4, PomanderRole.Healer)]
    [InlineData((byte)2, PomanderRole.Dps)]
    [InlineData((byte)3, PomanderRole.Dps)]
    [InlineData((byte)0, PomanderRole.Dps)]
    public void Roles_ComeFromTheClassJobSheet(byte sheetRole, PomanderRole role)
    {
        Assert.Equal(role, AutoPomanderPolicy.RoleOf(sheetRole));
    }

    [Fact]
    public void AnyOtherKind_GoesToOneToon_DamageFirst_WhateverBuffsAreRunning()
    {
        var buffed = Dps with { BuffSecondsLeft = 500f };
        Assert.Equal("Grakur", AutoPomanderPolicy.PickAny([Tank, Healer, buffed])!.Name);
        Assert.Equal("Saar", AutoPomanderPolicy.PickAny([Tank, Healer, buffed with { Alive = false }])!.Name);
        Assert.Equal("Korha", AutoPomanderPolicy.PickAny([Tank])!.Name);
        Assert.Null(AutoPomanderPolicy.PickAny([Tank with { Alive = false }]));
    }

    [Fact]
    public void ATurnNotTaken_PassesToTheNextToon()
    {
        // Damage first, then healer, then tank — each a turn after the one before.
        var queue = AutoPomanderPolicy.QueueAny([Tank, Healer, Dps]);
        Assert.Equal(["Grakur", "Saar", "Korha"], queue.Select(c => c.Name));

        Assert.True(AutoPomanderPolicy.IsTurn(0, TimeSpan.Zero));
        Assert.False(AutoPomanderPolicy.IsTurn(1, TimeSpan.FromSeconds(9)));
        Assert.True(AutoPomanderPolicy.IsTurn(1, AutoPomanderPolicy.TurnLength));
        Assert.False(AutoPomanderPolicy.IsTurn(2, TimeSpan.FromSeconds(19)));
        Assert.True(AutoPomanderPolicy.IsTurn(2, TimeSpan.FromSeconds(20)));
    }

    [Theory]
    [InlineData(7224u)] // landmine
    [InlineData(7225u)] // luring
    [InlineData(7226u)] // enfeebling
    [InlineData(7227u)] // impeding
    [InlineData(7228u)] // toading
    [InlineData(9210u)] // odder
    [InlineData(10278u)] // owlet
    [InlineData(11247u)] // fae
    public void ATrapGoingOff_IsRecognisedByItsLogMessage(uint row)
    {
        Assert.True(TrapTrigger.IsTriggered(row));
    }

    [Theory]
    [InlineData(7222u)] // a pomander returned to its coffer
    [InlineData(7223u)] // the coffer bares its fangs
    [InlineData(7229u)]
    [InlineData(9103u)] // "You discover a concealed trap." — found, not set off
    [InlineData(6999u)] // another duty's "A trap is triggered!"
    public void OtherMessages_AreNotATrapGoingOff(uint row)
    {
        Assert.False(TrapTrigger.IsTriggered(row));
    }

    [Theory]
    [InlineData(7228u)] // toading
    [InlineData(9210u)] // odder
    [InlineData(10278u)] // owlet
    [InlineData(11247u)] // fae
    public void ATransformingTrap_ShouldTransformTheOneWhoGotItsMessage(uint row)
    {
        Assert.True(TrapTrigger.WeSteppedOnIt(row, transformBefore: 0, transformNow: 7));
        // Still ourselves: somebody else stepped on it.
        Assert.False(TrapTrigger.WeSteppedOnIt(row, transformBefore: 0, transformNow: 0));
        // Already transformed when it went off (a pomander of rage, say): no evidence either way.
        Assert.False(TrapTrigger.WeSteppedOnIt(row, transformBefore: 4, transformNow: 4));
    }

    [Theory]
    [InlineData(7224u)] // landmine
    [InlineData(7225u)] // luring
    [InlineData(7226u)] // enfeebling
    [InlineData(7227u)] // impeding
    public void AnAreaTrap_NeverNamesItsStepper(uint row)
    {
        Assert.False(TrapTrigger.HitsOnlyTheStepper(row));
        Assert.False(TrapTrigger.WeSteppedOnIt(row, transformBefore: 0, transformNow: 7));
    }

    [Fact]
    public void AToonNotInTheQueue_NeverGetsATurn()
    {
        Assert.False(AutoPomanderPolicy.IsTurn(-1, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void TheSteelQueue_LeavesOutCoveredToons_AndKeepsTheRoleOrder()
    {
        var tankCovered = Tank with { BuffSecondsLeft = 478f };
        Assert.Equal(["Saar", "Grakur"], AutoPomanderPolicy.Queue(SelfBuffPomander.Steel, [Dps, Healer, tankCovered]).Select(c => c.Name));
    }

    [Theory]
    [InlineData(10u)] // witching: "all targets within range"
    [InlineData(32u)] // protomander of witching
    [InlineData(12u)] // rage
    [InlineData(13u)] // lust
    [InlineData(16u)] // resolution
    [InlineData(22u)] // dread
    public void WitchingAndTheTransformations_WaitForAFight(uint item)
    {
        Assert.Equal(PomanderTiming.InCombat, AutoPomanderPolicy.TimingOf(item));
    }

    [Theory]
    [InlineData(1u)]  // safety
    [InlineData(2u)]  // sight
    [InlineData(5u)]  // affluence
    [InlineData(9u)]  // fortune
    [InlineData(14u)] // intuition
    [InlineData(15u)] // raising
    [InlineData(19u)] // petrification: every enemy on the floor
    [InlineData(21u)] // storms
    [InlineData(36u)] // haste
    [InlineData(38u)] // devotion
    public void FloorAndPartyKinds_MayBeUsedAtAnyMoment(uint item)
    {
        Assert.Equal(PomanderTiming.Anytime, AutoPomanderPolicy.TimingOf(item));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void ThePouchIsFull_AtThree(int held, bool full)
    {
        Assert.Equal(full, AutoPomanderPolicy.IsFull(held));
    }

    [Fact]
    public void UsedPomanders_StackShort_WithRepeatsCounted()
    {
        var at = new DateTime(2026, 11, 21, 19, 0, 0, DateTimeKind.Utc);
        PomanderUse Use(uint id, string name) => new(18, id, name, "someone", at);

        var lines = PomanderLog.Stacked(
        [
            Use(2, "pomander of sight"), Use(9, "pomander of fortune"), Use(14, "pomander of intuition"),
            Use(37, "pomander of purification"), Use(14, "pomander of intuition"), Use(24, "protomander of sight"),
        ]);

        Assert.Equal(["sight ×2", "fortune", "intuition ×2", "purification"], lines);
    }

    [Fact]
    public void ANameInAnotherLanguage_IsKeptWhole()
    {
        var at = new DateTime(2026, 11, 21, 19, 0, 0, DateTimeKind.Utc);
        Assert.Equal(["Pomander der Sicht"], PomanderLog.Stacked([new PomanderUse(1, 2, "Pomander der Sicht", "someone", at)]));
    }
}
