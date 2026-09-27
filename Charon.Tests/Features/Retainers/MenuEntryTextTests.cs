using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class MenuEntryTextTests
{
    [Fact]
    public void TheSheetsPlaceholderCountDoesNotHaveToMatchTheLiveOne()
    {
        // Row 2378 is "Entrust or withdraw items. (Slots filled: 0)" in the sheet and "(Slots
        // filled: 172)" on screen. Exact equality reported "waiting for a bell" while the menu was
        // open; this is the fix.
        Assert.True(MenuEntryText.Matches(
            "Entrust or withdraw items. (Slots filled: 172)",
            "Entrust or withdraw items. (Slots filled: 0)"));
    }

    [Fact]
    public void GilEntrustedIsTheSameShape()
    {
        Assert.True(MenuEntryText.Matches(
            "Entrust or withdraw gil. (Gil entrusted: 10,638,259)",
            "Entrust or withdraw gil. (Gil entrusted: 0)"));
    }

    [Fact]
    public void EntriesWithoutASuffixStillMatchExactly()
    {
        Assert.True(MenuEntryText.Matches("Quit.", "Quit."));
    }

    [Fact]
    public void DifferentEntriesNeverMatch()
    {
        Assert.False(MenuEntryText.Matches(
            "Entrust or withdraw gil. (Gil entrusted: 10)",
            "Entrust or withdraw items. (Slots filled: 0)"));
        Assert.False(MenuEntryText.Matches("Quit.", "Entrust or withdraw items. (Slots filled: 0)"));
    }

    [Fact]
    public void BlankTextNeverMatches()
    {
        // An unreadable sheet row must do nothing, never click whatever sits first — which on this
        // menu is "Entrust or withdraw gil".
        Assert.False(MenuEntryText.Matches("Quit.", null));
        Assert.False(MenuEntryText.Matches("Quit.", "   "));
        Assert.False(MenuEntryText.Matches(null, "Quit."));
    }

    [Fact]
    public void StemCutsAtTheFirstParenthesis()
    {
        Assert.Equal("Reset retainer class.", MenuEntryText.Stem("Reset retainer class. (Miner: Lv. 87)"));
        Assert.Equal("Quit.", MenuEntryText.Stem("Quit."));
        Assert.Equal(string.Empty, MenuEntryText.Stem(null));
    }
}
