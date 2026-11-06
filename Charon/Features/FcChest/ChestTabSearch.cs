using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.FcChest;

/// <summary>
/// Which chest tabs a fetch for another plugin loads, and when it stops looking. A tab's contents are only in
/// memory once it has been viewed, so with nobody at the keyboard the fetch switches the chest to each tab it
/// needs. Pure: the adapter supplies "is this tab loaded" and the counts.
/// </summary>
public static class ChestTabSearch
{
    public const int ItemTabs = 5;

    /// <summary>
    /// Tabs to load, in order: the snapshot's guesses first, as given (most units first), then every other item
    /// tab 1-5. Tabs already loaded are left out — they are read live — and so is anything outside 1-5.
    /// </summary>
    public static IReadOnlyList<int> Order(IReadOnlyList<int> likelyPages, Func<int, bool> isLoaded) =>
        likelyPages.Where(p => p is >= 1 and <= ItemTabs)
            .Concat(Enumerable.Range(1, ItemTabs))
            .Distinct()
            .Where(p => !isLoaded(p))
            .ToList();

    /// <summary>Stop looking once the loaded tabs hold the amount, or when no tab is left to load.</summary>
    public static bool Done(int loadedUnits, int amount, int tabsLeft) => tabsLeft == 0 || loadedUnits >= amount;
}
