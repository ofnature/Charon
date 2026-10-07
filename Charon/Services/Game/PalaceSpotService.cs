using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Charon.Features.DeepDungeon;
using Charon.Ipc;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Likely trap and hoard spots in deep dungeons — Palace Pal's idea, Charon's own code. Palace Pal
/// is AGPL-3.0 (the FCCH/AntiAfkKick rule: no code taken) and decommissioned; its community server
/// is gone, but every client that ran it still holds the spots it downloaded in
/// <c>pluginConfigs/PalacePal/palace-pal.data.sqlite3</c>. This service:
///
///   1. IMPORTS that file once per machine (read-only, through <see cref="SqliteTableReader"/>),
///   2. RECORDS every trap and hoard this client sees from then on (traps after Sight, hoards after
///      Intuition — the same object ids the ESP draws),
///   3. reads the floor's pomander news from the log (<see cref="FloorNewsTexts"/>) so the guesses
///      disappear once the game has told us the truth,
///   4. hands the ESP the spots near the player.
///
/// The store is a file of its own (<c>palace-spots.json</c>), not Charon.json: ~38k spots would
/// bloat a config that is saved constantly. Charon's config directory is SHARED by every client on
/// the machine, so a save merges with what is on disk first — two clients in deep dungeons at once
/// must not erase each other's sightings.
///
/// The fleet SHARES spots over the LAN relay (<see cref="Charon.Ipc.SpotRelay"/>, channel
/// <c>charon.spots</c>) — the replacement for Palace Pal's dead server: a new sighting is broadcast at
/// once, and a box entering a floor set asks the fleet for what it is missing, so a PC with no Palace
/// Pal history still gets the other PC's. Spots are narrowed to the rooms THIS floor built
/// (<see cref="FloorRooms"/>, BossMod's approach).
///
/// Not ported: Palace Pal's "trap exploded" detection, which hooks a game function found by byte
/// pattern (the patch-day breakage this repo avoids).
/// </summary>
public sealed class PalaceSpotService : IDisposable
{
    private const string StoreFileName = "palace-spots.json";
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(60);

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IChatGui _chat;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly DeepDungeonReader _reader;
    private readonly CharonConfig _config;
    private readonly Action _saveConfig;
    private readonly RelayClient _relay;
    private readonly Func<string> _localName;
    private readonly DeepDungeonGeometry _geometry;
    private readonly Random _random = new();
    private readonly List<Spot> _newlySeen = new();

    /// <summary>Which of the floor set's two layouts this floor uses; -1 until the player is in a known box.</summary>
    private int _activeLayout = -1;

    private PendingAnswer? _pendingAnswer;
    private int _lastHidden;
    private int _sharedIn;

    /// <summary>An answer waiting out its jitter; cancelled if a box that knows as much answers first.</summary>
    private sealed class PendingAnswer(string id, uint territory, DateTime dueUtc)
    {
        public string Id { get; } = id;
        public uint Territory { get; } = territory;
        public DateTime DueUtc { get; } = dueUtc;
        public bool Cancelled { get; set; }
    }

    private readonly FloorIntel _intel = new();
    private SpotStore? _store;
    private FloorNewsTexts? _texts;
    private bool _dirty;
    private DateTime _nextScanUtc;
    private DateTime _lastSaveUtc = DateTime.UtcNow;
    private int _lastFloor = -1;
    private uint _lastTerritory;
    private int _seenThisSession;

    public PalaceSpotService(
        IDalamudPluginInterface pluginInterface, IObjectTable objectTable, IClientState clientState,
        IChatGui chat, IDataManager data, IPluginLog log, DeepDungeonReader reader,
        CharonConfig config, Action saveConfig, RelayClient relay, Func<string> localName)
    {
        _pluginInterface = pluginInterface;
        _objectTable = objectTable;
        _clientState = clientState;
        _chat = chat;
        _data = data;
        _log = log;
        _reader = reader;
        _config = config;
        _saveConfig = saveConfig;
        _relay = relay;
        _localName = localName;
        _geometry = new DeepDungeonGeometry(data, log);

        _chat.ChatMessage += OnChatMessage;
    }

    public string Status { get; private set; } = "not in a deep dungeon";

