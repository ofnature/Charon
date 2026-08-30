using System.Linq;
using Charon.Features.DeepDungeon;

namespace Charon.Tests.Features.DeepDungeon;

public sealed class FloorMapTests
{
    private static ushort[] Empty() => new ushort[FloorMap.RoomCount];

    [Fact]
    public void EmptyFloor_HasNoExistingRooms()
    {
        var cells = FloorMap.Build(Empty());

        Assert.Equal(FloorMap.RoomCount, cells.Count);
        Assert.All(cells, c => Assert.False(c.Exists));
    }

    [Fact]
    public void GridCoordinates_AreRowMajor_FiveWide()
    {
        var cells = FloorMap.Build(Empty());

        Assert.Equal((0, 0), (cells[0].X, cells[0].Y));
        Assert.Equal((4, 0), (cells[4].X, cells[4].Y));
        Assert.Equal((0, 1), (cells[5].X, cells[5].Y));
        Assert.Equal((4, 4), (cells[24].X, cells[24].Y));
    }

    [Fact]
    public void FlagBits_MapToTheRightProperties()
    {
        var rooms = Empty();
        rooms[7] = FloorMap.ConnectionN | FloorMap.ConnectionE | FloorMap.Passage | FloorMap.Revealed;
        rooms[12] = FloorMap.ConnectionS | FloorMap.Return | FloorMap.Home;

        var cells = FloorMap.Build(rooms);

        var passage = cells[7];
        Assert.True(passage.Exists);
        Assert.True(passage.North);
        Assert.True(passage.East);
        Assert.False(passage.South);
        Assert.True(passage.IsPassage);
        Assert.True(passage.IsRevealed);

        var home = cells[12];
        Assert.True(home.South);
        Assert.True(home.IsReturn);
        Assert.True(home.IsHome);
        Assert.False(home.IsRevealed); // unrevealed rooms still carry their data — the point
    }

    [Fact]
    public void Counts_SeparateKnownFromRevealed()
    {
        // The reveal question the window answers visually: rooms whose layout the client holds
        // but the game has not shown yet count as known, not revealed.
        var rooms = Empty();
        rooms[0] = FloorMap.ConnectionE | FloorMap.Revealed;
        rooms[1] = FloorMap.ConnectionW; // known, unrevealed
        rooms[2] = FloorMap.ConnectionW | FloorMap.Revealed;

        var (known, revealed) = FloorMap.Counts(FloorMap.Build(rooms));

        Assert.Equal(3, known);
        Assert.Equal(2, revealed);
    }

    [Fact]
    public void ARoomWithOnlyAMarkerFlag_StillExists()
    {
        // Defensive: a cell carrying Return/Passage but no connections must not vanish.
        var rooms = Empty();
        rooms[3] = FloorMap.Return;

        Assert.True(FloorMap.Build(rooms)[3].Exists);
        Assert.Single(FloorMap.Build(rooms).Where(c => c.Exists));
    }
}
