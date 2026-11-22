using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.DeepDungeon;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace Charon.Services.Game;

/// <summary>
/// Uses a pomander while the party's pouch is FULL of that kind, so the next one that drops is not
/// turned away (who and when: <see cref="AutoPomanderPolicy"/>). Per character, off unless switched on;
/// any kind can be excluded.
///
/// "Full" is the pouch holding <see cref="AutoPomanderPolicy.Cap"/>, read from the pouch every tick, and
/// NOTHING ELSE. The game's "returned to its coffer" message (system log message 7222, its integer
/// argument the DeepDungeonItem row) is only logged: treating it as a second sign of fullness emptied a
/// pouch (see <see cref="OnLogMessage"/>).
///
/// Facts, from BossMod's deep dungeon code (BSD-3-Clause, Andrew Gilewsky — read, not copied): message
/// 7222 and its argument; the limit of 3; a pomander is used with
/// <c>InstanceContentDeepDungeon.UsePomander(slot)</c>, the slot being the item's index in the DeepDungeon
/// sheet's PomanderSlot list for this kind of dungeon; the pouch entry's flag bit 0 is the game's own
/// "usable now" and bit 1 "in effect"; status 1094 (Item Penalty) forbids items; nothing is sent during an
/// animation lock.
///
/// A kind already in effect is not used again (that would only waste it). A use is checked by the pouch
/// count going down, never by the call having been made; three uses that do not show there and it stops
/// trying that kind for a minute. One pomander per 4.5 seconds at most, across all kinds.
///
/// UNVERIFIED: the buff ids (<see cref="StrengthStatus"/>, <see cref="SteelStatus"/>). The Status sheet
/// has those rows under the right names, but nothing in the game data ties them to the pomanders — so
/// the party's statuses are logged whenever a steel or strength is used or returned to its coffer, and
/// that line is the proof. With a wrong id every toon reads as unbuffed and the first in the order
/// takes one every time the pouch fills.
/// </summary>
public sealed unsafe class AutoPomander : IDisposable
{
    public const uint StrengthStatus = 687;
    public const uint SteelStatus = 1100;
    private const uint ItemPenaltyStatus = 1094;
    private const int MaxAttempts = 3;

    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(250);
    // Live, 2026-11-21: on one client every first call did nothing and the count fell 0.25s after a second
    // call 2.6s later; on another the first call landed in 0.25s. Why the first sometimes does nothing is
    // not known (that toon was being played by hand, mid-fight). The retry is unhurried either way.
    private static readonly TimeSpan BetweenUses = TimeSpan.FromSeconds(4.5);
    private static readonly TimeSpan GiveUpFor = TimeSpan.FromSeconds(60);

    private readonly IChatGui _chat;
    private readonly IObjectTable _objects;
    private readonly IPartyList _party;
    private readonly IDataManager _data;
    private readonly DeepDungeonReader _reader;
    private readonly Func<bool> _enabled;
    private readonly Func<uint, bool> _skipped;
    private readonly Func<bool> _inCombat;
    private readonly IPluginLog _log;

    private sealed class ItemState
    {
        public int Attempts;
        public int CountAtAttempt = -1;
        public DateTime GaveUpUntilUtc;
        /// <summary>Since when this kind has been ready for THIS toon to use; null while it is not.</summary>
        public DateTime? ReadySinceUtc;
        public string Logged = string.Empty;
    }

    private readonly Dictionary<uint, ItemState> _state = new();
    private List<(uint Id, string Name)>? _names;
    private DateTime _nextTickUtc;
    private DateTime _lastUseUtc;
    private DateTime? _dumpAtUtc;
    private string _dumpReason = string.Empty;

    /// <param name="skipped">True for a DeepDungeonItem row the player has excluded.</param>
    public AutoPomander(IChatGui chat, IObjectTable objects, IPartyList party, IDataManager data,
        DeepDungeonReader reader, Func<bool> enabled, Func<uint, bool> skipped, Func<bool> inCombat, IPluginLog log)
    {
        _chat = chat;
        _objects = objects;
        _party = party;
        _data = data;
        _reader = reader;
        _enabled = enabled;
        _skipped = skipped;
        _inCombat = inCombat;
        _log = log;
        _chat.LogMessage += OnLogMessage;
    }