    /// <summary>The last import's outcome, for the settings page.</summary>
    public string ImportStatus { get; private set; } = string.Empty;

    public int KnownSpots => _store?.Count ?? 0;

    private string StorePath => Path.Combine(_pluginInterface.ConfigDirectory.FullName, StoreFileName);

    /// <summary>Palace Pal's database on this machine, beside Charon's own config folder.</summary>
    private string? PalacePalPath
    {
        get
        {
            var root = _pluginInterface.ConfigDirectory.Parent;
            return root == null ? null : Path.Combine(root.FullName, "PalacePal", "palace-pal.data.sqlite3");
        }
    }

    public bool PalacePalFileExists => PalacePalPath is { } path && File.Exists(path);

    public void Update(DateTime nowUtc)
    {
        // Answers go out whether or not THIS box is in a deep dungeon — a toon idling in town is
        // exactly the one with time to answer.
        SendDueAnswer(nowUtc);

        var snapshot = _reader.GetSnapshot();
        if (!snapshot.Active)
        {
            if (_dirty)
                Save();
            _lastFloor = -1;
            Status = $"not in a deep dungeon · {KnownSpots:N0} spots known";
            return;
        }

        EnsureLoaded();

        // A new floor (or a new floor set) knows nothing yet — the pomanders are per floor.
        var territory = _clientState.TerritoryType;
        if (snapshot.Floor != _lastFloor || territory != _lastTerritory)
        {
            if (territory != _lastTerritory)
                AskFleet(territory);

            _intel.Reset();
            _activeLayout = -1;
            _lastFloor = snapshot.Floor;
            _lastTerritory = territory;
        }

        if (nowUtc >= _nextScanUtc)
        {
            _nextScanUtc = nowUtc + ScanInterval;
            Record(territory);
            FindActiveLayout(territory);
        }

        if (_dirty && nowUtc - _lastSaveUtc >= SaveInterval)
            Save();

        var (traps, hoards) = _store!.CountIn(territory);
        var layout = _activeLayout < 0 ? "layout not placed yet" : $"layout {(char)('A' + _activeLayout)}, {_lastHidden} off this floor";
        Status = $"floor {snapshot.Floor} · {traps:N0} trap / {hoards:N0} hoard spots here · {layout} · {_intel.Describe()}"
                 + (_seenThisSession > 0 ? $" · {_seenThisSession} recorded this session" : "")
                 + (_sharedIn > 0 ? $" · {_sharedIn} from the fleet" : "");
    }

    /// <summary>
    /// The spots worth drawing around <paramref name="center"/>, given what the floor has revealed. Each
    /// kind has its own reach: a trap matters where you are about to step, a hoard spot is somewhere you
    /// would walk TO, so the caller can draw those across the whole floor.
    /// </summary>
    public IReadOnlyList<Spot> Near(Vector3 center, float trapRange, float hoardRange, bool traps, bool hoards)
    {
        var snapshot = _reader.GetSnapshot();
        if (_store == null || !snapshot.Active)
            return Array.Empty<Spot>();

        var territory = _clientState.TerritoryType;
        var layouts = _geometry.Layouts(territory);
        var list = new List<Spot>();
        var hidden = 0;
        foreach (var spot in _store.In(territory))
        {
            if (spot.Kind == SpotKind.Trap ? !traps : !hoards)
                continue;
            if (!_intel.Shows(spot.Kind))
                continue;
            if (!FloorRooms.OnThisFloor(layouts, _activeLayout, snapshot.Rooms, spot.X, spot.Z))
            {
                hidden++;
                continue;
            }

            var range = spot.Kind == SpotKind.Trap ? trapRange : hoardRange;
            if (Vector3.DistanceSquared(center, new Vector3(spot.X, spot.Y, spot.Z)) <= range * range)
                list.Add(spot);
        }

        _lastHidden = hidden;
        return list;
    }

