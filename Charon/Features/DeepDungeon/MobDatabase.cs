using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charon.Features.DeepDungeon;

/// <summary>How a deep-dungeon mob notices you — the fact the aggro circles are drawn from.</summary>
public enum MobAggro
{
    Sight = 0,
    Sound = 1,
    Proximity = 2,
}

/// <summary>One mob's curated info, keyed by its NameId.</summary>
public sealed record MobRecord(uint Id, MobAggro Aggro, int DangerLevel, bool Patrol, bool BossOrAdd, bool Special);

/// <summary>
/// NecroLens's curated deep-dungeon mob dataset (MIT, Jukkales/NecroLens — allMobs.json, ~700
/// mobs), embedded verbatim. This is the knowledge that cannot be derived from the game sheets:
/// which mobs aggro on sight vs sound vs proximity, and which patrol. Loaded lazily from the
/// embedded resource; a missing or broken resource degrades to an empty table (mobs then draw
/// with the proximity default, never nothing).
/// </summary>
public sealed class MobDatabase
{
    private Dictionary<uint, MobRecord>? _byNameId;

    private sealed class Row
    {
        [JsonPropertyName("Id")] public uint Id { get; set; }
        [JsonPropertyName("AggroType")] public int AggroType { get; set; }
        [JsonPropertyName("DangerLevel")] public int DangerLevel { get; set; }
        [JsonPropertyName("Patrol")] public bool Patrol { get; set; }
        [JsonPropertyName("BossOrAdd")] public bool BossOrAdd { get; set; }
        [JsonPropertyName("Special")] public bool Special { get; set; }
    }

    public int Count => Table.Count;

    public MobRecord? Find(uint nameId) => Table.TryGetValue(nameId, out var record) ? record : null;

    private Dictionary<uint, MobRecord> Table
    {
        get
        {
            if (_byNameId != null)
                return _byNameId;

            _byNameId = new Dictionary<uint, MobRecord>();
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("Charon.Data.allMobs.json");
                if (stream == null)
                    return _byNameId;

                using var reader = new StreamReader(stream);
                var rows = JsonSerializer.Deserialize<List<Row>>(reader.ReadToEnd());
                if (rows == null)
                    return _byNameId;

                foreach (var row in rows)
                {
                    _byNameId[row.Id] = new MobRecord(
                        row.Id, (MobAggro)Math.Clamp(row.AggroType, 0, 2),
                        row.DangerLevel, row.Patrol, row.BossOrAdd, row.Special);
                }
            }
            catch
            {
                // fail-open: an empty table means proximity-default circles, not a crash
            }

            return _byNameId;
        }
    }

    /// <summary>Test seam: parse rows from raw JSON (the same shape as the embedded file).</summary>
    internal static List<MobRecord> ParseForTest(string json)
    {
        var rows = JsonSerializer.Deserialize<List<Row>>(json) ?? [];
        return rows.ConvertAll(r => new MobRecord(
            r.Id, (MobAggro)Math.Clamp(r.AggroType, 0, 2), r.DangerLevel, r.Patrol, r.BossOrAdd, r.Special));
    }
}
