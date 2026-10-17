using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Variant;
using Charon.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Charon.Services.Game;

/// <summary>
/// The variant-dungeon guide's game side: this character's route record (the game's own bit array, see
/// <see cref="VariantRecord"/>), the record titles from the VVDNotebookContents sheet, which dungeon we are
/// in, and the route being guided — chosen in the UI, or the suggested next one when nobody has chosen.
/// Read-only: nothing here acts on the game.
///
/// FLEET SHARING (<c>charon.variant</c>, <see cref="VariantRelay"/>): only a toon's own client can read its
/// record, so every box reports its own — on change, once loaded, and every minute — and answers an "ask"
/// with it. A box asks on load and on entering a variant dungeon. With the party's records in hand the
/// suggestion becomes the route the MOST of the party still needs (<see cref="VariantNeeds.GroupSuggest"/>),
/// and the Variant section shows who needs what. A party member with no record (not running Charon, or not
/// heard from yet) is UNKNOWN and listed as such — never counted as having or needing anything.
/// </summary>
public sealed unsafe class VariantService
{
    /// <summary>VVDNotebookContents column 2 is the record title (column 3 is the record's text).</summary>
    private const int TitleColumn = 2;

    private readonly IClientState _clientState;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly Dictionary<uint, string> _titles = new();
    private readonly Dictionary<int, int> _chosen = new();
    private readonly RelayClient _relay;
    private readonly Func<string> _localName;
    private readonly Func<IReadOnlyList<string>> _partyNames;
    private readonly FleetVariantRecords _fleet = new();
    private readonly Random _random = new();
    private bool _titlesRead;

    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BroadcastEvery = TimeSpan.FromSeconds(60);

    private DateTime _nextReadUtc;
    private DateTime _lastBroadcastUtc;
    private byte[]? _lastBroadcast;
    private bool _askedOnLoad;
    private DateTime? _replyAtUtc;

    public VariantService(IClientState clientState, IDataManager data, IPluginLog log, RelayClient relay,
        Func<string> localName, Func<IReadOnlyList<string>> partyNames)
    {
        _clientState = clientState;
        _data = data;
        _log = log;
        _relay = relay;
        _localName = localName;
        _partyNames = partyNames;
    }

    /// <summary>The Variant section's "who needs what" shows the whole fleet rather than the party.</summary>
    public bool ShowWholeFleet { get; set; }

    /// <summary>How many other toons' records this box has heard.</summary>
    public int FleetRecordCount => _fleet.Count;

    /// <summary>What the marker overlay drew last frame — set by the overlay, shown on the Debug line.</summary>
    public string OverlayStatus { get; set; } = "not drawn";

    /// <summary>The variant dungeon we are standing in, or null.</summary>
    public VariantDungeon? Current => VariantRoutes.ByTerritory(_clientState.TerritoryType);

    /// <summary>The completion bits, or null while the player state is not loaded — unknown, never "none done".</summary>
    public byte[]? ReadRecord()
    {
        var player = PlayerState.Instance();
        if (player == null || !player->IsLoaded)
            return null;
        return player->CompletedVVDNotebookContents.ToArray();
    }

    public VariantProgress? Progress(VariantDungeon dungeon)
    {
        var bits = ReadRecord();
        return bits == null ? null : VariantRecord.Progress(dungeon, bits);
    }

    /// <summary>
    /// The route being guided: the one chosen this session, else the PARTY's pick when at least two party
    /// members' records are known, else this toon's own next route, else route 1.
    /// </summary>
    public int Target(VariantDungeon dungeon) => _chosen.TryGetValue(dungeon.Series, out var chosen)
        ? chosen
        : Suggestion(dungeon) ?? 1;

    /// <summary>The suggested route, ignoring any choice — null when there is nothing left to suggest.</summary>
    public int? Suggestion(VariantDungeon dungeon)
    {
        var party = Group(forceFleet: false);
        if (party.IsParty && party.Known.Count >= 2)
            return VariantNeeds.GroupSuggest(dungeon, party.Known);

        var progress = Progress(dungeon);
        return progress == null ? null : VariantRecord.Suggest(progress);
    }

    /// <summary>True when the suggestion is the party's rather than this toon's own.</summary>
    public bool SuggestsForParty
    {
        get
        {
            var party = Group(forceFleet: false);
            return party.IsParty && party.Known.Count >= 2;
        }
    }

    /// <summary>Whose records to weigh: the party when in one, else (or when forced) every toon heard from.</summary>
    public sealed record GroupView(bool IsParty, IReadOnlyList<ToonRecord> Known, IReadOnlyList<string> Unknown);

