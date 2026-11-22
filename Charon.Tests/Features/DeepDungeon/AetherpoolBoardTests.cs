using System;
using System.Linq;
using Charon.Features.DeepDungeon;
using Charon.Ipc;

namespace Charon.Tests.Features.DeepDungeon;

public sealed class AetherpoolBoardTests
{
    private static readonly DateTime T0 = new(2026, 11, 21, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AReport_IsShownBesideTheSameKindOfDungeon_Only()
    {
        var board = new AetherpoolBoard();
        board.Upsert("Saar Ishere", 4, 12, 14, T0);

        var entry = board.For(4, T0).Single();
        Assert.Equal(("Saar Ishere", 12, 14), (entry.Name, entry.Arm, entry.Armor));
        // Each kind of deep dungeon keeps its own aetherpool levels.
        Assert.Empty(board.For(1, T0));
    }

    [Fact]
    public void ALaterReport_ReplacesTheEarlierOne()
    {
        var board = new AetherpoolBoard();
        board.Upsert("Saar Ishere", 4, 12, 14, T0);
        board.Upsert("saar ishere", 4, 13, 14, T0.AddSeconds(30));

        Assert.Equal(13, board.For(4, T0.AddSeconds(30)).Single().Arm);
    }

    [Fact]
    public void ABoxGoneQuiet_IsDropped_NotShownWithItsLastNumbers()
    {
        var board = new AetherpoolBoard();
        board.Upsert("Saar Ishere", 4, 12, 14, T0);

        Assert.Single(board.For(4, T0 + AetherpoolBoard.MaxAge));
        Assert.Empty(board.For(4, T0 + AetherpoolBoard.MaxAge + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void EveryoneIsListed_ByName()
    {
        var board = new AetherpoolBoard();
        board.Upsert("Saar Ishere", 4, 12, 14, T0);
        board.Upsert("Grakur Voidstep", 4, 3, 5, T0);

        Assert.Equal(["Grakur Voidstep", "Saar Ishere"], board.For(4, T0).Select(e => e.Name));
    }

    [Theory]
    [InlineData("", 4, 1, 1)]
    [InlineData("a", 0, 1, 1)]
    [InlineData("a", 4, -1, 1)]
    [InlineData("a", 4, 1, 100)]
    public void AnImpossibleReport_IsNotStored(string name, int dungeon, int arm, int armor)
    {
        var board = new AetherpoolBoard();
        Assert.False(board.Upsert(name, dungeon, arm, armor, T0));
        Assert.Empty(board.For(dungeon, T0));
    }

    [Fact]
    public void Describe_ReadsArmThenArmor()
    {
        Assert.Equal("+15 / +9", AetherpoolBoard.Describe(15, 9));
    }

    [Fact]
    public void AStateFrameRoundTrips()
    {
        var message = AetherpoolRelay.Parse(AetherpoolRelay.SerializeState("Korha Ishere", 4, 15, 15))!;

        Assert.Equal(AetherpoolRelay.ActState, message.Act);
        Assert.Equal(("Korha Ishere", 4, 15, 15), (message.Name, message.Dungeon, message.Arm, message.Armor));
    }

    [Fact]
    public void AnAskFrameRoundTrips_AndNeedsNoLevels()
    {
        var message = AetherpoolRelay.Parse(AetherpoolRelay.SerializeAsk("Korha Ishere"))!;
        Assert.Equal(AetherpoolRelay.ActAsk, message.Act);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"act\":\"state\",\"name\":\"\",\"dd\":4,\"arm\":1,\"armor\":1}")]
    [InlineData("{\"act\":\"state\",\"name\":\"a\",\"dd\":0,\"arm\":1,\"armor\":1}")]
    [InlineData("{\"act\":\"state\",\"name\":\"a\",\"dd\":4,\"arm\":150,\"armor\":1}")]
    [InlineData("{\"act\":\"dance\",\"name\":\"a\",\"dd\":4,\"arm\":1,\"armor\":1}")]
    public void AJunkFrame_IsNull(string? json)
    {
        Assert.Null(AetherpoolRelay.Parse(json));
    }
}