    public string Status { get; private set; } = "not in a deep dungeon";

    /// <summary>Every pomander and protomander the game has, by its DeepDungeonItem row — for the settings list.</summary>
    public IReadOnlyList<(uint Id, string Name)> AllNames()
    {
        if (_names != null)
            return _names;

        var names = new List<(uint, string)>();
        var sheet = _data.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeonItem>();
        if (sheet != null)
        {
            foreach (var row in sheet)
            {
                var name = row.Singular.ExtractText();
                if (row.RowId > 0 && name.Length > 0)
                    names.Add((row.RowId, name));
            }
        }

        return _names = names;
    }

    public void Dispose() => _chat.LogMessage -= OnLogMessage;

    private void OnLogMessage(ILogMessage message)
    {
        var id = message.LogMessageId;
        if (id != AutoPomanderPolicy.OvercapLogMessageId && id != PomanderLog.UseLogMessageId)
            return;

        try
        {
            if (!_reader.GetSnapshot().Active)
                return;

            uint itemId = 0;
            for (var i = 0; i < message.ParameterCount; i++)
            {
                if (message.TryGetIntParameter(i, out var value) && PomanderLog.IsPomander(value))
                {
                    itemId = (uint)value;
                    break;
                }
            }

            if (itemId == 0)
                return;

            // The party's statuses a moment after a steel or strength are the evidence for the buff ids.
            if (AutoPomanderPolicy.KindOf(itemId) != null)
            {
                _dumpAtUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
                _dumpReason = id == PomanderLog.UseLogMessageId ? $"after a {ItemName(itemId)} was used" : $"with a {ItemName(itemId)} returned to its coffer";
            }

            if (id != AutoPomanderPolicy.OvercapLogMessageId)
                return;

            // Logged only. It used to ALSO count as "full" for 30 seconds, whatever the pouch held — and
            // since every client retries that coffer every 10 seconds the message kept coming, so one use
            // was followed by another every 2.5 seconds until the pouch was empty (user, 2026-11-21: "just
            // used 4 haste pomanders and now i am out of them"). Full is the count, and nothing else.
            _log.Info($"Auto pomander: the party cannot carry another {ItemName(itemId)}{(_enabled() ? string.Empty : " (off for this character)")}");
        }
        catch (Exception ex)
        {
            // A log hook must never throw into the game's message path.
            _log.Warning(ex, "Auto pomander: could not read a log message");
        }
    }

    public void Update(DateTime nowUtc)
    {
        if (nowUtc < _nextTickUtc)
            return;
        _nextTickUtc = nowUtc + Pace;

        try
        {
            Tick(nowUtc);
        }
        catch (Exception ex)
        {
            Status = "threw (see log)";
            _log.Warning(ex, "Auto pomander tick threw");
        }
    }

    private void Tick(DateTime nowUtc)
    {
        var framework = EventFramework.Instance();
        var content = framework == null ? null : framework->GetInstanceContentDeepDungeon();
        if (content == null)
        {
            _dumpAtUtc = null;
            _state.Clear();
            Status = "not in a deep dungeon";
            return;
        }

        if (_dumpAtUtc is { } dumpAt && nowUtc >= dumpAt)
        {
            _dumpAtUtc = null;
            LogPartyStatuses(_dumpReason);
        }

        if (!_enabled())
        {
            Status = "off for this character";
            return;
        }

        var slots = Slots(content->DeepDungeonId);
        var full = new List<string>();
        for (var slot = 0; slot < slots.Count && slot < content->Items.Length; slot++)
        {
            if (slots[slot] == 0)
                continue;

            var line = Step(content, slots[slot], slot, nowUtc);
            if (line != null)
                full.Add(line);
        }

        Status = full.Count == 0 ? "no kind is full" : string.Join(" · ", full);
    }

