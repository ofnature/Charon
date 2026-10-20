using System;
using Charon.Features.Tweaks;

namespace Charon.Tests.Features.Tweaks;

public sealed class CutsceneSkipPolicyTests
{
    private static CutsceneSkipDecision Decide(bool enabled = true, bool inCutscene = true, int yes = -1, bool otherList = false) =>
        CutsceneSkipPolicy.Decide(enabled, inCutscene, yes, otherList);

    [Fact]
    public void Off_OrNotInACutscene_DoesNothing()
    {
        Assert.Equal(CutsceneSkipAction.None, Decide(enabled: false).Action);
        Assert.Equal(CutsceneSkipAction.None, Decide(inCutscene: false).Action);
    }

    [Fact]
    public void InACutscene_AsksToSkip()
    {
        Assert.Equal(CutsceneSkipAction.Trigger, Decide().Action);
    }

    [Fact]
    public void ThePrompt_IsAnsweredYes_WithItsEntryIndex()
    {
        var decision = Decide(yes: 0);
        Assert.Equal(CutsceneSkipAction.ConfirmYes, decision.Action);
        Assert.Equal(0, decision.EntryIndex);
    }

    [Fact]
    public void ItNeverAsksOverAnotherChoice()
    {
        Assert.Equal(CutsceneSkipAction.None, Decide(otherList: true).Action);
    }

    [Fact]
    public void ALiveLease_WinsInEitherDirection_ThenTheToggleDecidesAgain()
    {
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var live = now.AddSeconds(30);
        var lapsed = now.AddSeconds(-1);

        Assert.True(CutsceneSkipPolicy.Effective(toggle: false, leaseSkip: true, live, now));   // a run skips on a played box
        Assert.False(CutsceneSkipPolicy.Effective(toggle: true, leaseSkip: false, live, now));  // roleplay: watch them
        Assert.True(CutsceneSkipPolicy.Effective(toggle: true, leaseSkip: false, lapsed, now)); // caller gone: toggle again
        Assert.False(CutsceneSkipPolicy.Effective(toggle: false, leaseSkip: true, lapsed, now));
    }

    [Fact]
    public void ThePromptIsKnownByItsTitle_OrByBeingATwoEntryListWithYes()
    {
        Assert.True(CutsceneSkipPolicy.LooksLikeSkipPrompt(titleMatches: true, entryCount: 2, yesIndex: 0));
        Assert.True(CutsceneSkipPolicy.LooksLikeSkipPrompt(titleMatches: false, entryCount: 2, yesIndex: 0));
        Assert.False(CutsceneSkipPolicy.LooksLikeSkipPrompt(titleMatches: false, entryCount: 3, yesIndex: 0));
        Assert.False(CutsceneSkipPolicy.LooksLikeSkipPrompt(titleMatches: false, entryCount: 2, yesIndex: -1));
    }

    [Fact]
    public void ThePrompt_IsRecognisedByTheGamesOwnText()
    {
        string[] skip = ["Skip cutscene?"];
        Assert.True(CutsceneSkipPolicy.IsSkipPrompt(["", "Skip cutscene? "], skip));
        Assert.False(CutsceneSkipPolicy.IsSkipPrompt(["Teleport to Limsa Lominsa?"], skip));
        Assert.False(CutsceneSkipPolicy.IsSkipPrompt(["Skip cutscene?"], []));
    }

    [Fact]
    public void YesIndex_FindsTheGamesYes_AndBlankTextMatchesNothing()
    {
        string[] yes = ["Yes."];
        Assert.Equal(0, CutsceneSkipPolicy.YesIndex(["Yes.", "No."], yes));
        Assert.Equal(1, CutsceneSkipPolicy.YesIndex(["No.", "yes."], yes));
        Assert.Equal(-1, CutsceneSkipPolicy.YesIndex(["No."], yes));
        Assert.Equal(-1, CutsceneSkipPolicy.YesIndex(["", "No."], [""]));
    }
}
