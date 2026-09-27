using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class RetainerRosterTests
{
    [Fact]
    public void AnUnloadedRosterIsNotAnEmptyOne()
    {
        // The game reloads the roster while a retainer is being dismissed. Reading that moment as
        // "no retainers" stopped a live refresh halfway, on a character with two.
        Assert.Equal(RosterState.NotLoaded, RetainerRoster.Classify(loaded: false, count: 0));
    }

    [Fact]
    public void OnlyALoadedRosterCanSayNone()
    {
        Assert.Equal(RosterState.Empty, RetainerRoster.Classify(loaded: true, count: 0));
    }

    [Fact]
    public void ALoadedRosterWithRetainersIsPresent()
    {
        Assert.Equal(RosterState.Present, RetainerRoster.Classify(loaded: true, count: 2));
    }

    [Fact]
    public void ARosterCannotEmptyMidSession_EvenIfItClaimsToBeLoaded()
    {
        // Having seen retainers this pass, an empty read is a reload however it is flagged: a
        // character does not lose its retainers while standing at the bell.
        Assert.Equal(RosterState.NotLoaded, RetainerRoster.Classify(loaded: true, count: 0, seenThisPass: true));
    }

    [Fact]
    public void AStaleCountWithoutALoadedFlagIsStillNotLoaded()
    {
        // Whatever a count says, it only means something once the game has handed the roster over.
        Assert.Equal(RosterState.NotLoaded, RetainerRoster.Classify(loaded: false, count: 2));
    }
}
