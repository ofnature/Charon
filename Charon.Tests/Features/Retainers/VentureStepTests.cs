using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class VentureStepTests
{
    private static readonly VentureMenuText Text = new(
        ViewReport: "View venture report. (Complete)",
        AssignIdle: "Assign venture.",
        AssignInProgress: "Assign venture. (In progress)",
        QuickExploration: "Quick Exploration.");

    private static VentureDecision Decide(
        bool armed = true,
        VentureScreen screen = VentureScreen.Menu,
        IReadOnlyList<string>? entries = null,
        bool reassign = false,
        bool confirm = false,
        bool assign = false,
        VentureMenuText? text = null,
        uint wantedTaskId = 0) =>
        VentureStep.Decide(armed, screen, entries ?? [], reassign, confirm, assign, text ?? Text, wantedTaskId);

    [Fact]
    public void VenturePicker_WithAPlan_ChoosesIt()
    {
        var d = Decide(screen: VentureScreen.TaskList, wantedTaskId: 903);

        Assert.Equal(VentureAction.PickVenture, d.Action);
        Assert.Contains("903", d.Reason);
    }

    [Fact]
    public void VenturePicker_WithNoPlan_DoesNothing()
    {
        // Clicking whatever is listed first would assign a venture nobody asked for, and charge for it.
        var d = Decide(screen: VentureScreen.TaskList, wantedTaskId: 0);

        Assert.Equal(VentureAction.None, d.Action);
        Assert.Equal("no venture planned for this retainer", d.Reason);
    }

    [Fact]
    public void APlan_DoesNotDisturbTheOtherScreens()
    {
        // The report still wins over the plan: a finished venture's rewards are collected first, or they
        // sit in the report while the retainer is sent straight back out.
        var d = Decide(screen: VentureScreen.TaskResult, reassign: true, wantedTaskId: 903);

        Assert.Equal(VentureAction.Reassign, d.Action);
    }

    [Fact]
    public void Disarmed_DoesNothing_WhateverIsOnScreen()
    {
        var d = Decide(armed: false, screen: VentureScreen.TaskAsk, assign: true);
        Assert.Equal(VentureAction.None, d.Action);
        Assert.Equal("not armed", d.Reason);
    }

    [Fact]
    public void NoWindowOpen_DoesNothing()
    {
        Assert.Equal(VentureAction.None, Decide(screen: VentureScreen.None).Action);
    }

    [Fact]
    public void FinishedVenture_PrefersReassign_SoTheChosenVentureIsKept()
    {
        var d = Decide(screen: VentureScreen.TaskResult, reassign: true, confirm: true);
        Assert.Equal(VentureAction.Reassign, d.Action);
    }

    [Fact]
    public void FinishedVenture_FallsBackToConfirm_WhenReassignIsUnavailable()
    {
        var d = Decide(screen: VentureScreen.TaskResult, reassign: false, confirm: true);
        Assert.Equal(VentureAction.Confirm, d.Action);
    }

    [Fact]
    public void GreyedButtons_AreWaitedFor_NeverForced()
    {
        Assert.Equal(VentureAction.None, Decide(screen: VentureScreen.TaskResult).Action);
        Assert.Equal(VentureAction.None, Decide(screen: VentureScreen.TaskAsk).Action);
    }

    [Fact]
    public void TaskAsk_ConfirmsTheVenture()
    {
        Assert.Equal(VentureAction.Assign, Decide(screen: VentureScreen.TaskAsk, assign: true).Action);
    }

    [Fact]
    public void Menu_CollectsBeforeAssigning()
    {
        // Both entries present: opening the report first is the only order that doesn't strand the
        // finished venture's rewards.
        var d = Decide(entries: ["Assign venture.", "View venture report. (Complete)", "Quit."]);
        Assert.Equal(VentureAction.SelectEntry, d.Action);
        Assert.Equal(1, d.EntryIndex);
    }

    [Fact]
    public void Menu_PicksQuickExploration_OnTheVentureCategoryList()
    {
        var d = Decide(entries: ["Botany.", "Quick Exploration.", "Return."]);
        Assert.Equal(1, d.EntryIndex);
    }

    [Fact]
    public void IdleRetainer_StartsTheAssignPath()
    {
        var d = Decide(entries: ["Assign venture.", "Quit."]);
        Assert.Equal(VentureAction.SelectEntry, d.Action);
        Assert.Equal(0, d.EntryIndex);
    }

    [Fact]
    public void RetainerAlreadyOut_IsLeftAlone_AndNeverChargedAgain()
    {
        // 2386 and 2387 are the retainer's STATE, not two spellings of one entry. Clicking the
        // in-progress entry assigns a REPLACEMENT venture and charges venture currency again —
        // seen live, immediately after a reassign had just sent her out.
        var d = Decide(entries: ["Assign venture. (In progress)", "Quit."]);
        Assert.Equal(VentureAction.None, d.Action);
        Assert.Contains("already out", d.Reason);
    }

    [Fact]
    public void BlankSheetText_NeverMatches()
    {
        // An unread Addon row would otherwise match every entry and click whatever sits first in
        // the menu — which on a retainer is "entrust gil".
        var blank = new VentureMenuText("", "", "", "");
        var d = Decide(entries: ["Entrust or withdraw gil.", "Quit."], text: blank);
        Assert.Equal(VentureAction.None, d.Action);
    }

    [Fact]
    public void AssignPath_IsNotStarted_WhenItCannotBeFinished()
    {
        // Without the quick exploration text we could open the venture list and then stall on it.
        // Leaving the retainer idle is the better failure.
        var noQuick = new VentureMenuText("View venture report. (Complete)", "Assign venture.", "", "");
        var d = Decide(entries: ["Assign venture.", "Quit."], text: noQuick);
        Assert.Equal(VentureAction.None, d.Action);
    }

    [Fact]
    public void ClosingTheRetainerList_DoesNotEndTheSession()
    {
        // Picking a retainer closes the list. Treating that as the end disarmed the assist the
        // moment anyone was selected, so it could never do a thing (found in live testing).
        Assert.False(VentureStep.SessionEnded(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5)));
        Assert.False(VentureStep.SessionEnded(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WalkingAwayFromTheBell_EndsTheSession()
    {
        Assert.True(VentureStep.SessionEnded(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Menu_WithNothingRelevant_DoesNothing()
    {
        var d = Decide(entries: ["Entrust or withdraw items.", "Quit."]);
        Assert.Equal(VentureAction.None, d.Action);
        Assert.Equal("nothing to do for this retainer", d.Reason);
    }

    /// <summary>
    /// The menu texts, verbatim from the game's Addon sheet: 2383 Quit, 2384 the dated report, 2385 the plain
    /// report, 2386 assign, 2387 assign in progress. The retainer menu in the wild shows the last of these
    /// alongside the report, which is why the cycle needs a way out.
    /// </summary>
    private static readonly VentureMenuText SheetText = new(
        "View venture report. (Complete)",
        "Assign venture.",
        "Assign venture. (In progress)",
        "Quick exploration.",
        "Quit.",
        "View venture report. (Complete on 27/8 8:00)");

    /// <summary>
    /// Once the cycle is done the run leaves the retainer. This is the state the user was left in: the send was
    /// out, so nothing more was clicked, and the retainer's menu stayed open on screen.
    /// </summary>
    [Fact]
    public void AFinishedCycleClosesTheRetainerMenu()
    {
        string[] menu = ["Entrust or withdraw items.", "View retainer attributes and gear.", "Assign venture. (In progress)", "Quit."];

        var decision = VentureStep.Decide(true, VentureScreen.Menu, menu, false, false, false, SheetText,
            closeWhenDone: true);

        Assert.Equal(VentureAction.Quit, decision.Action);
        Assert.Equal(3, decision.EntryIndex);
        Assert.Contains("closing", decision.Reason);
    }

    /// <summary>And without that flag nothing changes: an unfinished cycle must never close the menu early.</summary>
    [Fact]
    public void AFinishedCycleOnlyClosesWhenTheSendHasGoneOut()
    {
        string[] menu = ["View venture report. (Complete)", "Assign venture.", "Quit."];

        var decision = VentureStep.Decide(true, VentureScreen.Menu, menu, false, false, false, SheetText);

        Assert.Equal(VentureAction.SelectEntry, decision.Action);
        Assert.Equal(0, decision.EntryIndex);
    }

    /// <summary>
    /// The client words the report two ways — with the completion date and without — so a finished venture stays
    /// collectable instead of looking like a retainer with nothing to do.
    /// </summary>
    [Fact]
    public void EitherWordingOfTheReportCountsAsAReport()
    {
        string[] menu = ["View venture report. (Complete on 27/8 8:00)", "Assign venture. (In progress)", "Quit."];

        var decision = VentureStep.Decide(true, VentureScreen.Menu, menu, false, false, false, SheetText);

        Assert.Equal(VentureAction.SelectEntry, decision.Action);
        Assert.Equal(0, decision.EntryIndex);
        Assert.Equal("opening the venture report", decision.Reason);
    }

    /// <summary>An unread sheet row never matches, so a blank Quit stays put rather than clicking the last entry.</summary>
    [Fact]
    public void AnUnreadableQuitEntryIsNotClicked()
    {
        var noQuit = SheetText with { Quit = string.Empty };
        string[] menu = ["Assign venture. (In progress)", "Quit."];

        var decision = VentureStep.Decide(true, VentureScreen.Menu, menu, false, false, false, noQuit,
            closeWhenDone: true);

        Assert.NotEqual(VentureAction.Quit, decision.Action);
        Assert.Equal(VentureAction.None, decision.Action);
    }
}
