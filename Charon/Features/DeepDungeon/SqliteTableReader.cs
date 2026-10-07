using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Charon.Features.DeepDungeon;

/// <summary>
/// Reads every row of ONE table out of a SQLite 3 database file, read-only. Pure — no Dalamud
/// types, no native library.
///
/// Built to import Palace Pal's saved trap/hoard spots (its <c>palace-pal.data.sqlite3</c>)
/// without taking a native SQLite dependency into the plugin. It implements only what reading a
/// rowid table needs, straight from the published file format (sqlite.org/fileformat2.html):
/// the 100-byte header, table b-tree interior (0x05) and leaf (0x0D) pages, overflow chains, and
/// the record format. Anything else — a WITHOUT ROWID table, a non-UTF-8 database, a malformed
/// page — throws <see cref="InvalidDataException"/> rather than guessing.
///
/// It reads the MAIN file only: changes still sitting in a <c>-wal</c> file are invisible here,
/// which is why the caller checks for one and says so.
/// </summary>
public static class SqliteTableReader
{
    private const int MaxPages = 1_000_000;

    public sealed record Table(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows)
    {
        public int IndexOf(string column)
        {
            for (var i = 0; i < Columns.Count; i++)
            {
                if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }
    }

    public static Table Read(byte[] db, string tableName)
    {
        if (db.Length < 100 || Encoding.ASCII.GetString(db, 0, 15) != "SQLite format 3")
            throw new InvalidDataException("not a SQLite 3 database");

        var pageSize = (db[16] << 8) | db[17];
        if (pageSize == 1)
            pageSize = 65536;
        if (pageSize < 512 || db.Length % pageSize != 0)
            throw new InvalidDataException($"unexpected page size {pageSize}");

        var encoding = BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(56));
        if (encoding is not (0 or 1))
            throw new InvalidDataException($"database text encoding {encoding} is not UTF-8");

        var file = new FileView(db, pageSize, pageSize - db[20]);

        // sqlite_master lives on page 1: type, name, tbl_name, rootpage, sql.
        foreach (var (_, row) in file.WalkTable(1))
        {
            if (row.Length < 5 || row[0] as string != "table"
                || !string.Equals(row[1] as string, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            var root = Convert.ToInt32(row[3]);
            var (columns, rowidAlias) = ParseColumns(row[4] as string ?? string.Empty);

            var rows = new List<object?[]>();
            foreach (var (rowid, values) in file.WalkTable(root))
            {
                // A row written before an ALTER TABLE ADD COLUMN carries fewer values; the missing
                // ones are the column default, which the caller supplies. Pad with null.
                var full = new object?[columns.Count];
                Array.Copy(values, full, Math.Min(values.Length, full.Length));

                // An INTEGER PRIMARY KEY column is the rowid itself and is stored as NULL.
                if (rowidAlias >= 0)
                    full[rowidAlias] = rowid;

                rows.Add(full);
            }

            return new Table(columns, rows);
        }

        throw new InvalidDataException($"no table named '{tableName}'");
    }

    /// <summary>Column names in declaration order, and which one (if any) aliases the rowid.</summary>
    internal static (List<string> Columns, int RowidAlias) ParseColumns(string createSql)
    {
        var open = createSql.IndexOf('(');
        var close = createSql.LastIndexOf(')');
        if (open < 0 || close <= open)
            throw new InvalidDataException("unreadable CREATE TABLE statement");

        var columns = new List<string>();
        var rowidAlias = -1;
        var depth = 0;
        var start = open + 1;

        for (var i = open + 1; i <= close; i++)
        {
            var ch = i == close ? ',' : createSql[i];
            if (ch == '(')
                depth++;
            else if (ch == ')')
                depth--;
            else if (ch == ',' && depth == 0)
            {
                var definition = createSql[start..i].Trim();
                start = i + 1;
                if (definition.Length == 0 || IsTableConstraint(definition))
                    continue;

                var tokens = definition.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                var name = tokens[0].Trim('"', '`', '[', ']');
                if (tokens.Length > 1
                    && tokens[1].Equals("INTEGER", StringComparison.OrdinalIgnoreCase)
                    && definition.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase))
                    rowidAlias = columns.Count;

                columns.Add(name);
            }
        }

        return (columns, rowidAlias);
    }

    private static bool IsTableConstraint(string definition)
    {
        foreach (var keyword in new[] { "CONSTRAINT", "PRIMARY", "FOREIGN", "UNIQUE", "CHECK" })
        {
            if (definition.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
                && (definition.Length == keyword.Length || char.IsWhiteSpace(definition[keyword.Length])))
                return true;
        }

        return false;
    }