    /// <summary>One kind's turn. Null when the pouch is not full of it.</summary>
    private string? Step(InstanceContentDeepDungeon* content, uint itemId, int slot, DateTime nowUtc)
    {
        var entry = content->Items[slot];
        var state = StateOf(itemId);
        var name = ItemName(itemId);

        if (state.CountAtAttempt >= 0 && entry.Count < state.CountAtAttempt)
        {
            Note(state, $"used a {name} — the pouch now holds {entry.Count}");
            state.Attempts = 0;
            state.CountAtAttempt = -1;
        }

        if (!AutoPomanderPolicy.IsFull(entry.Count))
        {
            state.Attempts = 0;
            state.CountAtAttempt = -1;
            state.ReadySinceUtc = null;
            return null;
        }

        var held = $"{PomanderLog.Stacked([new PomanderUse(0, itemId, name, string.Empty, nowUtc)])[0]} full";
        if (_skipped(itemId))
        {
            state.ReadySinceUtc = null;
            return $"{held} — excluded";
        }

        var local = _objects.LocalPlayer;
        var kind = AutoPomanderPolicy.KindOf(itemId);
        IReadOnlyList<PomanderCandidate> queue;
        int unseen;
        if (kind is { } selfBuff)
        {
            queue = AutoPomanderPolicy.Queue(selfBuff, Candidates(selfBuff, out unseen));
            if (queue.Count == 0)
            {
                state.ReadySinceUtc = null;
                Note(state, $"{name}: the pouch is full and everyone's buff has more than a minute left — none used");
                return $"{held} — everyone is covered";
            }
        }
        else
        {
            if (entry.IsActive)
            {
                state.ReadySinceUtc = null;
                Note(state, $"{name}: the pouch is full but it is already in effect — none used");
                return $"{held} — already in effect";
            }

            queue = AutoPomanderPolicy.QueueAny(Candidates(null, out unseen));
            if (queue.Count == 0)
            {
                state.ReadySinceUtc = null;
                return $"{held} — nobody alive to use it";
            }
        }

        var position = -1;
        for (var i = 0; i < queue.Count && local != null; i++)
        {
            if (queue[i].EntityId == local.EntityId)
            {
                position = i;
                break;
            }
        }

        var first = queue[0];
        if (position < 0)
        {
            state.ReadySinceUtc = null;
            Note(state, $"{name}: the pouch is full — it is {first.Name}'s to use ({first.Role})");
            return $"{held} — {first.Name}'s to use" + (unseen > 0 ? $" ({unseen} out of sight)" : string.Empty);
        }

        // A kind that needs a fight is not ready until this toon is in one; the turns are counted from then,
        // so a whole party entering combat does not all use one at once.
        if (AutoPomanderPolicy.TimingOf(itemId) == PomanderTiming.InCombat && !_inCombat())
        {
            state.ReadySinceUtc = null;
            return $"{held} — waiting for a fight";
        }

        state.ReadySinceUtc ??= nowUtc;
        var readyFor = nowUtc - state.ReadySinceUtc.Value;
        if (!AutoPomanderPolicy.IsTurn(position, readyFor))
        {
            var wait = AutoPomanderPolicy.TurnLength * position - readyFor;
            Note(state, $"{name}: the pouch is full — it is {first.Name}'s to use ({first.Role}); mine if it is still full after {position} turn(s)");
            return $"{held} — {first.Name}'s to use, mine in {Math.Ceiling(wait.TotalSeconds):0}s if it is not taken";
        }

        if (nowUtc < state.GaveUpUntilUtc)
            return $"{held} — mine, but {MaxAttempts} uses did not take; trying again shortly";

        if (state.Attempts >= MaxAttempts)
        {
            state.Attempts = 0;
            state.CountAtAttempt = -1;
            state.GaveUpUntilUtc = nowUtc + GiveUpFor;
            Note(state, $"used a {name} {MaxAttempts} times and the pouch never went down — waiting {GiveUpFor.TotalSeconds:0}s");
            return $"{held} — mine, but {MaxAttempts} uses did not take; trying again shortly";
        }

        if (HasStatus(local!, ItemPenaltyStatus))
            return $"{held} — mine, waiting out Item Penalty";

        if (!entry.IsUsable)
            return $"{held} — mine, but the game marks it unusable right now";

        if (nowUtc - _lastUseUtc < BetweenUses || ActionManager.Instance()->AnimationLock > 0)
            return $"{held} — mine, waiting for the game to take it";

        if (state.Attempts == 0)
            state.CountAtAttempt = entry.Count;
        state.Attempts++;
        _lastUseUtc = nowUtc;
        content->UsePomander((uint)slot);
        _log.Info($"Auto pomander: using a {name} (pouch slot {slot}, holding {entry.Count}, slot item id {entry.ItemId}, flags {entry.Flags}, attempt {state.Attempts}, position {position} in the queue)");
        return $"{held} — using one";
    }

