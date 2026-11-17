using System.Linq;
using Charon.Features.Tweaks;

namespace Charon.Tests.Features.Tweaks;

public sealed class WindowDockTests
{
    // A 1080p screen with a 48px taskbar.
    private static readonly Box Work = new(0, 0, 1920, 1032);

    // The user's desk, left to right: three 1080p screens, the centre one primary.
    private static readonly Screen[] Three =
    [
        new(new Box(-1920, 0, 1920, 1032), false),
        new(Work, true),
        new(new Box(1920, 0, 1920, 1032), false),
    ];

    [Fact]
    public void OneScreen_MainAndSlotsTileItExactly()
    {
        foreach (var slots in Enumerable.Range(WindowDock.MinSlots, WindowDock.MaxSlots))
        {
            var layout = WindowDock.Strip(Work, slots);
            Assert.Equal(new Box(0, 0, 1440, 1032), layout.Main);
            Assert.Equal(slots, layout.Slots.Count);
            Assert.All(layout.Slots, b => Assert.Equal((1440, 480), (b.Left, b.Width)));
            Assert.Equal(0, layout.Slots[0].Top);
            for (var i = 1; i < slots; i++)
                Assert.Equal(layout.Slots[i - 1].Top + layout.Slots[i - 1].Height, layout.Slots[i].Top);
            Assert.Equal(1032, layout.Slots[^1].Top + layout.Slots[^1].Height); // the strip reaches the bottom
        }
    }

    [Fact]
    public void TwoScreens_MainIsTheWholeFirst_AndSlotsGoTwoAcrossOnTheSecond()
    {
        var second = new Box(1920, 0, 1920, 1032);
        var three = WindowDock.TwoScreens(Work, second, 3);
        Assert.Equal(Work, three.Main);
        Assert.Equal(
            new[] { new Box(1920, 0, 960, 516), new Box(2880, 0, 960, 516), new Box(1920, 516, 960, 516) },
            three.Slots);

        Assert.Equal(new[] { second }, WindowDock.TwoScreens(Work, second, 1).Slots);
        Assert.Equal(new Box(2880, 688, 960, 344), WindowDock.TwoScreens(Work, second, 6).Slots[5]);
    }

    [Fact]
    public void Auto_PutsMainOnThePrimary_AndTheDockOnTheScreenToItsRight()
    {
        var layout = WindowDock.Layout(Three, WindowDock.Auto, WindowDock.Auto, 3)!;
        Assert.Equal(Work, layout.Main);
        Assert.Equal(new Box(1920, 0, 960, 516), layout.Slots[0]);
    }

    [Fact]
    public void TheScreens_CanBeChosen()
    {
        // Main on the right-hand screen, dock on the left-hand one.
        var layout = WindowDock.Layout(Three, 2, 0, 2)!;
        Assert.Equal(new Box(1920, 0, 1920, 1032), layout.Main);
        Assert.Equal(new Box(-1920, 0, 960, 1032), layout.Slots[0]);

        // Both on one screen: the strip.
        var strip = WindowDock.Layout(Three, 1, 1, 3)!;
        Assert.Equal(new Box(0, 0, 1440, 1032), strip.Main);
        Assert.Equal(new Box(1440, 0, 480, 344), strip.Slots[0]);
    }

    [Fact]
    public void AScreenThatIsGone_FallsBackToAuto()
    {
        var one = new[] { new Screen(Work, true) };
        var layout = WindowDock.Layout(one, 2, 2, 3)!;
        Assert.Equal(new Box(0, 0, 1440, 1032), layout.Main); // a strip on the only screen

        // Primary is the rightmost: the dock goes to the screen on its left.
        var two = new[] { new Screen(new Box(-1920, 0, 1920, 1032), false), new Screen(Work, true) };
        Assert.Equal(new Box(-1920, 0, 960, 516), WindowDock.Layout(two, WindowDock.Auto, WindowDock.Auto, 3)!.Slots[0]);

        Assert.Null(WindowDock.Layout([], WindowDock.Auto, WindowDock.Auto, 3));
    }

    [Fact]
    public void ScreenLabels_SayWhichIsWhich()
    {
        Assert.Equal("Screen 1 (left), 1920x1032", WindowDock.ScreenLabel(Three, 0));
        Assert.Equal("Screen 2 (centre), primary, 1920x1032", WindowDock.ScreenLabel(Three, 1));
        Assert.Equal("Screen 3 (right), 1920x1032", WindowDock.ScreenLabel(Three, 2));
    }

