using System.Linq;
using Charon.Features.DeepDungeon;
using Charon.Ipc;

namespace Charon.Tests.Ipc;

public sealed class SpotRelayTests
{
    [Fact]
    public void ASeenFrameRoundTrips_AndIsMarkedSeen()
    {
        var json = SpotRelay.Seen("Korha Ishere", 1281, [new Spot(SpotKind.Trap, -377.31f, 0.1f, 174.42f, true)]);
        var message = SpotRelay.Parse(json)!;

        Assert.Equal(SpotRelay.ActSeen, message.Act);
        Assert.Equal(1281u, message.Territory);
        var spot = SpotRelay.SpotsOf(message).Single();
        Assert.Equal(SpotKind.Trap, spot.Kind);
        Assert.Equal(-377.31f, spot.X);
        Assert.True(spot.Seen);
    }

    [Fact]
    public void CoordinatesTravelUnrounded_SoAGridLineIsNeverCrossed()
    {
        // 0.1999 × 5 truncates to 0; rounded to 0.20 it would truncate to 1 — a second spot.
        var json = SpotRelay.Seen("a", 561, [new Spot(SpotKind.Hoard, 0.1999f, 0f, 0f, true)]);
        Assert.Equal(0.1999f, SpotRelay.SpotsOf(SpotRelay.Parse(json)!).Single().X);
    }

    [Fact]
    public void AnAnswerIsSplitIntoFrames_EachCarryingTheTotal()
    {
        var spots = Enumerable.Range(0, 95).Select(i => new Spot(SpotKind.Trap, i, 0f, i, false)).ToList();

        var frames = SpotRelay.Answer("a", "ask1", 561, spots);

        Assert.Equal(3, frames.Count); // 40 + 40 + 15
        var parsed = frames.Select(f => SpotRelay.Parse(f)!).ToList();
        Assert.All(parsed, m => Assert.Equal(95, m.Known));
        Assert.All(parsed, m => Assert.Equal("ask1", m.Id));
        Assert.Equal(95, parsed.Sum(m => m.Spots.Count));
    }

    [Fact]
    public void AnImportedGuessStaysAGuess_WhenShared()
    {
        var frames = SpotRelay.Answer("a", "ask1", 561, [new Spot(SpotKind.Trap, 1f, 0f, 1f, false)]);
        Assert.False(SpotRelay.SpotsOf(SpotRelay.Parse(frames[0])!).Single().Seen);
    }

    [Theory]
    [InlineData(500, 100, 0, true)]    // we know more than the asker, nobody has answered
    [InlineData(500, 500, 0, false)]   // the asker already knows as much
    [InlineData(100, 500, 0, false)]   // the asker knows more
    [InlineData(500, 100, 500, false)] // someone who knows as much already answered
    [InlineData(500, 100, 300, true)]  // someone answered, but we know more
    public void OnlyTheBoxThatKnowsMostAnswers(int mine, int asker, int bestOther, bool expected)
    {
        Assert.Equal(expected, SpotRelay.ShouldAnswer(mine, asker, bestOther));
    }

    [Fact]
    public void MalformedFramesAndRowsAreDropped()
    {
        Assert.Null(SpotRelay.Parse("not json"));
        Assert.Null(SpotRelay.Parse("{}"));

        var message = new SpotMessage { Act = SpotRelay.ActSeen, Spots = [[1f, 2f], [9f, 1f, 1f, 1f, 1f], [2f, 1f, 1f, 1f, 0f]] };
        Assert.Single(SpotRelay.SpotsOf(message));
    }
}