    private ItemState StateOf(uint itemId)
    {
        if (!_state.TryGetValue(itemId, out var state))
            _state[itemId] = state = new ItemState();
        return state;
    }

    /// <summary>Logs a line once, not every tick it stays true.</summary>
    private void Note(ItemState state, string what)
    {
        if (state.Logged == what)
            return;
        state.Logged = what;
        _log.Info($"Auto pomander: {what}");
    }

    /// <param name="kind">The buff to read off each member, or null when the pomander is not a buff.</param>
    private List<PomanderCandidate> Candidates(SelfBuffPomander? kind, out int unseen)
    {
        var buff = kind switch
        {
            SelfBuffPomander.Steel => SteelStatus,
            SelfBuffPomander.Strength => StrengthStatus,
            _ => 0u,
        };
        var list = new List<PomanderCandidate>();

        foreach (var member in PartyCharacters(out unseen))
        {
            var left = 0f;
            if (buff != 0)
            {
                foreach (var status in member.StatusList)
                {
                    if (status.StatusId == buff)
                        left = MathF.Max(left, status.RemainingTime);
                }
            }

            list.Add(new PomanderCandidate(member.Name.TextValue, member.EntityId,
                AutoPomanderPolicy.RoleOf(member.ClassJob.ValueNullable?.Role ?? 0),
                member.CurrentHp > 0 && !member.IsDead, left));
        }

        return list;
    }

    /// <summary>The party as the object table shows it — just us when solo.</summary>
    private List<IBattleChara> PartyCharacters(out int unseen)
    {
        var list = new List<IBattleChara>();
        unseen = 0;
        if (_party.Length == 0)
        {
            if (_objects.LocalPlayer is IBattleChara self)
                list.Add(self);
            return list;
        }

        foreach (var member in _party)
        {
            if (_objects.SearchByEntityId(member.EntityId) is IBattleChara character)
                list.Add(character);
            else
                unseen++;
        }

        return list;
    }

    private static bool HasStatus(IBattleChara character, uint statusId)
    {
        foreach (var status in character.StatusList)
        {
            if (status.StatusId == statusId)
                return true;
        }

        return false;
    }

    private void LogPartyStatuses(string reason)
    {
        var lines = PartyCharacters(out _).Select(c =>
            $"{c.Name.TextValue} [{string.Join(", ", c.StatusList.Where(s => s.StatusId != 0).Select(s => $"{s.StatusId} {s.RemainingTime:0}s"))}]");
        _log.Info($"Auto pomander: party statuses {reason}: {string.Join(" · ", lines)}");
    }

    /// <summary>This kind of dungeon's pouch, slot by slot (the DeepDungeon sheet's PomanderSlot list); 0 = no item.</summary>
    private IReadOnlyList<uint> Slots(byte dungeonId)
    {
        if (_slots.TryGetValue(dungeonId, out var known))
            return known;

        var list = new List<uint>();
        var row = _data.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeon>()?.GetRowOrDefault(dungeonId);
        if (row != null)
        {
            foreach (var slot in row.Value.PomanderSlot)
                list.Add(slot.RowId);
        }

        return _slots[dungeonId] = list;
    }

    private readonly Dictionary<byte, IReadOnlyList<uint>> _slots = new();

    private string ItemName(uint itemId)
    {
        foreach (var (id, name) in AllNames())
        {
            if (id == itemId)
                return name;
        }

        return $"pomander #{itemId}";
    }
}
