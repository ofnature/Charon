using System.IO;
using System.Linq;
using Charon.Features.DeepDungeon;

namespace Charon.Tests.Features.DeepDungeon;

/// <summary>
/// The fixture is a real SQLite file built with Python's sqlite3 module in Palace Pal's exact
/// schema: 1 KB pages (so the table needs interior pages), the Source/SinceVersion columns added by
/// ALTER TABLE AFTER the first row (so that row carries fewer values), a 3,000-character text value
/// (an overflow chain), and a second table whose CREATE statement overflows its sqlite_master cell.
/// 602 rows: 561 → 157 traps + 69 hoards, 770 → 134 + 72, 1099 → 109 + 60, 1105 → 1 trap.
/// </summary>
public sealed class SqliteTableReaderTests
{
    private static byte[] Fixture() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "palace-pal-sample.sqlite3"));

    [Fact]
    public void ReadsEveryRow_AcrossInteriorPages()
    {
        var table = SqliteTableReader.Read(Fixture(), "Locations");

        Assert.Equal(602, table.Rows.Count);
        Assert.Equal(
            new[] { "LocalId", "TerritoryType", "Type", "X", "Y", "Z", "Seen", "Source", "SinceVersion" },
            table.Columns);
    }

    [Fact]
    public void CountsMatchWhatSqliteItselfReported()
    {
        var table = SqliteTableReader.Read(Fixture(), "Locations");
        int terr = table.IndexOf("TerritoryType"), type = table.IndexOf("Type");

        var counts = table.Rows
            .GroupBy(r => ((long)r[terr]!, (long)r[type]!))
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(157, counts[(561, 1)]);
        Assert.Equal(69, counts[(561, 2)]);
        Assert.Equal(72, counts[(770, 2)]);
        Assert.Equal(1, counts[(1105, 1)]);
    }

    [Fact]
    public void TheIntegerPrimaryKeyIsTheRowid()
    {
        var table = SqliteTableReader.Read(Fixture(), "Locations");
        var ids = table.Rows.Select(r => (long)r[0]!).ToList();

        Assert.Equal(Enumerable.Range(1, 602).Select(i => (long)i), ids);
    }

    [Fact]
    public void ARowFromBeforeTheAlterTable_HasNullForTheLaterColumns()
    {
        var first = SqliteTableReader.Read(Fixture(), "Locations").Rows[0];

        Assert.Equal(561L, first[1]);
        Assert.Equal(2L, first[2]);
        Assert.Equal(303.1365966796875, (double)first[3]!, 9);
        Assert.Null(first[7]);
        Assert.Null(first[8]);
    }

    [Fact]
    public void AnOverflowingValueIsReadWhole()
    {
        var last = SqliteTableReader.Read(Fixture(), "Locations").Rows[^1];

        Assert.Equal(1105L, last[1]);
        Assert.Equal(new string('v', 3000), last[8]);
        Assert.Equal(1.5, (double)last[3]!);
    }

    [Fact]
    public void AnUnknownTableIsRefused_NotGuessed()
    {
        Assert.Throws<InvalidDataException>(() => SqliteTableReader.Read(Fixture(), "Nope"));
    }

    [Fact]
    public void SomethingThatIsNotSqliteIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => SqliteTableReader.Read(new byte[4096], "Locations"));
    }

    [Fact]
    public void ColumnsAddedLaterAreParsedInOrder()
    {
        // Palace Pal's real CREATE statement, as its migrations left it.
        const string sql = "CREATE TABLE \"Locations\" (\n    \"LocalId\" INTEGER NOT NULL CONSTRAINT \"PK_Locations\" PRIMARY KEY AUTOINCREMENT,\n"
                           + "    \"TerritoryType\" INTEGER NOT NULL,\n    \"Seen\" INTEGER NOT NULL\n"
                           + ", \"Source\" INTEGER NOT NULL DEFAULT 0, \"SinceVersion\" TEXT NOT NULL DEFAULT '')";

        var (columns, alias) = SqliteTableReader.ParseColumns(sql);

        Assert.Equal(new[] { "LocalId", "TerritoryType", "Seen", "Source", "SinceVersion" }, columns);
        Assert.Equal(0, alias);
    }
}
