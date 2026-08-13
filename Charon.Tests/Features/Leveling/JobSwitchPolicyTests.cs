using Charon.Features.Leveling;

namespace Charon.Tests.Features.Leveling;

public sealed class JobSwitchPolicyTests
{
    private static JobSwitchDecision Eval(
        uint requested = 19, uint current = 1, bool hasGearset = true,
        bool busy = false, bool inCombat = false, bool inDuty = false) =>
        JobSwitchPolicy.Evaluate(requested, current, hasGearset, busy, inCombat, inDuty);

    [Fact]
    public void ValidRequest_Switches()
    {
        Assert.Equal(JobSwitchAction.Switch, Eval().Action);
    }

    [Fact]
    public void AlreadyOnTheJob_CompletesWithoutEquipping()
    {
        var d = Eval(requested: 19, current: 19);
        Assert.Equal(JobSwitchAction.AlreadyThere, d.Action);
    }

    [Fact]
    public void AlreadyThere_EvenWhenItWouldOtherwiseBeIllegal()
    {
        // A no-op needs nothing to be legal: mid-combat "switch to what I'm on" is success, and
        // the caller's wait loop must end the same way it would after a real switch.
        var d = Eval(requested: 19, current: 19, inCombat: true, inDuty: true, hasGearset: false);
        Assert.Equal(JobSwitchAction.AlreadyThere, d.Action);
    }

    [Fact]
    public void Busy_RefusesEvenANoOp()
    {
        // One operation at a time is the contract; while something runs the answer is always no,
        // so the caller can never interleave two operations' completion events.
        var d = Eval(requested: 19, current: 19, busy: true);
        Assert.Equal(JobSwitchAction.Refuse, d.Action);
        Assert.Contains("running", d.Reason);
    }

    [Theory]
    [InlineData(true, false, "combat")]
    [InlineData(false, true, "duty")]
    public void GameRefusalStates_AreRefusedWithTheReason(bool combat, bool duty, string word)
    {
        var d = Eval(inCombat: combat, inDuty: duty);
        Assert.Equal(JobSwitchAction.Refuse, d.Action);
        Assert.Contains(word, d.Reason);
    }

    [Fact]
    public void NoGearset_Refuses_TheSwitcherNeverImprovises()
    {
        var d = Eval(hasGearset: false);
        Assert.Equal(JobSwitchAction.Refuse, d.Action);
        Assert.Contains("gearset", d.Reason);
    }

    [Fact]
    public void NoJobRequested_Refuses()
    {
        Assert.Equal(JobSwitchAction.Refuse, Eval(requested: 0).Action);
    }

    [Fact]
    public void EveryRefusalCarriesAReason()
    {
        JobSwitchDecision[] cases =
        [
            Eval(requested: 0),
            Eval(busy: true),
            Eval(inCombat: true),
            Eval(inDuty: true),
            Eval(hasGearset: false),
        ];

        Assert.All(cases, c =>
        {
            Assert.Equal(JobSwitchAction.Refuse, c.Action);
            Assert.NotEmpty(c.Reason);
        });
    }
}
