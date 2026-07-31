using Charon.Features.Follow;

namespace Charon.Tests.Features.Follow;

public sealed class SprintPolicyTests
{
    private static SprintDecision Evaluate(
        bool enabled = true, bool inCombat = false, bool mounted = false,
        bool moving = true, bool actionReady = true) =>
        SprintPolicy.Evaluate(enabled, inCombat, mounted, moving, actionReady);

    [Fact]
    public void MovingOutOfCombat_Sprints()
    {
        Assert.True(Evaluate().Sprint);
    }

    [Fact]
    public void Disabled_DoesNothing()
    {
        Assert.False(Evaluate(enabled: false).Sprint);
    }

    [Fact]
    public void InCombat_NeverSprints()
    {
        // Sprint is barely useful in combat and competing for the action queue is the one thing
        // Charon never does — same reason Heal Watch stands down for the rotation.
        var d = Evaluate(inCombat: true);
        Assert.False(d.Sprint);
        Assert.Contains("combat", d.Reason);
    }

    [Fact]
    public void Mounted_DoesNotSprint()
    {
        var d = Evaluate(mounted: true);
        Assert.False(d.Sprint);
        Assert.Contains("mounted", d.Reason);
    }

    [Fact]
    public void StandingStill_DoesNotSprint()
    {
        // Sprinting on the spot burns a 60s cooldown for nothing.
        var d = Evaluate(moving: false);
        Assert.False(d.Sprint);
        Assert.Contains("standing", d.Reason);
    }

    [Fact]
    public void OnCooldown_DoesNotSprint()
    {
        var d = Evaluate(actionReady: false);
        Assert.False(d.Sprint);
        Assert.Contains("not available", d.Reason);
    }

    [Fact]
    public void CombatWinsOverEveryOtherCondition()
    {
        // Ordering matters: the combat gate must be reported even when something else also blocks.
        var d = Evaluate(inCombat: true, mounted: true, moving: false, actionReady: false);
        Assert.Contains("combat", d.Reason);
    }

    [Fact]
    public void EveryRefusalCarriesAReason()
    {
        Assert.NotEmpty(Evaluate(enabled: false).Reason);
        Assert.NotEmpty(Evaluate(inCombat: true).Reason);
        Assert.NotEmpty(Evaluate(mounted: true).Reason);
        Assert.NotEmpty(Evaluate(moving: false).Reason);
        Assert.NotEmpty(Evaluate(actionReady: false).Reason);
    }
}
