using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class RetainerListRowsTests
{
    /// <summary>
    /// A real RetainerList value array, captured at a bell on 2026-09-27 (non-text values as null).
    /// The fixture IS the evidence: every offset in <see cref="RetainerListRows"/> is read from it.
    /// </summary>
    private static readonly string?[] RealList =
    [
        null,               // [0] 2 (NOT the row count — see RowOf)
        null,               // [1] venture tokens 511
        null,               // [2] 2
        "T'sala",           // [3]  row 0 name
        null, null, null, null, null,
        "Selling 3 items",  // [9]
        "Complete in 8h 13m", // [10]
        null, null,         // [11] [12]
        "T'sola",           // [13] row 1 name
        null, null, null, null, null,
        "Selling 8 items",  // [19]
        "Complete in 8h 15m", // [20]
        null, null,         // [21] [22]
    ];

    [Fact]
    public void TheFirstRetainerIsRowZero()
    {
        Assert.Equal(0, RetainerListRows.RowOf(RealList, "T'sala"));
    }

    [Fact]
    public void TheSecondRetainerIsRowOne_NotItsRawArrayPosition()
    {
        // The bug: the raw sorted position of T'sola pointed PAST her row, onto an unpaid slot the
        // game refused ("You cannot summon that retainer"). The list itself says row 1.
        Assert.Equal(1, RetainerListRows.RowOf(RealList, "T'sola"));
    }

    [Fact]
    public void NamesAreMatchedCaseInsensitively()
    {
        Assert.Equal(1, RetainerListRows.RowOf(RealList, "t'SOLA"));
    }

    [Fact]
    public void AnUnknownNameIsNotARow()
    {
        Assert.Equal(-1, RetainerListRows.RowOf(RealList, "Nobody"));
    }

    [Fact]
    public void OtherTextInARowIsNeverMistakenForAName()
    {
        // "Selling 3 items" sits in row 0's block; only the name slot may match.
        Assert.Equal(-1, RetainerListRows.RowOf(RealList, "Selling 3 items"));
    }

    [Fact]
    public void AnEmptyOrShortList_HasNoRows()
    {
        Assert.Equal(-1, RetainerListRows.RowOf([], "T'sala"));
        Assert.Equal(-1, RetainerListRows.RowOf([null, null, null], "T'sala"));
        Assert.Equal(-1, RetainerListRows.RowOf(RealList, " "));
    }

    /// <summary>An eight-retainer list shaped like the real one, value [0] still reading 2.</summary>
    private static string?[] EightRetainers()
    {
        string[] names = ["Kongoola", "Hotstufff", "Dreadheart", "Kyrakris", "Lunarvoilet", "Dracongirl", "Kiittyy", "Dragonnlady"];
        var list = new string?[3 + names.Length * 10];
        for (var row = 0; row < names.Length; row++)
        {
            list[3 + row * 10] = names[row];
            list[3 + row * 10 + 6] = "Selling 0 items";
            list[3 + row * 10 + 7] = "Complete";
        }

        return list;
    }

    [Fact]
    public void RetainersPastTheSecondRow_AreFound()
    {
        // The roommate's account (2026-11-01): nothing past the second slot could be collected. A search
        // bounded by value [0] — 2 on the account the layout was read from — stops right there.
        var list = EightRetainers();
        Assert.Equal(2, RetainerListRows.RowOf(list, "Dreadheart"));
        Assert.Equal(7, RetainerListRows.RowOf(list, "Dragonnlady"));
        Assert.Equal(8, RetainerListRows.Names(list).Count);
    }

    [Fact]
    public void ReadingStopsAtTheFirstRowWithoutAName()
    {
        var list = EightRetainers();
        list[3 + 3 * 10] = null; // row 3 has no name: the packed rows end there
        Assert.Equal(3, RetainerListRows.Names(list).Count);
        Assert.Equal(-1, RetainerListRows.RowOf(list, "Lunarvoilet"));
    }

    [Fact]
    public void NamesAreReadInRowOrder()
    {
        Assert.Equal(["T'sala", "T'sola"], RetainerListRows.Names(RealList));
    }
}
