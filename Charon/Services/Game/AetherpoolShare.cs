using System;
using System.Collections.Generic;
using Charon.Features.DeepDungeon;
using Charon.Ipc;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Shares this toon's aetherpool arm and armor with the fleet over the LAN relay
/// (<c>charon.aetherpool</c>) and keeps what the others report (<see cref="AetherpoolBoard"/>).
///
/// Only while inside a deep dungeon — that is the only time the levels are readable. A box reports on
/// entering, when a level changes and every 30 seconds, and asks once on entering so it does not wait
/// out everyone else's 30 seconds; an ask is answered after a short random delay so a whole fleet does
/// not answer in one burst.
/// </summary>
public sealed class AetherpoolShare
{
    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BroadcastEvery = TimeSpan.FromSeconds(30);

    private readonly DeepDungeonReader _reader;
    private readonly RelayClient _relay;
    private readonly Func<string> _localName;
    private readonly IChatGui _chat;
    private readonly Func<bool> _announce;
    private readonly IPluginLog _log;
    private readonly AetherpoolBoard _board = new();
    private readonly Random _random = new();
    private readonly Dictionary<string, (int Dungeon, int Arm, int Armor)> _logged = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _nextReadUtc;
    private DateTime _lastBroadcastUtc;
    private DateTime? _replyAtUtc;
    private (int Dungeon, int Arm, int Armor)? _lastSent;
    private bool _wasActive;

    public AetherpoolShare(DeepDungeonReader reader, RelayClient relay, Func<string> localName, IChatGui chat,
        Func<bool> announce, IPluginLog log)
    {
        _reader = reader;
        _relay = relay;
        _localName = localName;
        _chat = chat;
        _announce = announce;
        _log = log;
    }

    public string Status { get; private set; } = "not in a deep dungeon";

    /// <summary>The last increase put in chat, for the Debug line.</summary>
    public string LastAnnounced { get; private set; } = "none yet";

    /// <summary>The other toons heard from recently in the kind of deep dungeon we are in. Empty outside one.</summary>
    public IReadOnlyList<AetherpoolEntry> Others(DateTime nowUtc)
    {
        var snapshot = _reader.GetSnapshot();
        return snapshot.Active ? _board.For(snapshot.DungeonId, nowUtc) : [];
    }

    public void Update(DateTime nowUtc)
    {
        if (nowUtc < _nextReadUtc)
            return;
        _nextReadUtc = nowUtc + ReadEvery;

        var snapshot = _reader.GetSnapshot();
        var me = _localName();
        if (!snapshot.Active || me.Length == 0)
        {
            _wasActive = false;
            _lastSent = null;
            _replyAtUtc = null;
            Status = "not in a deep dungeon";
            return;
        }

        var mine = (snapshot.DungeonId, snapshot.Arm, snapshot.Armor);
        var entered = !_wasActive;
        _wasActive = true;

        if (entered)
        {
            _log.Info($"Aetherpool: this toon reads arm +{snapshot.Arm}, armor +{snapshot.Armor} (deep dungeon {snapshot.DungeonId})");
            _relay.Publish(RelayClient.AetherpoolChannel, AetherpoolRelay.SerializeAsk(me));
        }

        var answerDue = _replyAtUtc is { } replyAt && nowUtc >= replyAt;
        if (entered || answerDue || _lastSent != mine || nowUtc - _lastBroadcastUtc >= BroadcastEvery)
        {
            _relay.Publish(RelayClient.AetherpoolChannel,
                AetherpoolRelay.SerializeState(me, snapshot.DungeonId, snapshot.Arm, snapshot.Armor));
            _lastSent = mine;
            _lastBroadcastUtc = nowUtc;
            _replyAtUtc = null;
        }

        var heard = _board.For(snapshot.DungeonId, nowUtc).Count;
        Status = $"mine {AetherpoolBoard.Describe(snapshot.Arm, snapshot.Armor)} · heard from {heard} other toon(s)"
                 + (heard == 0 ? " (none reporting — they need this build, Daedalus's LAN relay, and to be in a deep dungeon)" : "")
                 + $" · announced: {(_announce() ? LastAnnounced : "off")}";
    }

    public void OnRelayMessage(string json, DateTime nowUtc)
    {
        var message = AetherpoolRelay.Parse(json);
        if (message == null || message.Name.Equals(_localName(), StringComparison.OrdinalIgnoreCase))
            return;

        if (message.Act == AetherpoolRelay.ActAsk)
        {
            // Only a box that is in a deep dungeon has anything to say; Update sends it when due.
            if (_wasActive)
                _replyAtUtc ??= nowUtc + TimeSpan.FromMilliseconds(_random.Next(200, 1500));
            return;
        }

        var previous = _board.Last(message.Name);
        if (!_board.Upsert(message.Name, message.Dungeon, message.Arm, message.Armor, nowUtc))
            return;

        // The game tells each toon about its OWN increase only; this says it for the others. Only while we are
        // in a deep dungeon ourselves, and printed to this client's chat alone - nothing is sent to anyone.
        var increase = AetherpoolBoard.Increase(previous, message.Name, message.Dungeon, message.Arm, message.Armor);
        if (increase != null && _wasActive && _announce())
        {
            _chat.Print($"[Charon] {increase}");
            LastAnnounced = $"{increase} at {nowUtc.ToLocalTime():HH:mm:ss}";
        }

        // Logged when new or changed only (levels move a few times a run), so a log shows the exchange works.
        var heard = (message.Dungeon, message.Arm, message.Armor);
        if (!_logged.TryGetValue(message.Name, out var before) || before != heard)
        {
            _logged[message.Name] = heard;
            _log.Info($"Aetherpool: {message.Name} reports arm +{message.Arm}, armor +{message.Armor} (deep dungeon {message.Dungeon})");
        }
    }
}
