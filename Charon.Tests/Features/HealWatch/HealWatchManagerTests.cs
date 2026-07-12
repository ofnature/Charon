using Charon.Features.HealWatch;

namespace Charon.Tests.Features.HealWatch;

public sealed class HealWatchManagerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static HealWatchConfig DefaultConfig(
        bool outOfPartyOnly = true, bool maintainHot = true, bool raiseDead = true) =>
        new(Enabled: true, HealThreshold: 0.8f, EmergencyThreshold: 0.4f,
            OutOfPartyOnly: outOfPartyOnly, MaintainHot: maintainHot, RaiseDead: raiseDead);

    private static HealCandidate Toon(string name, float hp, bool inParty = false, uint entityId = 1000) =>
        new(name, entityId, hp, inParty);

    private static IReadOnlyList<HealIntent> Eval(HealWatchManager m, DateTime now, params HealCandidate[] toons) =>
        m.Evaluate(toons, rotationEnabled: false, canHot: true, canRaise: true, now);

    // --- Heals ---

    [Fact]
    public void BelowThreshold_ProducesHealIntent()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intents = Eval(manager, T0, Toon("Styx", 0.75f));

        Assert.Contains(intents, i => i is { Name: "Styx", Kind: HealKind.Heal, Emergency: false });
    }

    [Fact]
    public void AboveThreshold_NoHeal_ButNoDamage_NoHotEither()
    {
        var manager = new HealWatchManager(DefaultConfig());
        Assert.Empty(Eval(manager, T0, Toon("Styx", 1.0f)));
    }

    [Fact]
    public void AboveHealThreshold_ButDamaged_StillGetsHotUpkeep()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intents = Eval(manager, T0, Toon("Styx", 0.9f));

        var intent = Assert.Single(intents);
        Assert.Equal(HealKind.Hot, intent.Kind);
    }

    // --- Priority ordering: emergency heal → raise → heal → hot ---

    [Fact]
    public void Ordering_EmergencyThenRaiseThenHealThenHot()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intents = Eval(manager, T0,
            Toon("Hurt", 0.6f),       // heal (+hot)
            Toon("Dead", 0f),         // raise
            Toon("Dying", 0.3f),      // emergency heal (+hot)
            Toon("Scratched", 0.95f)); // hot only

        Assert.Equal(HealKind.Heal, intents[0].Kind);
        Assert.Equal("Dying", intents[0].Name);
        Assert.True(intents[0].Emergency);

        Assert.Equal(HealKind.Raise, intents[1].Kind);
        Assert.Equal("Dead", intents[1].Name);

        Assert.Equal(HealKind.Heal, intents[2].Kind);
        Assert.Equal("Hurt", intents[2].Name);

        Assert.Equal(HealKind.Hot, intents[^1].Kind); // hots trail everything
    }

    // --- Raises ---

    [Fact]
    public void DeadToon_ProducesRaiseIntent()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intent = Assert.Single(Eval(manager, T0, Toon("Styx", 0f)));

        Assert.Equal(HealKind.Raise, intent.Kind);
        Assert.True(intent.Emergency);
    }

    [Fact]
    public void RaiseDisabled_DeadToonIgnored()
    {
        var manager = new HealWatchManager(DefaultConfig(raiseDead: false));
        Assert.Empty(Eval(manager, T0, Toon("Styx", 0f)));
    }

    [Fact]
    public void NoRaiseCapability_DeadToonIgnored()
    {
        var manager = new HealWatchManager(DefaultConfig());
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0f) }, false, canHot: true, canRaise: false, T0));
    }

    [Fact]
    public void RaiseCooldown_CoversTheHardcast()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intent = Eval(manager, T0, Toon("Styx", 0f))[0];
        manager.OnHealCast(intent, T0);

        // Still casting / raise pending — heartbeat still says dead 10s later.
        var during = T0.AddSeconds(10);
        Assert.Empty(Eval(manager, during, Toon("Styx", 0f)));

        // After the raise cooldown, a still-dead toon is eligible again.
        var after = T0 + HealWatchManager.RaiseCooldown + HealWatchManager.GlobalCooldown;
        Assert.Single(Eval(manager, after, Toon("Styx", 0f)));
    }

    // --- HoT upkeep ---

    [Fact]
    public void HotDisabled_NoHotIntents()
    {
        var manager = new HealWatchManager(DefaultConfig(maintainHot: false));
        Assert.Empty(Eval(manager, T0, Toon("Styx", 0.9f)));
    }

    [Fact]
    public void NoHotCapability_NoHotIntents()
    {
        var manager = new HealWatchManager(DefaultConfig());
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0.9f) }, false, canHot: false, canRaise: true, T0));
    }

    [Fact]
    public void HotCooldown_AbsorbsStatusCheckChurn()
    {
        var manager = new HealWatchManager(DefaultConfig());
        var intent = Eval(manager, T0, Toon("Styx", 0.9f))[0];
        manager.OnHealCast(intent, T0);

        var during = T0 + HealWatchManager.GlobalCooldown;
        Assert.Empty(Eval(manager, during, Toon("Styx", 0.9f)));

        var after = T0 + HealWatchManager.HotCooldown + HealWatchManager.GlobalCooldown;
        Assert.Single(Eval(manager, after, Toon("Styx", 0.9f)));
    }

    // --- Gates shared by all kinds ---

    [Fact]
    public void MissingEntityId_Skipped()
    {
        var manager = new HealWatchManager(DefaultConfig());
        Assert.Empty(Eval(manager, T0, Toon("Styx", 0.5f, entityId: 0)));
    }

    [Fact]
    public void InParty_SkippedWhenOutOfPartyOnly()
    {
        var manager = new HealWatchManager(DefaultConfig(outOfPartyOnly: true));
        Assert.Empty(Eval(manager, T0, Toon("Styx", 0.5f, inParty: true)));

        var permissive = new HealWatchManager(DefaultConfig(outOfPartyOnly: false));
        Assert.NotEmpty(Eval(permissive, T0, Toon("Styx", 0.5f, inParty: true)));
    }

    [Fact]
    public void RotationEnabled_StandsDown()
    {
        var manager = new HealWatchManager(DefaultConfig());
        Assert.Empty(manager.Evaluate(new[] { Toon("Styx", 0.3f) }, rotationEnabled: true, true, true, T0));
    }

    [Fact]
    public void Disabled_Inert()
    {
        var manager = new HealWatchManager(DefaultConfig() with { Enabled = false });
        Assert.Empty(Eval(manager, T0, Toon("Styx", 0.3f)));
    }

    [Fact]
    public void TargetCooldown_PreventsDoubleCast_OnStaleVitals()
    {
        var manager = new HealWatchManager(DefaultConfig(maintainHot: false));
        var intent = Eval(manager, T0, Toon("Styx", 0.5f))[0];
        manager.OnHealCast(intent, T0);

        // Heartbeat still reports the pre-heal HP two seconds later — must not re-cast.
        Assert.Empty(Eval(manager, T0.AddSeconds(2.0), Toon("Styx", 0.5f)));

        // After the target cooldown, a still-low toon is eligible again.
        var after = T0 + HealWatchManager.HealCooldown + HealWatchManager.GlobalCooldown;
        Assert.NotEmpty(Eval(manager, after, Toon("Styx", 0.5f)));
    }

    [Fact]
    public void GlobalCooldown_PacesCasts_AcrossTargets()
    {
        var manager = new HealWatchManager(DefaultConfig(maintainHot: false));
        var intent = Eval(manager, T0, Toon("Styx", 0.5f), Toon("Acheron", 0.6f))[0];
        manager.OnHealCast(intent, T0);

        // A DIFFERENT hurt toon must still wait out the global pacing.
        Assert.Empty(Eval(manager, T0.AddSeconds(1.0), Toon("Acheron", 0.6f)));
        Assert.NotEmpty(Eval(manager, T0 + HealWatchManager.GlobalCooldown, Toon("Acheron", 0.6f)));
    }

    [Fact]
    public void HealLog_RecordsNewestFirst_WithKind()
    {
        var manager = new HealWatchManager(DefaultConfig());
        manager.OnHealCast(new HealIntent("Styx", 1, HealKind.Heal, Emergency: false), T0);
        manager.OnHealCast(new HealIntent("Acheron", 2, HealKind.Raise, Emergency: true), T0.AddSeconds(5));

        Assert.Equal(2, manager.HealLog.Count);
        Assert.Equal("Acheron", manager.HealLog[0].Name);
        Assert.Equal(HealKind.Raise, manager.HealLog[0].Kind);
    }
}

