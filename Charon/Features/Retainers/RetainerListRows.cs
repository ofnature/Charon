using System;
using System.Collections.Generic;

namespace Charon.Features.Retainers;

/// <summary>
/// Finds a retainer's ROW in the bell's <c>RetainerList</c> by reading the list's own values.
/// Pure — no Dalamud types.
///
/// WHY NOT <c>GetRetainerBySortedIndex</c>: that walks the game's full retainer array, skipping
/// empty entries but still answering with the RAW position. The list shows only live retainers,
/// packed. So on an account with a gap in that array — an unpaid or empty slot between two paid
/// retainers — the raw position of the second one points one row too far, and the game resolved it
/// against a slot the player is not paying for: "You cannot summon that retainer unless you resume
/// payment for their services", four times in chat. The first retainer only worked because nothing
/// sat in front of it. AutoRetainer avoids this by selecting BY NAME from the list itself, and so
/// does this.
///
/// LAYOUT, read off a real <c>RetainerList</c> (2026-09-27, two retainers), not inferred:
///   [0]  2 on a two-retainer account — NOT trusted as the row count (see RowOf)
///   [1]  venture tokens                   (511 — the menu showed "Ventures: 511")
///   [2]  unknown                          (2)
///   then 10 values per row, starting at [3]:
///   +0 name · +1 class icon · +2 level · +3 inventory slots filled · +4 gil · +5 market icon ·
///   +6 "Selling N items" · +7 venture status text · +8 bool · +9 bool
/// Every number in that dump matched what the retainer menus showed (172/174 slots, the exact gil),
/// which is what makes the offsets trustworthy rather than guessed.
/// </summary>
public static class RetainerListRows
{
    /// <summary>Index of the first row's first value.</summary>
    public const int Header = 3;

    /// <summary>Values per row.</summary>
    public const int Stride = 10;

    /// <summary>Where a row's name sits within its block.</summary>
    public const int NameOffset = 0;

    /// <summary>
    /// The row whose name matches, or -1. <paramref name="texts"/> holds each value's text, or null for a
    /// non-text value, in array order.
    ///
    /// Rows are read until a row's name slot holds no text — NOT up to value [0]. Value [0] was taken as
    /// the row count off a two-retainer account, where [0] and [2] both read 2; the roommate's
    /// eight-retainer account then collected nothing past the second row (user, 2026-11-01: "cannot
    /// collect any outside the default 2 slots"), which is what a 2 that means something else (the two
    /// free retainer slots?) bounding the search would do. The packed names themselves are the evidence of
    /// how many rows there are; only an exact name in a name slot ever matches, so reading on is safe.
    /// </summary>
    public static int RowOf(IReadOnlyList<string?> texts, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return -1;

        var row = 0;
        foreach (var candidate in NameSlots(texts))
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return row;
            row++;
        }

        return -1;
    }

    /// <summary>Every name the list shows, in row order — for the trace and for tests.</summary>
    public static List<string> Names(IReadOnlyList<string?> texts) => new(NameSlots(texts));

    /// <summary>The name slot of each row, in order, until one holds no text (the end of the packed rows).</summary>
    private static IEnumerable<string> NameSlots(IReadOnlyList<string?> texts)
    {
        for (var index = Header + NameOffset; index < texts.Count; index += Stride)
        {
            if (texts[index] is not { Length: > 0 } name)
                yield break;
            yield return name;
        }
    }
}
