using Charon.Features.Leveling;

namespace Charon.Tests.Features.Leveling;

public sealed class LevelingBlockerPolicyTests
{
    private static JobTrackDefinition GlaPld() =>
        new(1, ClassRowId: 1, ClassAbbr: "GLA", JobRowId: 19, JobAbbr: "PLD", StartsAdvanced: false);

    private static JobTrackDefinition Drk() =>
        new(21, ClassRowId: 32, ClassAbbr: "DRK", JobRowId: 32, JobAbbr: "DRK", StartsAdvanced: true);

    private static JobTrackState State(
        short level, bool jobUnlocked = false, bool hasGearset = true) =>
        new(level, 0, 1000, jobUnlocked, hasGearset);

    private static BlockerDecision Eval(
        JobTrackDefinition? def = null, short level = 42, bool jobUnlocked = true,
        bool hasGearset = true, int ex = 5) =>
        LevelingBlockerPolicy.Evaluate(def ?? GlaPld(), State(level, jobUnlocked, hasGearset), ex);

    // --- The to-do list: every blocker names what the operator can do about it ---

    [Fact]
    public void LockedTrack_SaysNotYet_NotNever()
    {
        var d = Eval(level: 0);

        Assert.Equal(LevelingBlocker.JobNotUnlocked, d.Blocker);
        Assert.True(d.Hard);
        Assert.Contains("joins the rotation", d.Text);
    }

    // --- Expansion-locked jobs: "unlock it" must not be said to an account that CANNOT ---

    [Fact]
    public void JobBeyondTheAccountsExpansions_SaysSo_InsteadOfPromisingItCanJoin()
    {
        // VPR needs Dawntrail (ex 5); a free trial account is Shadowbringers (ex 3). "Unlock it
        // and it joins the rotation" is a lie there — the honest text names the expansion.
        var vpr = new JobTrackDefinition(30, 41, "VPR", 41, "VPR", StartsAdvanced: true,
            RequiredExpansion: 5);
        var d = LevelingBlockerPolicy.Evaluate(vpr, State(0), maxExpansion: 3);

        Assert.Equal(LevelingBlocker.JobNotUnlocked, d.Blocker);
        Assert.True(d.Hard);
        Assert.Contains("Dawntrail", d.Text);
        Assert.Contains("not available", d.Text);
        Assert.DoesNotContain("joins the rotation", d.Text);
    }

    [Fact]
    public void SameJob_OnAnAccountThatHasTheExpansion_GetsTheFriendlyText()
    {
        // The gate self-heals: the day the trial's MaxExpansion moves up a row, the same locked
        // job flips back to "unlock it and it joins" with no code change.
        var vpr = new JobTrackDefinition(30, 41, "VPR", 41, "VPR", StartsAdvanced: true,
            RequiredExpansion: 5);
        var d = LevelingBlockerPolicy.Evaluate(vpr, State(0), maxExpansion: 5);

        Assert.Contains("joins the rotation", d.Text);
    }

    [Fact]
    public void AtTheAccountCeiling_IsTheOneBlockerWithNoToDo()
    {
        var d = Eval(level: 80, ex: 3);

        Assert.Equal(LevelingBlocker.AtLevelCap, d.Blocker);
        Assert.True(d.Hard);
        Assert.Contains("ceiling", d.Text);
    }

    [Fact]
    public void Below15_RecommendsClassHunts()
    {
        var d = Eval(level: 12, jobUnlocked: false);

        Assert.Equal(LevelingBlocker.BelowDungeonMinimum, d.Blocker);
        Assert.True(d.Hard);
        Assert.Contains("class hunts", d.Text);
        Assert.Contains("15", d.Text);
    }

    [Fact]
    public void ClassAt30_WithQuestPending_IsASoftStop()
    {
        // The game keeps levelling a class past 30, but it learns no more actions — a level 50
        // class runs on a level 30 kit. Soft: SealBreaker surfaces it rather than erroring.
        var d = Eval(level: 30, jobUnlocked: false);

        Assert.Equal(LevelingBlocker.AdvancedJobQuestPending, d.Blocker);
        Assert.False(d.Hard);
        Assert.Contains("GLA", d.Text);
        Assert.Contains("PLD", d.Text);
    }

    [Fact]
    public void ClassAt29_IsNotPendingYet()
    {
        Assert.Equal(LevelingBlocker.None, Eval(level: 29, jobUnlocked: false).Blocker);
    }

    [Fact]
    public void StartsAdvancedJob_NeverGetsTheQuestStop()
    {
        // DRK has no class quest — self-parenting rows are exempt from the 30 check entirely.
        Assert.Equal(LevelingBlocker.None, Eval(Drk(), level: 35, jobUnlocked: true).Blocker);
    }

    [Fact]
    public void NoGearset_IsHard_BecauseTheSwitcherNeverImprovises()
    {
        var d = Eval(hasGearset: false);

        Assert.Equal(LevelingBlocker.NoGearset, d.Blocker);
        Assert.True(d.Hard);
        Assert.Contains("PLD", d.Text); // names the job's set, since the job is unlocked
    }

    [Fact]
    public void NoGearset_BeforeTheJobQuest_NamesTheClass()
    {
        var d = Eval(level: 20, jobUnlocked: false, hasGearset: false);
        Assert.Contains("GLA", d.Text);
    }

    // --- Ordering: first match wins, and the order is deliberate ---

    [Fact]
    public void QuestPending_BeatsNoGearset()
    {
        // At 30 the quest changes which row the track targets — a gearset made first would be
        // for a class about to become a job, so the quest is the action to surface.
        var d = Eval(level: 30, jobUnlocked: false, hasGearset: false);
        Assert.Equal(LevelingBlocker.AdvancedJobQuestPending, d.Blocker);
    }

    [Fact]
    public void AccountCeiling_BeatsEverything_ExceptBeingLocked()
    {
        var d = Eval(level: 80, jobUnlocked: false, hasGearset: false, ex: 3);
        Assert.Equal(LevelingBlocker.AtLevelCap, d.Blocker);
    }

    [Fact]
    public void WorkableTrack_HasNoBlocker_AndNoText()
    {
        var d = Eval();

        Assert.Equal(LevelingBlocker.None, d.Blocker);
        Assert.False(d.Hard);
        Assert.Empty(d.Text);
    }

    [Fact]
    public void EveryRealBlocker_ExplainsItself()
    {
        BlockerDecision[] cases =
        [
            Eval(level: 0),
            Eval(level: 80, ex: 3),
            Eval(level: 12, jobUnlocked: false),
            Eval(level: 30, jobUnlocked: false),
            Eval(hasGearset: false),
        ];

        Assert.All(cases, c => Assert.NotEmpty(c.Text));
    }
}