    public GroupView Group(bool forceFleet)
    {
        var known = new List<ToonRecord>();
        var unknown = new List<string>();
        var me = _localName();
        var mine = ReadRecord();
        if (mine != null && me.Length > 0)
            known.Add(new ToonRecord(me, mine, DateTime.UtcNow, Local: true));
        else if (me.Length > 0)
            unknown.Add(me);

        var party = _partyNames();
        if (party.Count >= 2 && !forceFleet)
        {
            foreach (var name in party.Where(n => !n.Equals(me, StringComparison.OrdinalIgnoreCase)))
            {
                var record = _fleet.Get(name);
                if (record != null)
                    known.Add(record);
                else
                    unknown.Add(name);
            }

            return new GroupView(true, known, unknown);
        }

        known.AddRange(_fleet.All.Where(r => !r.Name.Equals(me, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase));
        return new GroupView(false, known, unknown);
    }

    /// <summary>The group the Variant section's table weighs — the party, or the fleet when asked or not in one.</summary>
    public GroupView TableGroup() => Group(ShowWholeFleet);

    /// <summary>Tick: report our own record when it changes or is due, answer an ask, ask once on load.</summary>
    public void Update(DateTime nowUtc)
    {
        if (_replyAtUtc is { } replyAt && nowUtc >= replyAt)
        {
            _replyAtUtc = null;
            Broadcast(nowUtc, force: true);
        }

        if (nowUtc < _nextReadUtc)
            return;
        _nextReadUtc = nowUtc + ReadEvery;

        if (ReadRecord() == null || _localName().Length == 0)
            return;

        if (!_askedOnLoad)
        {
            _askedOnLoad = true;
            Ask();
        }

        Broadcast(nowUtc, force: false);
    }

    /// <summary>Ask every box for its record — on load, and on entering a variant dungeon.</summary>
    public void Ask()
    {
        var me = _localName();
        if (me.Length > 0)
            _relay.Publish(RelayClient.VariantChannel, VariantRelay.SerializeAsk(me));
    }

    public void OnRelayMessage(string json, DateTime nowUtc)
    {
        var message = VariantRelay.Parse(json);
        if (message == null || message.Name.Equals(_localName(), StringComparison.OrdinalIgnoreCase))
            return;

        if (message.Act == VariantRelay.ActAsk)
        {
            // Jittered, so eight boxes answering the same ask do not land in one burst.
            _replyAtUtc ??= nowUtc + TimeSpan.FromMilliseconds(_random.Next(200, 1500));
            return;
        }

        var bits = VariantRelay.DecodeBits(message.Bits);
        if (bits != null && _fleet.Upsert(message.Name, bits, nowUtc))
            _log.Debug("[Variant] record from {0}: {1}", message.Name, message.Bits);
    }

    private void Broadcast(DateTime nowUtc, bool force)
    {
        var bits = ReadRecord();
        var me = _localName();
        if (bits == null || me.Length == 0)
            return;

        var changed = _lastBroadcast == null || !_lastBroadcast.AsSpan().SequenceEqual(bits);
        if (!force && !changed && nowUtc - _lastBroadcastUtc < BroadcastEvery)
            return;

        _relay.Publish(RelayClient.VariantChannel, VariantRelay.SerializeState(me, bits));
        _lastBroadcast = bits;
        _lastBroadcastUtc = nowUtc;
    }

    public bool IsChosen(VariantDungeon dungeon) => _chosen.ContainsKey(dungeon.Series);

    public void Choose(VariantDungeon dungeon, int route) => _chosen[dungeon.Series] = route;

    /// <summary>Back to following the suggestion.</summary>
    public void ClearChoice(VariantDungeon dungeon) => _chosen.Remove(dungeon.Series);

    /// <summary>The record's own title from the game, in the client's language; "Route n" if the sheet is unreadable.</summary>
    public string Title(VariantDungeon dungeon, int route)
    {
        if (!_titlesRead)
            ReadTitles();
        return _titles.TryGetValue(dungeon.NotebookRow(route), out var title) && title.Length > 0
            ? title
            : $"Route {route}";
    }

    public string Status
    {
        get
        {
            var bits = ReadRecord();
            var record = bits == null
                ? "record not loaded yet"
                : $"record {VariantRoutes.All.Sum(d => VariantRecord.Progress(d, bits).Done.Count)}/{VariantRoutes.All.Sum(d => d.Routes.Count)}";
            var party = Group(forceFleet: false);
            var fleet = $"fleet records {_fleet.Count}"
                        + (party.IsParty ? $" · party {party.Known.Count} known, {party.Unknown.Count} unknown" : "");
            var here = Current;
            if (here == null)
                return $"not in a variant dungeon · {record} · {fleet}";

            var how = IsChosen(here) ? "chosen" : SuggestsForParty ? "party pick" : "suggested";
            return $"in {here.Name} — guiding route {Target(here)} ({how}) · markers: {OverlayStatus} · {record} · {fleet}";
        }
    }

    private void ReadTitles()
    {
        _titlesRead = true;
        try
        {
            var sheet = _data.Excel.GetSheet<Lumina.Excel.RawRow>(null, "VVDNotebookContents");
            foreach (var row in sheet)
                _titles[row.RowId] = row.ReadStringColumn(TitleColumn).ExtractText();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Variant] VVDNotebookContents unreadable — routes will show by number");
        }
    }
}
