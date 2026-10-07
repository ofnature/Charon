using System;
using System.Collections.Generic;

namespace Charon.Features.DeepDungeon;

/// <summary>
/// One collision box of a deep-dungeon room slot: a room, or a hallway that belongs to it. Axis
/// aligned (every box in the game files has zero rotation), extents are HALF sizes.
/// </summary>
public readonly record struct RoomBox(int Slot, float CenterX, float CenterZ, float HalfX, float HalfZ)
{
    public bool Contains(float x, float z) =>
        MathF.Abs(x - CenterX) <= HalfX && MathF.Abs(z - CenterZ) <= HalfZ;
}

/// <summary>
/// Narrows likely spots to the rooms that exist on THIS floor. Pure — no Dalamud types.
///
/// The approach is BossMod's (BSD-3-Clause, awgil/ffxiv_bossmod, <c>DeepDungeon/Rooms.cs</c> +
/// <c>Traps.cs</c> — adapted, with attribution): every floor set's <c>planmap.lgb</c> holds its room
/// slots as EventRange boxes whose priority is 100 + the 5×5 slot index. VERIFIED against the game
/// files for Palace of the Dead (561, 593), Heaven-on-High (770), Eureka Orthos (1099) and Pilgrim's
/// Traverse (1281, 1290): two layouts per floor set (Heaven-on-High has a third, smaller one), each
/// main layout with 21 distinct priorities 101-123 (the four grid corners never hold a room), columns
/// running east with X and rows south with Z, every box unrotated. Checked against all 19,155 imported
/// Palace Pal spots: every one lands in a box, none in two layouts. The game picks one layout per floor and builds a subset of its rooms
/// (<c>InstanceContentDeepDungeon.MapData</c>).
///
/// Charon's addition: the layouts sit in SEPARATE parts of the zone, so the active one is read
/// from where the player is standing rather than from BossMod's tileset-swap heuristic (their own
/// comment calls it a stand-in). A spot is then hidden when it lies in the inactive layout, or in a
/// room slot the floor did not build — and SHOWN when it lies in no box at all, because geometry we do
/// not have is never evidence that a trap cannot be there.
/// </summary>
public static class FloorRooms
{
    /// <summary>
    /// A layer with fewer distinct slots than this is some other set of ranges, not a layout. NOT 21:
    /// Heaven-on-High has a THIRD, 12-room layout in its own corner of the zone (verified — with it
    /// counted every HoH spot lands in a box, without it a fifth of them landed in none), while
    /// territory 782 carries a stray layer of 20 boxes that all share ONE slot and hold no spot.
    /// </summary>
    public const int MinSlotsForLayout = 6;

    /// <summary>Which layout the point stands in, or -1.</summary>
    public static int LayoutAt(IReadOnlyList<IReadOnlyList<RoomBox>> layouts, float x, float z)
    {
        for (var l = 0; l < layouts.Count; l++)
        {
            foreach (var box in layouts[l])
            {
                if (box.Contains(x, z))
                    return l;
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether a spot can be live on this floor. <paramref name="activeLayout"/> -1 means not known
    /// yet, which shows everything — the filter only ever removes what it can rule out.
    /// </summary>
    public static bool OnThisFloor(
        IReadOnlyList<IReadOnlyList<RoomBox>> layouts, int activeLayout, IReadOnlyList<ushort> rooms, float x, float z)
    {
        if (activeLayout < 0 || activeLayout >= layouts.Count)
            return true;

        var inActive = false;
        foreach (var box in layouts[activeLayout])
        {
            if (!box.Contains(x, z))
                continue;

            inActive = true;
            if (box.Slot >= 0 && box.Slot < rooms.Count && rooms[box.Slot] != 0)
                return true; // a box of a room this floor built
        }

        if (inActive)
            return false; // only in boxes of rooms the floor did not build

        for (var l = 0; l < layouts.Count; l++)
        {
            if (l == activeLayout)
                continue;
            foreach (var box in layouts[l])
            {
                if (box.Contains(x, z))
                    return false; // the other layout's part of the zone
            }
        }

        return true; // in no box we know of
    }
}
