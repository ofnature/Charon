using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Variant;

/// <summary>Which routes of one dungeon this character has recorded.</summary>
public sealed record VariantProgress(VariantDungeon Dungeon, IReadOnlyList<int> Done, IReadOnlyList<int> Left)
{
    public bool Complete => Left.Count == 0;
}

/// <summary>
/// The game's own record of finished variant routes, and what to run next. Pure — no Dalamud types.
///
/// The record is <c>PlayerState.CompletedVVDNotebookContents</c>: a bit array whose size ClientStructs
/// documents as "VVDNotebookContentsSheet.RowCount - 1", i.e. one bit per notebook row starting at row 1 —
/// so row n is bit n - 1, read least-significant bit first like every other ClientStructs bit array.
/// </summary>
public static class VariantRecord
{
    public static bool IsDone(ReadOnlySpan<byte> bits, uint notebookRow)
    {
        if (notebookRow == 0)
            return false;

        var bit = (int)notebookRow - 1;
        var index = bit >> 3;
        return index < bits.Length && (bits[index] & (1 << (bit & 7))) != 0;
    }

    public static VariantProgress Progress(VariantDungeon dungeon, ReadOnlySpan<byte> bits)
    {
        var done = new List<int>();
        var left = new List<int>();
        foreach (var route in dungeon.Routes)
            (IsDone(bits, dungeon.NotebookRow(route.Number)) ? done : left).Add(route.Number);
        return new VariantProgress(dungeon, done, left);
    }

    /// <summary>
    /// The route to run next: an unfinished SECRET route first, then the lowest-numbered one left.
    ///
    /// Secret first because each secret route is a sibling route plus extra steps (Sil'dihn 12 is route 7
    /// with emotes instead of the interact, Rokkon 12 is route 7 with the lanterns lit, Aloalo 12 is the
    /// deny-three-times path with the rites, the Merchant's Tale 13 starts like 5 and 6): a failed attempt
    /// simply carries on into a sibling and still earns a record. Aloalo's community guide gives exactly
    /// this advice for its route 12. Null when every route is done.
    /// </summary>
    public static int? Suggest(VariantProgress progress)
    {
        if (progress.Left.Count == 0)
            return null;

        var secret = progress.Dungeon.Routes.FirstOrDefault(r => r.Secret && progress.Left.Contains(r.Number));
        return secret?.Number ?? progress.Left.Min();
    }
}
