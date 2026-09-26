using Charon.Features.Tweaks;

namespace Charon.Tests.Features.Tweaks;

public class AfkGuardPolicyTests
{
    private static AfkDecision Decide(
        bool enabled = true,
        bool loggedIn = true,
        double[]? timers = null,
        int threshold = 600,
        double? sinceNudge = null,
        int stuck = 0) =>
        AfkGuardPolicy.Decide(enabled, loggedIn, timers ?? [12.0, 4.0, 9.5], threshold, sinceNudge, stuck);

    [Fact]
    public void Off_DoesNothing()
    {
        var decision = Decide(enabled: false, timers: [9999.0]);

        Assert.Equal(AfkAction.Idle, decision.Action);
        Assert.Equal("off", decision.Reason);
    }

    [Fact]
    public void NotLoggedIn_DoesNothing()
    {
        // There is no idle timer to beat at the title screen, and nothing to keep alive.
        var decision = Decide(loggedIn: false, timers: [9999.0]);

        Assert.Equal(AfkAction.Idle, decision.Action);
        Assert.Equal("not logged in", decision.Reason);
    }

    [Fact]
    public void UnreadableTimers_SaySoInsteadOfGuessing()
    {
        var decision = Decide(timers: []);

        Assert.Equal(AfkAction.Idle, decision.Action);
        Assert.Contains("not readable", decision.Reason);
    }

    [Fact]
    public void BelowTheThreshold_ReportsTheNumberItSaw()
    {
        var decision = Decide(timers: [212.4, 3.0, 0.0]);

        Assert.Equal(AfkAction.Idle, decision.Action);
        Assert.Equal("idle 212s of 600s", decision.Reason);
    }

    [Fact]
    public void AtTheThreshold_Nudges()
    {
        var decision = Decide(timers: [600.0, 12.0, 8.0]);

        Assert.Equal(AfkAction.Nudge, decision.Action);
    }

    [Fact]
    public void AFollowingBoxIsIdleToTheClient()
    {
        // The whole reason this exists: an hour of plugin-driven movement is not input, so a box that has
        // been following the fleet all session is an hour into a logout timer.
        var decision = Decide(timers: [3612.0, 0.0, 0.0], threshold: 600);

        Assert.Equal(AfkAction.Nudge, decision.Action);
        Assert.Contains("3612s", decision.Reason);
    }

    [Fact]
    public void TheWorstTimerDecides()
    {
        // All three matter: the content-input timer is the one that runs in duties.
        var decision = Decide(timers: [0.5, 700.0, 0.2]);

        Assert.Equal(AfkAction.Nudge, decision.Action);
    }

    [Fact]
    public void JustNudged_Waits()
    {
        var decision = Decide(timers: [640.0], sinceNudge: 5.0);

        Assert.Equal(AfkAction.Idle, decision.Action);
        Assert.Contains("nudged 5s ago", decision.Reason);
    }

    [Fact]
    public void CooldownExpired_NudgesAgain()
    {
        var decision = Decide(timers: [640.0], sinceNudge: AfkGuardPolicy.CooldownSeconds + 1);

        Assert.Equal(AfkAction.Nudge, decision.Action);
    }

    [Fact]
    public void ANudgeThatDoesNotResetTheTimer_StopsAndSaysWhy()
    {
        var decision = Decide(timers: [900.0], sinceNudge: 120.0, stuck: AfkGuardPolicy.NudgesBeforeGivingUp);

        Assert.Equal(AfkAction.Idle, decision.Action);
        Assert.Contains("not taking keystrokes", decision.Reason);
    }

    [Fact]
    public void NonsenseTimersAreIgnoredRatherThanNudgedOn()
    {
        // A NEGATIVE timer is a read that cannot be trusted; a nudge based on it would be a guess.
        var decision = Decide(timers: [-1.0, double.NaN, 30.0]);

        Assert.Equal(AfkAction.Idle, decision.Action);
        Assert.Equal("idle 30s of 600s", decision.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-600)]
    public void AThresholdOfZeroOrLessClampsToTheSettingsFloor(int nonsense)
    {
        // A hand-edited config must not turn this into a keystroke every cooldown, forever.
        var decision = Decide(timers: [1.0], threshold: nonsense);

        Assert.Equal(AfkAction.Idle, decision.Action);
        Assert.Equal($"idle 1s of {AfkGuardPolicy.MinimumThresholdSeconds}s", decision.Reason);
    }
}