    private sealed class FileView(byte[] db, int pageSize, int usable)
    {
        public IEnumerable<(long Rowid, object?[] Values)> WalkTable(int root)
        {
            var visited = new HashSet<int>();
            var pending = new Stack<int>();
            pending.Push(root);

            // Depth-first, children pushed right-to-left so rows come out in rowid order.
            while (pending.Count > 0)
            {
                var page = pending.Pop();
                if (!visited.Add(page) || visited.Count > MaxPages)
                    throw new InvalidDataException($"b-tree revisits page {page}");

                var pageStart = PageOffset(page);
                var header = page == 1 ? pageStart + 100 : pageStart;
                var kind = db[header];
                var cells = U16(header + 3);

                if (kind == 0x0D)
                {
                    for (var i = 0; i < cells; i++)
                    {
                        var pos = pageStart + U16(header + 8 + i * 2);
                        var payloadSize = Varint(ref pos);
                        var rowid = Varint(ref pos);
                        yield return (rowid, ParseRecord(Payload(pos, payloadSize)));
                    }
                }
                else if (kind == 0x05)
                {
                    pending.Push((int)U32(header + 8)); // right-most child: the largest rowids, read last
                    for (var i = cells - 1; i >= 0; i--)
                    {
                        var pos = pageStart + U16(header + 12 + i * 2);
                        pending.Push((int)U32(pos));
                    }
                }
                else
                {
                    throw new InvalidDataException($"page {page} is not a table b-tree page (type 0x{kind:X2})");
                }
            }
        }

        private int PageOffset(int page)
        {
            if (page < 1 || (long)page * pageSize > db.Length)
                throw new InvalidDataException($"page {page} is outside the file");
            return (page - 1) * pageSize;
        }

        /// <summary>The cell's payload, following its overflow chain when it does not fit locally.</summary>
        private byte[] Payload(int pos, long size)
        {
            if (size < 0 || size > db.Length)
                throw new InvalidDataException("payload size out of range");

            var total = (int)size;
            var maxLocal = usable - 35;
            var local = total;
            if (total > maxLocal)
            {
                var minLocal = ((usable - 12) * 32 / 255) - 23;
                var k = minLocal + ((total - minLocal) % (usable - 4));
                local = k <= maxLocal ? k : minLocal;
            }

            var payload = new byte[total];
            Copy(pos, payload, 0, local);

            var written = local;
            var next = written < total ? U32(pos + local) : 0u;
            var hops = 0;
            while (written < total)
            {
                if (next == 0 || ++hops > MaxPages)
                    throw new InvalidDataException("broken overflow chain");

                var overflow = PageOffset((int)next);
                var take = Math.Min(total - written, usable - 4);
                Copy(overflow + 4, payload, written, take);
                written += take;
                next = U32(overflow);
            }

            return payload;
        }

        private void Copy(int from, byte[] into, int at, int count)
        {
            if (from < 0 || from + count > db.Length)
                throw new InvalidDataException("read past the end of the file");
            Buffer.BlockCopy(db, from, into, at, count);
        }

        private int U16(int at) => (db[at] << 8) | db[at + 1];

        private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(db.AsSpan(at, 4));

        private long Varint(ref int pos) => SqliteTableReader.Varint(db, ref pos);
    }

    internal static long Varint(byte[] buffer, ref int pos)
    {
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = buffer[pos++];
            value = (value << 7) | (long)(b & 0x7F);
            if ((b & 0x80) == 0)
                return value;
        }

        return (value << 8) | buffer[pos++]; // the ninth byte contributes all eight bits
    }

    internal static object?[] ParseRecord(byte[] record)
    {
        var pos = 0;
        var headerSize = (int)Varint(record, ref pos);
        var types = new List<long>();
        while (pos < headerSize)
            types.Add(Varint(record, ref pos));

        var values = new object?[types.Count];
        pos = headerSize;
        for (var i = 0; i < types.Count; i++)
        {
            var type = types[i];
            switch (type)
            {
                case 0:
                    values[i] = null;
                    break;
                case >= 1 and <= 6:
                    var width = type switch { 5 => 6, 6 => 8, _ => (int)type };
                    long n = (sbyte)record[pos]; // sign-extend from the first byte
                    for (var b = 1; b < width; b++)
                        n = (n << 8) | record[pos + b];
                    values[i] = n;
                    pos += width;
                    break;
                case 7:
                    values[i] = BinaryPrimitives.ReadDoubleBigEndian(record.AsSpan(pos, 8));
                    pos += 8;
                    break;
                case 8:
                    values[i] = 0L;
                    break;
                case 9:
                    values[i] = 1L;
                    break;
                case >= 12 when type % 2 == 0:
                    var blobLength = (int)((type - 12) / 2);
                    values[i] = record.AsSpan(pos, blobLength).ToArray();
                    pos += blobLength;
                    break;
                case >= 13:
                    var textLength = (int)((type - 13) / 2);
                    values[i] = Encoding.UTF8.GetString(record, pos, textLength);
                    pos += textLength;
                    break;
                default:
                    throw new InvalidDataException($"reserved serial type {type}");
            }
        }

        return values;
    }
}
