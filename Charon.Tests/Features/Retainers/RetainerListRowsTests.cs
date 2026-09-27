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
        null,               // [0] row count 2
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
        Assert.Equal(0, RetainerListRows.RowOf(RealList, 2, "T'sala"));
    }

    [Fact]
    public void TheSecondRetainerIsRowOne_NotItsRawArrayPosition()
    {
        // The bug: the raw sorted position of T'sola pointed PAST her row, onto an unpaid slot the
        // game refused ("You cannot summon that retainer"). The list itself says row 1.
        Assert.Equal(1, RetainerListRows.RowOf(RealList, 2, "T'sola"));
    }

    [Fact]
    public void NamesAreMatchedCaseInsensitively()
    {
        Assert.Equal(1, RetainerListRows.RowOf(RealList, 2, "t'SOLA"));
    }

    [Fact]
    public void AnUnknownNameIsNotARow()
    {
        Assert.Equal(-1, RetainerListRows.RowOf(RealList, 2, "Nobody"));
    }

    [Fact]
    public void OtherTextInARowIsNeverMistakenForAName()
    {
        // "Selling 3 items" sits in row 0's block; only the name slot may match.
        Assert.Equal(-1, RetainerListRows.RowOf(RealList, 2, "Selling 3 items"));
    }

    [Fact]
    public void ACountTheArrayCannotHold_RefusesRatherThanGuesses()
    {
        Assert.Equal(-1, RetainerListRows.RowOf(RealList, 5, "Ghost"));
        Assert.Equal(-1, RetainerListRows.RowOf(RealList, 0, "T'sala"));
        Assert.Equal(-1, RetainerListRows.RowOf([], 2, "T'sala"));
    }

    [Fact]
    public void NamesAreReadInRowOrder()
    {
        Assert.Equal(["T'sala", "T'sola"], RetainerListRows.Names(RealList, 2));
    }
}