    [Fact]
    public void FreeSlot_IsTheFirstNobodyIsCentredIn()
    {
        var layout = WindowDock.Strip(Work, 3);
        Assert.Equal(0, WindowDock.FreeSlot(layout, []));
        Assert.Equal(1, WindowDock.FreeSlot(layout, [layout.Slots[0]]));
        Assert.Equal(0, WindowDock.FreeSlot(layout, [layout.Slots[1], layout.Main]));

        var two = WindowDock.Strip(Work, 2);
        Assert.Null(WindowDock.FreeSlot(two, [two.Slots[0], two.Slots[1]]));
    }

    [Fact]
    public void MainIsTaken_OnlyByAWindowCentredInIt()
    {
        var layout = WindowDock.Layout(Three, WindowDock.Auto, WindowDock.Auto, 3)!;
        Assert.True(WindowDock.MainTaken(layout, [layout.Main]));
        Assert.True(WindowDock.MainTaken(layout, [new Box(-8, -8, 1936, 1048)])); // a maximised client
        Assert.False(WindowDock.MainTaken(layout, [layout.Slots[0], layout.Slots[2]]));
        Assert.False(WindowDock.MainTaken(layout, [new Box(1918, 0, 1936, 1048)])); // one on the next screen
        Assert.False(WindowDock.MainTaken(layout, []));
    }

    [Fact]
    public void TwoClientsInOneSlot_TheHigherProcessIdMoves()
    {
        var layout = WindowDock.Strip(Work, 3);
        Assert.True(WindowDock.MustYieldSlot(layout, 0, 9000, [(7000, layout.Slots[0])]));
        Assert.False(WindowDock.MustYieldSlot(layout, 0, 7000, [(9000, layout.Slots[0])]));
        Assert.False(WindowDock.MustYieldSlot(layout, 0, 9000, [(7000, layout.Slots[1])]));
    }

    [Fact]
    public void ASlotThatNoLongerExists_IsGivenUp()
    {
        // The dock was cut from three slots to two while this client sat in the third.
        Assert.True(WindowDock.MustYieldSlot(WindowDock.Strip(Work, 2), 2, 7000, []));
    }

    [Fact]
    public void ADockedBot_GoesToTheDockAndStaysThere_EvenWhenClicked()
    {
        Assert.Equal(DockMove.ToDock, WindowDock.Decide(DockMode.Docked, DockPlace.None, focused: false, false));
        Assert.Equal(DockMove.Stay, WindowDock.Decide(DockMode.Docked, DockPlace.Dock, focused: true, false));
        // The mode was changed from Swap while it held the main region.
        Assert.Equal(DockMove.ToDock, WindowDock.Decide(DockMode.Docked, DockPlace.Main, focused: true, false));
    }

    [Fact]
    public void ASwapClient_TakesTheMainRegionWhenClicked()
    {
        Assert.Equal(DockMove.ToMain, WindowDock.Decide(DockMode.Swap, DockPlace.Dock, focused: true, true));
        Assert.Equal(DockMove.ToMain, WindowDock.Decide(DockMode.Swap, DockPlace.None, focused: true, false));
        Assert.Equal(DockMove.Stay, WindowDock.Decide(DockMode.Swap, DockPlace.Main, focused: true, true));
    }

    [Fact]
    public void TheMainClient_GivesWayOnlyToAClientThatTookItsPlace()
    {
        // Alt-tab to a browser, or a click on a docked bot that stays small: nothing took the main region.
        Assert.Equal(DockMove.Stay, WindowDock.Decide(DockMode.Swap, DockPlace.Main, focused: false, false));
        // Another swap client was clicked and has moved in.
        Assert.Equal(DockMove.ToDock, WindowDock.Decide(DockMode.Swap, DockPlace.Main, focused: false, true));
    }

    [Fact]
    public void AnUnfocusedSwapClient_StartsInTheDock_AndStaysThere()
    {
        Assert.Equal(DockMove.ToDock, WindowDock.Decide(DockMode.Swap, DockPlace.None, focused: false, true));
        Assert.Equal(DockMove.Stay, WindowDock.Decide(DockMode.Swap, DockPlace.Dock, focused: false, true));
    }

    [Fact]
    public void Off_NeverMovesAnything()
    {
        Assert.Equal(DockMove.Stay, WindowDock.Decide(DockMode.Off, DockPlace.None, focused: true, true));
    }
}
