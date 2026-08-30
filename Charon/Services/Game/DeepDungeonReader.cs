using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace Charon.Services.Game;

/// <summary>
/// Reads the live deep-dungeon state — floor, the 25-room map, chests, party rooms. Thin unsafe
/// adapter over <c>EventFramework.GetInstanceContentDeepDungeon()</c>; that pointer being
/// non-null IS the "in a deep dungeon" signal, so no territory list can ever go stale. The same
/// struct BossMod's DeepDungeonState mirrors in production (double-validated offsets).
/// </summary>
public sealed unsafe class DeepDungeonReader
{
    private static readonly TimeSpan CacheLife = TimeSpan.FromMilliseconds(500);

    private readonly IPluginLog _log;

    public sealed record ChestMark(byte Type, sbyte Room);
    public sealed record PartyMark(uint EntityId, sbyte Room);

    public sealed record Snapshot(
        bool Active, int Floor, int PassageProgress, int ReturnProgress,
        ushort[] Rooms, ChestMark[] Chests, PartyMark[] Party);

    private static readonly Snapshot Inactive =
        new(false, 0, 0, 0, [], [], []);

    private Snapshot _cache = Inactive;
    private DateTime _cacheUtc = DateTime.MinValue;

    public DeepDungeonReader(IPluginLog log) => _log = log;

    public string Status { get; private set; } = "not in a deep dungeon";

    public Snapshot GetSnapshot()
    {
        if (DateTime.UtcNow - _cacheUtc < CacheLife)
            return _cache;

        _cacheUtc = DateTime.UtcNow;
        try
        {
            var framework = EventFramework.Instance();
            var content = framework == null ? null : framework->GetInstanceContentDeepDungeon();
            if (content == null)
            {
                _cache = Inactive;
                Status = "not in a deep dungeon";
                return _cache;
            }

            var rooms = new ushort[Charon.Features.DeepDungeon.FloorMap.RoomCount];
            for (var i = 0; i < rooms.Length; i++)
                rooms[i] = (ushort)content->MapData[i];

            var chests = new ChestMark[content->Chests.Length];
            for (var i = 0; i < chests.Length; i++)
                chests[i] = new ChestMark(content->Chests[i].ChestType, content->Chests[i].RoomIndex);

            var party = new PartyMark[content->Party.Length];
            for (var i = 0; i < party.Length; i++)
                party[i] = new PartyMark(content->Party[i].EntityId, content->Party[i].RoomIndex);

            _cache = new Snapshot(true, content->Floor,
                content->PassageProgress, content->ReturnProgress, rooms, chests, party);

            var cells = Charon.Features.DeepDungeon.FloorMap.Build(rooms);
            var (known, revealed) = Charon.Features.DeepDungeon.FloorMap.Counts(cells);
            Status = $"floor {_cache.Floor} — {known} rooms known, {revealed} revealed";
            return _cache;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Deep dungeon read threw");
            _cache = Inactive;
            Status = "read threw (see log)";
            return _cache;
        }
    }
}