public sealed class HealActionTableTests
{
    [Fact]
    public void WhiteMage_FullKit_AtHighLevel()
    {
        var kit = HealActionTable.GetKit(HealActionTable.WhiteMage, 80)!;
        Assert.Equal(135u, kit.HealAction);   // Cure II
        Assert.Equal(137u, kit.HotAction);    // Regen
        Assert.Equal(158u, kit.HotStatusId);
        Assert.Equal(125u, kit.RaiseAction);  // Raise
    }

    [Fact]
    public void WhiteMage_LowLevel_NoRegenYet()
    {
        var kit = HealActionTable.GetKit(HealActionTable.WhiteMage, 32)!;
        Assert.Equal(135u, kit.HealAction);
        Assert.Equal(0u, kit.HotAction); // Regen is Lv35
        Assert.Equal(125u, kit.RaiseAction);
    }

    [Theory]
    [InlineData(HealActionTable.Scholar, 90, 190u, 185u, 297u, 173u)]     // Physick / Adloquium / Resurrection
    [InlineData(HealActionTable.Astrologian, 90, 3610u, 3595u, 835u, 3603u)] // Benefic II / Aspected Benefic / Ascend
    public void OtherHealers_GetTheirKits(uint job, int level, uint heal, uint hot, uint hotStatus, uint raise)
    {
        var kit = HealActionTable.GetKit(job, level)!;
        Assert.Equal(heal, kit.HealAction);
        Assert.Equal(hot, kit.HotAction);
        Assert.Equal(hotStatus, kit.HotStatusId);
        Assert.Equal(raise, kit.RaiseAction);
    }

    [Fact]
    public void Sage_HasNoHot_ButRaises()
    {
        var kit = HealActionTable.GetKit(HealActionTable.Sage, 90)!;
        Assert.Equal(24284u, kit.HealAction); // Diagnosis
        Assert.Equal(0u, kit.HotAction);      // Eukrasia two-step out of scope
        Assert.Equal(24287u, kit.RaiseAction); // Egeiro
    }

    [Fact]
    public void NonHealerJob_GetsNothing()
    {
        Assert.Null(HealActionTable.GetKit(21, 90)); // WAR
        Assert.False(HealActionTable.IsHealerJob(21));
        Assert.True(HealActionTable.IsHealerJob(HealActionTable.Sage));
    }

    [Fact]
    public void UnderLeveled_EmptyHealSlot()
    {
        var kit = HealActionTable.GetKit(HealActionTable.WhiteMage, 1)!;
        Assert.Equal(0u, kit.HealAction);
        Assert.Equal(0u, kit.RaiseAction); // Raise is Lv12
    }
}
