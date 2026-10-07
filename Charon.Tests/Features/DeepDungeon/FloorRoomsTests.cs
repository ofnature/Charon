using System.Collections.Generic;
using Charon.Features.DeepDungeon;

namespace Charon.Tests.Features.DeepDungeon;

/// <summary>
/// Box figures are the real ones from Palace of the Dead 1-10 (territory 561): layout A sits around
/// x -416..-182 / z 185..425, layout B around x 181..432 / z -418..-183. Slot 6 (col 1, row 1) of
/// layout B is centred at (249.2, -345.7) with half-extents 23.3 × 21.5.
/// </summary>
public sealed class FloorRoomsTests
{
    private static readonly RoomBox ASlot6 = new(6, -370f, 230f, 22f, 22f);
    private static readonly RoomBox ASlot7 = new(7, -300f, 230f, 22f, 22f);
    private static readonly RoomBox BSlot6 = new(6, 249.2f, -345.7f, 23.3f, 21.5f);

    private static readonly IReadOnlyList<IReadOnlyList<RoomBox>> Layouts =
        [new[] { ASlot6, ASlot7 }, new[] { BSlot6 }];

    private static ushort[] RoomsWith(params int[] slots)
    {
        var rooms = new ushort[25];
        foreach (var slot in slots)
            rooms[slot] = 0x80; // revealed, no other flags — any non-zero means "built"
        return rooms;
    }

    [Fact]
    public void TheLayoutIsWhereThePlayerStands()
    {
        Assert.Equal(1, FloorRooms.LayoutAt(Layouts, 249f, -345f));
        Assert.Equal(0, FloorRooms.LayoutAt(Layouts, -370f, 230f));
        Assert.Equal(-1, FloorRooms.LayoutAt(Layouts, 0f, 0f));
    }

    [Fact]
    public void ASpotInABuiltRoom_IsShown()
    {
        Assert.True(FloorRooms.OnThisFloor(Layouts, 0, RoomsWith(6), -370f, 230f));
    }

    [Fact]
    public void ASpotInARoomThisFloorDidNotBuild_IsHidden()
    {
        Assert.False(FloorRooms.OnThisFloor(Layouts, 0, RoomsWith(6), -300f, 230f));
    }

    [Fact]
    public void ASpotInTheOtherLayout_IsHidden()
    {
        Assert.False(FloorRooms.OnThisFloor(Layouts, 0, RoomsWith(6, 7), 249f, -345f));
    }

    [Fact]
    public void ASpotInNoKnownBox_IsShown_NeverRuledOut()
    {
        // Geometry we do not have is not evidence that nothing can be there.
        Assert.True(FloorRooms.OnThisFloor(Layouts, 0, RoomsWith(6), 0f, 0f));
    }

    [Fact]
    public void BeforeTheLayoutIsKnown_EverythingShows()
    {
        Assert.True(FloorRooms.OnThisFloor(Layouts, -1, RoomsWith(), 249f, -345f));
        Assert.True(FloorRooms.OnThisFloor([], 0, RoomsWith(), 249f, -345f));
    }

    [Fact]
    public void OverlappingBoxes_ShowIfAnyOfThemIsBuilt()
    {
        // A hallway box of slot 7 overlapping room 6: a spot there is reachable if either room exists.
        var hallway = new RoomBox(7, -350f, 230f, 10f, 5f);
        IReadOnlyList<IReadOnlyList<RoomBox>> layouts = [new[] { ASlot6, hallway }];

        Assert.True(FloorRooms.OnThisFloor(layouts, 0, RoomsWith(6), -350f, 230f));
        Assert.True(FloorRooms.OnThisFloor(layouts, 0, RoomsWith(7), -350f, 230f));
        Assert.False(FloorRooms.OnThisFloor(layouts, 0, RoomsWith(12), -350f, 230f));
    }
}
