using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class RetainerSelectPolicyTests
{
    private static SelectDecision Decide(
        bool enabled = true, string? wanted = "T'sola", string? open = null,
        string? menu = null, bool listVisible = true, bool tidyUp = false) =>
        RetainerSelectPolicy.Decide(enabled, wanted, open, menu, listVisible, tidyUp);

    [Fact]
    public void Disabled_DoesNothing() => Assert.Equal(SelectAction.None, Decide(enabled: false).Action);

    [Fact]
    public void NothingWanted_DoesNothing()
    {
        Assert.Equal(SelectAction.None, Decide(wanted: null).Action);
        Assert.Equal(SelectAction.None, Decide(wanted: "  ").Action);
    }

    [Fact]
    public void NoBellAtAll_NeverActs_BecauseCharonDoesNotWalkYouThere()
    {
        // The doctrine this must not break: Charon never takes a character to a bell.
        var d = Decide(listVisible: false);
        Assert.Equal(SelectAction.None, d.Action);
        Assert.Contains("waiting for a bell", d.Reason);
    }

    [Fact]
    public void StandingInTheRightMenu_OpensItems_NotWaitsForABell()
    {
        // The bug this test exists for: selecting a retainer CLOSES the list, so a check for "is the
        // list visible?" went false exactly when we were deepest in, and the status read
        // "waiting for a bell" while the retainer's own menu was on screen.
        var d = Decide(wanted: "T'sala", menu: "T'sala", listVisible: false);
        Assert.Equal(SelectAction.OpenItems, d.Action);
        Assert.Equal("T'sala", d.Retainer);
    }

    [Fact]
    public void AMenuIsNotBags()
    {
        // Standing in the menu is not the same as having the contents readable.
        Assert.NotEqual(SelectAction.Ready, Decide(wanted: "T'sala", menu: "T'sala").Action);
    }

    [Fact]
    public void RightBagsOpen_IsReady()
    {
        Assert.Equal(SelectAction.Ready, Decide(wanted: "T'sola", open: "T'sola", menu: "T'sola").Action);
    }

    [Fact]
    public void WrongBagsOpen_AreClosedFirst()
    {
        var d = Decide(wanted: "T'sola", open: "T'sala", menu: "T'sala");
        Assert.Equal(SelectAction.CloseItems, d.Action);
        Assert.Equal("T'sala", d.Retainer);
    }

    [Fact]
    public void WrongMenu_IsQuit()
    {
        var d = Decide(wanted: "T'sola", menu: "T'sala", listVisible: false);
        Assert.Equal(SelectAction.QuitOpen, d.Action);
        Assert.Equal("T'sala", d.Retainer);
    }

    [Fact]
    public void ListUpAndNobodyEngaged_Selects()
    {
        var d = Decide();
        Assert.Equal(SelectAction.Select, d.Action);
        Assert.Equal("T'sola", d.Retainer);
    }

    [Fact]
    public void WhenThePassIsDone_ItClosesTheLastRetainersBags()
    {
        // Live run: both retainers captured, then it stopped with T'sola's bags still open.
        var d = Decide(wanted: null, open: "T'sola", menu: "T'sola", listVisible: false, tidyUp: true);
        Assert.Equal(SelectAction.CloseItems, d.Action);
        Assert.Equal("T'sola", d.Retainer);
    }

    [Fact]
    public void ThenItQuits_LeavingThePlayerAtTheList()
    {
        var d = Decide(wanted: null, menu: "T'sola", listVisible: false, tidyUp: true);
        Assert.Equal(SelectAction.QuitOpen, d.Action);
    }

    [Fact]
    public void BackAtTheList_TidyingIsOver()
    {
        Assert.Equal(SelectAction.None, Decide(wanted: null, listVisible: true, tidyUp: true).Action);
    }

    [Fact]
    public void ASessionThePlayerDroveIsNeverTidied()
    {
        // Without tidyUp, an open retainer with nothing wanted is the PLAYER's business.
        Assert.Equal(SelectAction.None, Decide(wanted: null, open: "T'sola", menu: "T'sola").Action);
    }

    [Fact]
    public void ThePreviousRetainerLingering_IsNotTheNewOneArriving()
    {
        // Live trace: T'sala was still fading out when T'sola was chosen, and counting her as the
        // arrival let Select(T'sola) fire twice.
        Assert.False(RetainerSelectPolicy.SelectionLanded("T'sola", openRetainer: null, atBell: "T'sala"));
        Assert.True(RetainerSelectPolicy.SelectionLanded("T'sola", openRetainer: null, atBell: "T'sola"));
        Assert.True(RetainerSelectPolicy.SelectionLanded("T'sola", openRetainer: "T'sola", atBell: null));
    }

    [Fact]
    public void BagsJustClosed_IsNotTheListBack()
    {
        // Live trace: the session ended in the gap between T'sola's bags closing and her menu
        // returning, and the player was left standing in her menu.
        Assert.False(RetainerSelectPolicy.BackAtTheList(listVisible: false, openRetainer: null, menuRetainer: null));
        Assert.False(RetainerSelectPolicy.BackAtTheList(listVisible: false, openRetainer: null, menuRetainer: "T'sola"));
        Assert.True(RetainerSelectPolicy.BackAtTheList(listVisible: true, openRetainer: null, menuRetainer: null));
    }

    [Fact]
    public void NamesAreMatchedCaseInsensitively()
    {
        Assert.Equal(SelectAction.Ready, Decide(wanted: "T'sola", open: "t'SOLA").Action);
    }

    [Fact]
    public void TheWholeCycleAdvances_WithoutEverNeedingTheList()
    {
        // select → open items → read → close → quit → list → next, which is what one bell session
        // looks like when two retainers are being visited.
        Assert.Equal(SelectAction.Select, Decide(wanted: "A").Action);
        Assert.Equal(SelectAction.OpenItems, Decide(wanted: "A", menu: "A", listVisible: false).Action);
        Assert.Equal(SelectAction.Ready, Decide(wanted: "A", open: "A", menu: "A", listVisible: false).Action);

        // The pass finishes with A, so the next one is wanted: bags close, menu quits, list returns.
        Assert.Equal(SelectAction.CloseItems, Decide(wanted: "B", open: "A", menu: "A", listVisible: false).Action);
        Assert.Equal(SelectAction.QuitOpen, Decide(wanted: "B", menu: "A", listVisible: false).Action);
        Assert.Equal(SelectAction.Select, Decide(wanted: "B", listVisible: true).Action);
    }
}
