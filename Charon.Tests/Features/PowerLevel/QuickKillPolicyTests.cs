using System.Collections.Generic;
using Charon.Features.PowerLevel;

namespace Charon.Tests.Features.PowerLevel;

public sealed class QuickKillPolicyTests
{
    private static readonly TagAction Instant = new(24, "Shield Lob", 15, 20f, false);
    private static readonly TagAction Cast = new(119, "Stone", 1, 25f, true);

    private static TagCandidate Mob(uint id, float distance, bool tagged = false, bool tried = false)
        => new(id, $"Mob {id}", distance, tagged, tried);

    private static QuickKillDecision Tag(IReadOnlyList<TagCandidate> mobs, TagAction? tag = null,
        bool rotation = false, bool party = true, bool casting = false, bool moving = false)
        => QuickKillPolicy.DecideTag(rotation, party, tag ?? Instant, casting, moving, mobs);

    private static QuickKillDecision Kill(IReadOnlyList<TagCandidate> mobs, uint current = 0,
        float reach = 25f, bool carry = true)
        => QuickKillPolicy.DecideKill(carry, reach, current, mobs);

    // --- KILL: the carry aims its rotation ---

    [Fact]
    public void Kill_TargetsTheNearestEngagedMob()
    {
        var decision = Kill([Mob(1, 18f), Mob(2, 7f), Mob(3, 12f)]);
        Assert.True(decision.HasTarget);
        Assert.Equal(2u, decision.TargetEntityId);
    }

    [Fact]
    public void Kill_SticksWithTheCurrentTargetUntilItDies()
    {
        // A closer mob turned up, but switching mid-kill throws away the damage already dealt.
        var decision = Kill([Mob(1, 3f), Mob(2, 20f)], current: 2);
        Assert.Equal(2u, decision.TargetEntityId);
        Assert.StartsWith("killing", decision.Reason);
    }

    [Fact]
    public void Kill_KeepsItsTarget_EvenOnceItStepsOutOfReach()
    {
        Assert.Equal(2u, Kill([Mob(2, 40f)], current: 2).TargetEntityId);
    }

    [Fact]
    public void Kill_MovesOnOnceTheTargetIsGone()
    {
        // The old target is no longer fighting the fleet (dead or reset) — the next one takes over.
        var decision = Kill([Mob(5, 9f)], current: 2);
        Assert.Equal(5u, decision.TargetEntityId);
    }

    [Fact]
    public void Kill_IgnoresMobsOutOfReach()
    {
        var decision = Kill([Mob(1, 30f)], reach: 25f);
        Assert.False(decision.HasTarget);
        Assert.Equal("engaged mobs out of reach", decision.Reason);
    }

    [Fact]
    public void Kill_NothingEngaged_IsIdle()
    {
        Assert.Equal("nothing is fighting the fleet", Kill([]).Reason);
    }

    [Fact]
    public void Kill_NoFleet_IsIdle()
    {
        Assert.False(Kill([Mob(1, 5f)], carry: false).HasTarget);
    }

    // --- TAG: a carried toon lands one hit ---

    [Fact]
    public void Tag_PicksTheNearestUntaggedMobInReach()
    {
        var decision = Tag([Mob(1, 15f), Mob(2, 6f), Mob(3, 11f)]);
        Assert.True(decision.HasTarget);
        Assert.Equal(2u, decision.TargetEntityId);
    }

    [Fact]
    public void Tag_NeverHitsAMobTwice()
    {
        var decision = Tag([Mob(1, 3f, tagged: true), Mob(2, 9f)]);
        Assert.Equal(2u, decision.TargetEntityId);
    }

    [Fact]
    public void Tag_AllTagged_IsIdleAndSaysSo()
    {
        var decision = Tag([Mob(1, 3f, tagged: true), Mob(2, 9f, tagged: true)]);
        Assert.False(decision.HasTarget);
        Assert.Equal("all 2 engaged mob(s) tagged", decision.Reason);
    }

    [Fact]
    public void Tag_OutOfReach_IsSkipped_NeverWalkedTo()
    {
        Assert.False(Tag([Mob(1, 20.5f)]).HasTarget); // Shield Lob reaches 20y
    }

    [Fact]
    public void Tag_RecentlyTried_WaitsForTheHitToLand()
    {
        Assert.Equal(2u, Tag([Mob(1, 5f, tried: true), Mob(2, 12f)]).TargetEntityId);
    }

    [Fact]
    public void Tag_CastWhileMoving_Holds_InstantDoesNot()
    {
        Assert.False(Tag([Mob(1, 5f)], Cast, moving: true).HasTarget);
        Assert.True(Tag([Mob(1, 5f)], Instant, moving: true).HasTarget);
    }

    [Fact]
    public void Tag_RotationRunning_StandsDown()
    {
        var decision = Tag([Mob(1, 5f)], rotation: true);
        Assert.False(decision.HasTarget);
        Assert.Contains("rotation", decision.Reason);
    }

    [Fact]
    public void Tag_Solo_HasNobodyToCarry()
    {
        Assert.False(Tag([Mob(1, 5f)], party: false).HasTarget);
    }

    [Fact]
    public void Tag_NoTagForThisJob_IsIdle()
    {
        Assert.False(QuickKillPolicy.DecideTag(false, true, null, false, false, [Mob(1, 5f)]).HasTarget);
    }

    [Fact]
    public void Tag_Casting_Waits()
    {
        Assert.False(Tag([Mob(1, 5f)], casting: true).HasTarget);
    }

    [Fact]
    public void Tag_NothingEngaged_IsIdle()
    {
        Assert.Equal("nothing engaged", Tag([]).Reason);
    }
}
