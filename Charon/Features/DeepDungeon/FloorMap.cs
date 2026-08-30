using System.Collections.Generic;

namespace Charon.Features.DeepDungeon;

/// <summary>
/// The deep-dungeon floor layout as a drawing model. Pure logic — no Dalamud or ClientStructs
/// types; the adapter hands over the 25 raw room flags as ushorts.
///
/// The game keeps each floor as a 5×5 room grid (`InstanceContentDeepDungeon.MapData`, 25
/// entries — the same data BossMod's DeepDungeonState mirrors in production, which is what
/// validates the layout). Each cell carries connection bits, Passage/Return markers, Home (the
/// room you are in) and Revealed. The connection bits exist for UNREVEALED rooms too — that is
/// the whole point of drawing this: the full layout is knowable before it is explored.
/// </summary>
public static class FloorMap
{
    public const int GridSize = 5;
    public const int RoomCount = GridSize * GridSize;

    // Mirror of InstanceContentDeepDungeon.RoomFlags — kept as our own constants so the pure
    // layer stays free of game-library types.
    public const ushort ConnectionN = 1;
    public const ushort ConnectionS = 1 << 1;
    public const ushort ConnectionW = 1 << 2;
    public const ushort ConnectionE = 1 << 3;
    public const ushort Return = 1 << 4;
    public const ushort Passage = 1 << 5;
    public const ushort Home = 1 << 6;
    public const ushort Revealed = 1 << 7;

    /// <summary>One room cell, ready to draw. X grows east, Y grows south (index = Y*5+X).</summary>
    public sealed record Cell(
        int Index, int X, int Y, bool Exists,
        bool North, bool South, bool West, bool East,
        bool IsReturn, bool IsPassage, bool IsHome, bool IsRevealed);

    public static List<Cell> Build(IReadOnlyList<ushort> rooms)
    {
        var cells = new List<Cell>(RoomCount);
        for (var i = 0; i < RoomCount && i < rooms.Count; i++)
        {
            var flags = rooms[i];
            cells.Add(new Cell(
                i, i % GridSize, i / GridSize,
                Exists: flags != 0,
                North: (flags & ConnectionN) != 0,
                South: (flags & ConnectionS) != 0,
                West: (flags & ConnectionW) != 0,
                East: (flags & ConnectionE) != 0,
                IsReturn: (flags & Return) != 0,
                IsPassage: (flags & Passage) != 0,
                IsHome: (flags & Home) != 0,
                IsRevealed: (flags & Revealed) != 0));
        }

        return cells;
    }

    /// <summary>How many rooms exist on the floor / how many the game has revealed.</summary>
    public static (int Known, int Revealed) Counts(IReadOnlyList<Cell> cells)
    {
        int known = 0, revealed = 0;
        foreach (var cell in cells)
        {
            if (!cell.Exists)
                continue;
            known++;
            if (cell.IsRevealed)
                revealed++;
        }

        return (known, revealed);
    }
}