    /// <summary>A frame from another box on <c>charon.spots</c>.</summary>
    public void OnRelayMessage(string json)
    {
        var message = SpotRelay.Parse(json);
        if (message == null || message.From == _localName())
            return;

        EnsureLoaded();
        switch (message.Act)
        {
            case SpotRelay.ActSeen:
            case SpotRelay.ActSpots:
                foreach (var spot in SpotRelay.SpotsOf(message))
                {
                    // A shared spot keeps its own Seen flag: an imported guess stays a guess on the
                    // receiving PC too, and a live sighting is a sighting wherever it lands.
                    var seen = message.Act == SpotRelay.ActSeen || spot.Seen;
                    if (_store!.Add(message.Territory, spot.Kind, spot.X, spot.Y, spot.Z, seen))
                    {
                        _dirty = true;
                        _sharedIn++;
                    }
                }

                // Someone who knows at least as much has answered this ask — ours would only repeat it.
                if (message.Act == SpotRelay.ActSpots && _pendingAnswer is { } pending
                    && pending.Id == message.Id && message.Known >= _store!.In(pending.Territory).Count)
                    pending.Cancelled = true;
                break;

            case SpotRelay.ActAsk:
                var mine = _store!.In(message.Territory).Count;
                if (SpotRelay.ShouldAnswer(mine, message.Known, bestOtherAnswer: 0))
                {
                    // Jitter, so the box that knows most usually speaks first and the rest stand down.
                    var delay = TimeSpan.FromMilliseconds(300 + _random.Next(1500));
                    _pendingAnswer = new PendingAnswer(message.Id, message.Territory, DateTime.UtcNow + delay);
                }

                break;
        }
    }

