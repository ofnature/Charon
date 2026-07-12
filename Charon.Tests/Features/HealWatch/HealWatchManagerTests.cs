using Charon.Features.HealWatch;

namespace Charon.Tests.Features.HealWatch;

public sealed class HealWatchManagerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static HealWatchConfig DefaultConfig(bool outOfPartyOnly = true) =>
        new(Enabled: true, HealThreshold: 0.8f, EmergencyThreshold: 0.4f, OutOfPartyOnly: outOfPartyOnly);

    private static HealCandidate Toon(string name, float hp, bool inParty = false, uint entityId = 1000) =>
        new(name, entityId, hp, inParty);

    [Fact]
    public void BelowThreshold_ProducesIntent()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intents = manager.Evaluate(new[] { Toon("Styx", 0.75f) }, rotationEnabled: false, T0);

        var intent = Assert.Single(intents);
        Assert.Equal("Styx", intent.Name);
        Assert.False(intent.Emergency);
    }

    [Fact]
    public void AboveThreshold_Ignored()
    {
        var manager = new HealWatchManager(DefaultConfig());
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0.85f) }, false, T0));
    }

    [Fact]
    public void Emergency_JumpsQueue_AheadOfLowerUrgency()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intents = manager.Evaluate(
            new[] { Toon("Styx", 0.6f), Toon("Acheron", 0.35f), Toon("Lethe", 0.5f) }, false, T0);

        Assert.Equal(3, intents.Count);
        Assert.Equal("Acheron", intents[0].Name);
        Assert.True(intents[0].Emergency);
        Assert.Equal("Lethe", intents[1].Name); // then ascending HP
    }

    [Fact]
    public void DeadOrVitalless_Skipped()
    {
        // hp 0 = dead OR a Daedalus build without the hp field — never castable either way.
        var manager = new HealWatchManager(DefaultConfig());
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0f) }, false, T0));
    }

    [Fact]
    public void MissingEntityId_Skipped()
    {
        var manager = new HealWatchManager(DefaultConfig());
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0.5f, entityId: 0) }, false, T0));
    }

    [Fact]
    public void InParty_SkippedWhenOutOfPartyOnly()
    {
        var manager = new HealWatchManager(DefaultConfig(outOfPartyOnly: true));
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0.5f, inParty: true) }, false, T0));

        var permissive = new HealWatchManager(DefaultConfig(outOfPartyOnly: false));
        Assert.Single(permissive.Evaluate(new[] { Toon("Styx", 0.5f, inParty: true) }, false, T0));
    }

    [Fact]
    public void RotationEnabled_StandsDown()
    {
        var manager = new HealWatchManager(DefaultConfig());
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0.3f) }, rotationEnabled: true, T0));
    }

    [Fact]
    public void Disabled_Inert()
    {
        var manager = new HealWatchManager(DefaultConfig() with { Enabled = false });
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0.3f) }, false, T0));
    }

    [Fact]
    public void TargetCooldown_PreventsDoubleCast_OnStaleVitals()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intent = manager.Evaluate(new[] { Toon("Styx", 0.5f) }, false, T0)[0];
        manager.OnHealCast(intent, T0);

        // Heartbeat still reports the pre-heal HP two seconds later — must not re-cast.
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0.5f) }, false, T0.AddSeconds(2.0)));

        // After the target cooldown, a still-low toon is eligible again.
        var after = T0 + HealWatchManager.TargetCooldown + HealWatchManager.GlobalCooldown;
        Assert.Single(manager.Evaluate(new[] { Toon("Styx", 0.5f) }, false, after));
    }

    [Fact]
    public void GlobalCooldown_PacesCasts_AcrossTargets()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intent = manager.Evaluate(new[] { Toon("Styx", 0.5f), Toon("Acheron", 0.6f) }, false, T0)[0];
        manager.OnHealCast(intent, T0);

        // A DIFFERENT hurt toon must still wait out the global pacing.
        Assert.Empty(manager.Evaluate(new[] { Toon("Acheron", 0.6f) }, false, T0.AddSeconds(1.0)));
        Assert.Single(manager.Evaluate(new[] { Toon("Acheron", 0.6f) }, false, T0 + HealWatchManager.GlobalCooldown));
    }

    [Fact]
    public void HealLog_RecordsNewestFirst()
    {
        var manager = new HealWatchManager(DefaultConfig());
        manager.OnHealCast(new HealIntent("Styx", 1, Emergency: false), T0);
        manager.OnHealCast(new HealIntent("Acheron", 2, Emergency: true), T0.AddSeconds(5));

        Assert.Equal(2, manager.HealLog.Count);
        Assert.Equal("Acheron", manager.HealLog[0].Name);
        Assert.True(manager.HealLog[0].Emergency);
    }
}

public sealed class HealActionTableTests
{
    [Theory]
    [InlineData(HealActionTable.WhiteMage, 90, 135u)]  // Cure II at 30+
    [InlineData(HealActionTable.WhiteMage, 10, 120u)]  // Cure below 30
    [InlineData(HealActionTable.Conjurer, 15, 120u)]
    [InlineData(HealActionTable.Scholar, 90, 190u)]
    [InlineData(HealActionTable.Astrologian, 90, 3610u)] // Benefic II at 26+
    [InlineData(HealActionTable.Astrologian, 10, 3594u)]
    [InlineData(HealActionTable.Sage, 90, 24284u)]
    public void HealerJobs_GetLevelAppropriateHeal(uint job, int level, uint expected)
    {
        Assert.Equal(expected, HealActionTable.GetHealAction(job, level));
    }

    [Fact]
    public void NonHealerJob_GetsNothing()
    {
        Assert.Equal(0u, HealActionTable.GetHealAction(21, 90)); // WAR
        Assert.False(HealActionTable.IsHealerJob(21));
        Assert.True(HealActionTable.IsHealerJob(HealActionTable.Sage));
    }

    [Fact]
    public void UnderLeveled_GetsNothing()
    {
        Assert.Equal(0u, HealActionTable.GetHealAction(HealActionTable.WhiteMage, 1));
    }
}
