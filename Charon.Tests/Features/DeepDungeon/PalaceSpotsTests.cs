using System.Linq;
using Charon.Features.DeepDungeon;

namespace Charon.Tests.Features.DeepDungeon;

public sealed class PalaceSpotsTests
{
    private const uint Palace1To10 = 561;

    [Fact]
    public void TheSameSpotSeenTwice_CountsOnce()
    {
        // Palace Pal's 0.2y grid: two reads of one trap a hair apart land on the same key.
        var store = new SpotStore();
        Assert.True(store.Add(Palace1To10, SpotKind.Trap, 303.11f, 0f, -295.31f, seen: false));
        Assert.False(store.Add(Palace1To10, SpotKind.Trap, 303.15f, 0f, -295.35f, seen: false));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void ATrapAndAHoardOnOneSpot_AreDifferentSpots()
    {
        var store = new SpotStore();
        store.Add(Palace1To10, SpotKind.Trap, 1f, 0f, 1f, seen: false);
        store.Add(Palace1To10, SpotKind.Hoard, 1f, 0f, 1f, seen: false);
        Assert.Equal((1, 1), store.CountIn(Palace1To10));
    }

    [Fact]
    public void SeeingAnImportedSpot_UpgradesIt_AndReportsTheChange()
    {
        var store = new SpotStore();
        store.Add(Palace1To10, SpotKind.Trap, 1f, 0f, 1f, seen: false);

        Assert.True(store.Add(Palace1To10, SpotKind.Trap, 1f, 0f, 1f, seen: true));
        Assert.True(store.In(Palace1To10).Single().Seen);
        Assert.False(store.Add(Palace1To10, SpotKind.Trap, 1f, 0f, 1f, seen: false));
    }

    [Fact]
    public void FloorSetsAreKeptApart()
    {
        var store = new SpotStore();
        store.Add(561, SpotKind.Trap, 1f, 0f, 1f, seen: false);
        store.Add(770, SpotKind.Trap, 1f, 0f, 1f, seen: false);

        Assert.Single(store.In(561));
        Assert.Single(store.In(770));
        Assert.Empty(store.In(1099));
    }

    [Fact]
    public void UnknownKindsAndBrokenPositionsAreRefused()
    {
        var store = new SpotStore();
        Assert.False(store.Add(Palace1To10, (SpotKind)7, 1f, 0f, 1f, seen: false));
        Assert.False(store.Add(Palace1To10, SpotKind.Trap, float.NaN, 0f, 1f, seen: false));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void SavedRowsRoundTrip()
    {
        var store = new SpotStore();
        store.Add(561, SpotKind.Trap, 303.13f, -0.01f, -295.39f, seen: true);
        store.Add(1099, SpotKind.Hoard, -12.5f, 4f, 88f, seen: false);

        var back = SpotStore.FromRows(store.ToRows());

        Assert.Equal(2, back.Count);
        Assert.True(back.In(561).Single().Seen);
        Assert.Equal(SpotKind.Hoard, back.In(1099).Single().Kind);
    }

    [Fact]
    public void MergingAnotherClientsFile_AddsOnlyWhatIsNew()
    {
        // Two clients on one machine share the file; a save merges instead of overwriting.
        var mine = new SpotStore();
        mine.Add(561, SpotKind.Trap, 1f, 0f, 1f, seen: false);
        var theirs = new SpotStore();
        theirs.Add(561, SpotKind.Trap, 1f, 0f, 1f, seen: false);
        theirs.Add(561, SpotKind.Trap, 9f, 0f, 9f, seen: true);

        Assert.Equal(1, mine.Merge(theirs));
        Assert.Equal(2, mine.Count);
    }

    // --- Floor news, from the game's own log text (LogMessage rows 7255/7256/7272-7274). ---

    private static readonly FloorNewsTexts English = new(
        "The map for this floor has been revealed!",
        "All the traps in this floor have disappeared!",
        "You sense the Accursed Hoard calling you...",
        "You do not sense the call of the Accursed Hoard in this floor...",
        "You discover a piece of the Accursed Hoard!");

    [Theory]
    [InlineData("The map for this floor has been revealed!", FloorNews.TrapsRevealed)]
    [InlineData("All the traps in this floor have disappeared!", FloorNews.TrapsRemoved)]
    [InlineData("You sense the Accursed Hoard calling you...", FloorNews.HoardHere)]
    [InlineData("You do not sense the call of the Accursed Hoard in this floor...", FloorNews.NoHoard)]
    [InlineData("You discover a piece of the Accursed Hoard!", FloorNews.HoardFound)]
    [InlineData("You obtain a pomander of sight.", FloorNews.None)]
    [InlineData("", FloorNews.None)]
    public void TheLogIsReadByTheGamesOwnText(string message, FloorNews expected)
    {
        Assert.Equal(expected, English.Classify(message));
    }

    [Fact]
    public void BlankSheetTextMatchesNothing()
    {
        // An unreadable sheet must leave the spots up, never hide them on any line at all.
        var blank = new FloorNewsTexts("", "", "", "", "");
        Assert.Equal(FloorNews.None, blank.Classify("The map for this floor has been revealed!"));
    }

    [Fact]
    public void Sight_HidesTheTrapGuesses_ButNotTheHoardGuesses()
    {
        var intel = new FloorIntel();
        intel.Apply(FloorNews.TrapsRevealed);

        Assert.False(intel.Shows(SpotKind.Trap));
        Assert.True(intel.Shows(SpotKind.Hoard));
    }

    [Fact]
    public void Intuition_HidesTheHoardGuesses_WhicheverWayItAnswers()
    {
        foreach (var news in new[] { FloorNews.HoardHere, FloorNews.NoHoard, FloorNews.HoardFound })
        {
            var intel = new FloorIntel();
            intel.Apply(news);
            Assert.False(intel.Shows(SpotKind.Hoard));
            Assert.True(intel.Shows(SpotKind.Trap));
        }
    }

    [Fact]
    public void ANewFloorKnowsNothingAgain()
    {
        var intel = new FloorIntel();
        intel.Apply(FloorNews.TrapsRemoved);
        intel.Apply(FloorNews.NoHoard);

        intel.Reset();

        Assert.True(intel.Shows(SpotKind.Trap));
        Assert.True(intel.Shows(SpotKind.Hoard));
    }
}
