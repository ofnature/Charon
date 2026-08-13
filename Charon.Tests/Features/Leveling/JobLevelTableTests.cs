using Charon.Features.Leveling;

namespace Charon.Tests.Features.Leveling;

public sealed class JobLevelTableTests
{
    // GLA/PLD as the sheet has them: one track, ExpArrayIndex 1, PLD parented to GLA.
    private static JobTrackDefinition GlaPld() =>
        new(ExpArrayIndex: 1, ClassRowId: 1, ClassAbbr: "GLA", JobRowId: 19, JobAbbr: "PLD",
            StartsAdvanced: false);

    // DRK: self-parenting with JobIndex 12 — starts advanced, no class quest ever.
    private static JobTrackDefinition Drk() =>
        new(ExpArrayIndex: 21, ClassRowId: 32, ClassAbbr: "DRK", JobRowId: 32, JobAbbr: "DRK",
            StartsAdvanced: true);

    private static JobTrackState State(
        short level, long expInto = 0, int expToNextTotal = 1000,
        bool jobUnlocked = false, bool hasGearset = true) =>
        new(level, expInto, expToNextTotal, jobUnlocked, hasGearset);

    // --- Level cap: derived from the ExVersion row, never keyed on the free-trial flag ---

    [Theory]
    [InlineData(0, 50)]   // ARR
    [InlineData(3, 80)]   // Shadowbringers — the free trial today
    [InlineData(5, 100)]  // Dawntrail
    [InlineData(6, 110)]  // a future expansion degrades gracefully via the formula
    public void LevelCap_DerivesFromTheExpansionRow(int exVersion, int expected)
    {
        Assert.Equal(expected, JobLevelTable.LevelCapForExpansion(exVersion));
    }

    // --- Class/job pairing: one track, the row flips when the job quest completes ---

    [Fact]
    public void ClassWithoutJobQuest_ReportsTheClassRow()
    {
        var r = JobLevelTable.Compose(GlaPld(), State(42, jobUnlocked: false), 5);

        Assert.Equal(1u, r.RowId);
        Assert.Equal("GLA", r.Abbr);
        Assert.False(r.IsJob);
        Assert.Equal(string.Empty, r.ParentAbbr);
    }

    [Fact]
    public void JobQuestDone_ReportsTheJobRow_WithTheClassAsParent()
    {
        var r = JobLevelTable.Compose(GlaPld(), State(42, jobUnlocked: true), 5);

        Assert.Equal(19u, r.RowId);
        Assert.Equal("PLD", r.Abbr);
        Assert.True(r.IsJob);
        Assert.Equal("GLA", r.ParentAbbr);
    }

    [Fact]
    public void StartsAdvancedTrack_IsItsOwnJob_WithNoParent()
    {
        var r = JobLevelTable.Compose(Drk(), State(35), 5);

        Assert.Equal(32u, r.RowId);
        Assert.True(r.IsJob);
        Assert.Equal(string.Empty, r.ParentAbbr);
    }

    [Fact]
    public void LockedTrack_IsNotUnlocked_AndNeverAJob()
    {
        var r = JobLevelTable.Compose(Drk(), State(0), 5);

        Assert.False(r.Unlocked);
        Assert.False(r.IsJob);
        Assert.False(r.Capped);
    }

    // --- ExpToNext: null exactly where the game's EXP bar shows -/- ---

    [Fact]
    public void ExpToNext_IsTheRemainder_NotTheSheetTotal()
    {
        var r = JobLevelTable.Compose(GlaPld(), State(42, expInto: 300, expToNextTotal: 1000), 5);
        Assert.Equal(700L, r.ExpToNext);
    }

    [Fact]
    public void AtTheAccountCeiling_ExpToNext_IsNull_EvenThoughTheSheetHasANextLevel()
    {
        // The -/- case that motivated the field: level 80 on a Shadowbringers-capped account.
        // The SHEET still has exp values for 80→81, but the game shows -/- and none accrues.
        var r = JobLevelTable.Compose(GlaPld(), State(80, expToNextTotal: 5_000_000, jobUnlocked: true), 3);

        Assert.True(r.Capped);
        Assert.Null(r.ExpToNext);
    }

    [Fact]
    public void AtTheAbsoluteCap_SheetSaysZero_AndExpToNextIsNull()
    {
        var r = JobLevelTable.Compose(GlaPld(), State(100, expToNextTotal: 0, jobUnlocked: true), 5);

        Assert.True(r.Capped);
        Assert.Null(r.ExpToNext);
    }

    [Fact]
    public void LockedTrack_HasNoExpToNext()
    {
        Assert.Null(JobLevelTable.Compose(GlaPld(), State(0), 5).ExpToNext);
    }

    [Fact]
    public void BelowTheCeiling_IsNotCapped()
    {
        var r = JobLevelTable.Compose(GlaPld(), State(79, jobUnlocked: true), 3);
        Assert.False(r.Capped);
        Assert.NotNull(r.ExpToNext);
    }

    // --- The report embeds its own blocker, so one read answers "and why not" ---

    [Fact]
    public void ReportCarriesTheBlockerDecision()
    {
        var r = JobLevelTable.Compose(GlaPld(), State(12), 5);

        Assert.Equal(LevelingBlocker.BelowDungeonMinimum, r.Blocker);
        Assert.True(r.Hard);
        Assert.NotEmpty(r.BlockerText);
    }
}
