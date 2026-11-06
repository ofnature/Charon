using System.Collections.Generic;
using Charon.Features.FcChest;

namespace Charon.Tests.Features.FcChest;

public sealed class ChestTabSearchTests
{
    private static bool NoneLoaded(int page) => false;

    [Fact]
    public void SnapshotGuessesComeFirst_ThenEveryOtherTab()
    {
        var order = ChestTabSearch.Order([4, 2], NoneLoaded);

        Assert.Equal(new[] { 4, 2, 1, 3, 5 }, order);
    }

    [Fact]
    public void NoSnapshot_LooksAtEveryTabInOrder()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ChestTabSearch.Order([], NoneLoaded));
    }

    [Fact]
    public void LoadedTabsAreNotLoadedAgain()
    {
        var loaded = new HashSet<int> { 1, 4 };

        var order = ChestTabSearch.Order([4, 3], loaded.Contains);

        Assert.Equal(new[] { 3, 2, 5 }, order); // tab 4 is already read live; the guess 3 still leads
    }

    [Fact]
    public void GuessesOutsideTheItemTabsAreIgnored()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ChestTabSearch.Order([0, 6, -1], NoneLoaded));
    }

    [Fact]
    public void StopsWhenTheLoadedTabsHoldEnough_OrNothingIsLeftToLoad()
    {
        Assert.True(ChestTabSearch.Done(loadedUnits: 5, amount: 5, tabsLeft: 3));
        Assert.True(ChestTabSearch.Done(loadedUnits: 0, amount: 5, tabsLeft: 0));  // not in the chest
        Assert.False(ChestTabSearch.Done(loadedUnits: 2, amount: 5, tabsLeft: 1)); // keep looking for more
    }
}