    /// <summary>
    /// Reads Palace Pal's saved spots into ours. Idempotent: the grid dedupe means a second import
    /// adds nothing, so the button is safe to press again.
    /// </summary>
    public int ImportFromPalacePal()
    {
        EnsureLoaded();
        var path = PalacePalPath;
        if (path == null || !File.Exists(path))
        {
            ImportStatus = "no Palace Pal database on this machine";
            return 0;
        }

        try
        {
            // Palace Pal may be running: open without locking it out.
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }

            var table = SqliteTableReader.Read(bytes, "Locations");
            int territoryCol = table.IndexOf("TerritoryType"), typeCol = table.IndexOf("Type"),
                xCol = table.IndexOf("X"), yCol = table.IndexOf("Y"), zCol = table.IndexOf("Z"),
                seenCol = table.IndexOf("Seen");
            if (new[] { territoryCol, typeCol, xCol, yCol, zCol }.Any(i => i < 0))
            {
                ImportStatus = "Palace Pal's database has an unexpected layout — nothing imported";
                return 0;
            }

            var added = 0;
            foreach (var row in table.Rows)
            {
                if (row[territoryCol] is not long territory || row[typeCol] is not long type)
                    continue;

                var seen = seenCol >= 0 && row[seenCol] is long s && s != 0;
                if (_store!.Add((uint)territory, (SpotKind)(byte)type,
                        ToFloat(row[xCol]), ToFloat(row[yCol]), ToFloat(row[zCol]), seen))
                    added++;
            }

            var wal = path + "-wal";
            var walNote = File.Exists(wal) && new FileInfo(wal).Length > 0
                ? " (Palace Pal has unsaved changes in its -wal file; close the game and import again for those)"
                : "";

            ImportStatus = $"read {table.Rows.Count:N0} rows from Palace Pal, {added:N0} new{walNote}";
            _log.Info("Palace spots: {0}", ImportStatus);

            _config.PalacePalImported = true;
            _saveConfig();
            if (added > 0)
            {
                _dirty = true;
                Save();
            }

            return added;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or IndexOutOfRangeException or ArgumentException)
        {
            ImportStatus = $"could not read Palace Pal's database: {ex.Message}";
            _log.Warning(ex, "Palace spots: import failed");
            return 0;
        }
    }

    public void Dispose()
    {
        _chat.ChatMessage -= OnChatMessage;
        if (_dirty)
            Save();
    }

    private void AskFleet(uint territory)
    {
        var me = _localName();
        if (me.Length == 0)
            return;

        _relay.Publish(RelayClient.SpotsChannel,
            SpotRelay.Ask(me, Guid.NewGuid().ToString("N")[..12], territory, _store!.In(territory).Count));
    }

    private void SendDueAnswer(DateTime nowUtc)
    {
        if (_pendingAnswer is not { } pending || nowUtc < pending.DueUtc)
            return;

        _pendingAnswer = null;
        var me = _localName();
        if (pending.Cancelled || _store == null || me.Length == 0)
            return;

        var spots = _store.In(pending.Territory);
        foreach (var frame in SpotRelay.Answer(me, pending.Id, pending.Territory, spots))
            _relay.Publish(RelayClient.SpotsChannel, frame);
        _log.Debug("Palace spots: answered ask {0} with {1} spots for territory {2}", pending.Id, spots.Count, pending.Territory);
    }

    /// <summary>The floor's layout, read from where we are standing (the two layouts sit apart).</summary>
    private void FindActiveLayout(uint territory)
    {
        if (_activeLayout >= 0 || _objectTable.LocalPlayer is not { } me)
            return;

        _activeLayout = FloorRooms.LayoutAt(_geometry.Layouts(territory), me.Position.X, me.Position.Z);
    }

    private void Record(uint territory)
    {
        foreach (var obj in _objectTable)
        {
            SpotKind kind;
            if (DeepDungeonIds.Traps.ContainsKey(obj.BaseId))
                kind = SpotKind.Trap;
            else if (obj.BaseId is DeepDungeonIds.AccursedHoard or DeepDungeonIds.AccursedHoardCoffer)
                kind = SpotKind.Hoard;
            else
                continue;

            var p = obj.Position;
            if (p.LengthSquared() < 0.01f)
                continue; // an object not placed yet reads as the origin

            if (_store!.Add(territory, kind, p.X, p.Y, p.Z, seen: true))
            {
                _dirty = true;
                _seenThisSession++;
                _newlySeen.Add(new Spot(kind, p.X, p.Y, p.Z, true));
            }
        }

        // Tell the fleet at once — one Sight reveals a whole floor's traps, which fits a frame or two.
        if (_newlySeen.Count > 0)
        {
            var me = _localName();
            if (me.Length > 0)
            {
                foreach (var batch in _newlySeen.Chunk(SpotRelay.BatchSize))
                    _relay.Publish(RelayClient.SpotsChannel, SpotRelay.Seen(me, territory, batch));
            }

            _newlySeen.Clear();
        }
    }

    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (_lastFloor < 0)
            return; // not in a deep dungeon — nothing here can be floor news

        _texts ??= LoadTexts();
        var news = _texts.Classify(message.Message.TextValue);
        if (news != FloorNews.None)
            _intel.Apply(news);
    }

    private FloorNewsTexts LoadTexts()
    {
        string Row(uint id)
        {
            try
            {
                return _data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>()?.GetRowOrDefault(id)?.Text.ExtractText() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Palace spots: LogMessage row {0} unreadable", id);
                return string.Empty; // blank never matches — the spots simply stay up
            }
        }

        return new FloorNewsTexts(
            Row(FloorNewsTexts.TrapsRevealedRow), Row(FloorNewsTexts.TrapsRemovedRow),
            Row(FloorNewsTexts.HoardHereRow), Row(FloorNewsTexts.NoHoardRow), Row(FloorNewsTexts.HoardFoundRow));
    }

    private void EnsureLoaded()
    {
        if (_store != null)
            return;

        _store = ReadStoreFile() ?? new SpotStore();
        if (!_config.PalacePalImported && PalacePalFileExists)
            ImportFromPalacePal();
    }

    private SpotStore? ReadStoreFile()
    {
        try
        {
            if (!File.Exists(StorePath))
                return null;

            var rows = JsonSerializer.Deserialize<Dictionary<string, List<float[]>>>(File.ReadAllText(StorePath));
            return SpotStore.FromRows(rows);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Palace spots: could not read {0}", StorePath);
            return null;
        }
    }

    /// <summary>Merge with whatever another client on this machine saved, then write atomically.</summary>
    private void Save()
    {
        if (_store == null)
            return;

        try
        {
            var onDisk = ReadStoreFile();
            if (onDisk != null)
                _store.Merge(onDisk);

            var temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_store.ToRows()));
            File.Move(temp, StorePath, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Palace spots: save failed, will retry");
        }

        _lastSaveUtc = DateTime.UtcNow;
    }

    private static float ToFloat(object? value) => value switch
    {
        double d => (float)d,
        long l => l,
        _ => float.NaN, // rejected by the store
    };
}
