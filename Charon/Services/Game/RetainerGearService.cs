using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Diagnostics;
using Charon.Features.Gear;
using Charon.Features.Retainers;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>
/// Retainer gear: reads what a retainer wears, works out the best set from gear the player holds, and
/// (on an explicit click) puts it on. The choosing is <see cref="RetainerGearOptimizer"/>; this is the
/// game-facing half.
///
/// FACTS, each from a production source (no code taken from the last two — Henchman is AGPL and
/// VERMAXION carries no licence at all):
///   - a retainer's worn gear is <c>InventoryType.RetainerEquippedItems</c>, slots numbered like the
///     player's own (AutoRetainer, BSD-3, <c>ItemLevel.Calculate</c>);
///   - a piece's real stat is base + HQ bonus + melded materia, capped by item level and slot
///     (ECommons, MIT, <c>ExcelItemHelper.GetStat</c> / <c>GetStatCap</c>);
///   - the gear window is the addon <c>RetainerCharacter</c>, opened from the retainer's menu entry
///     "View retainer attributes and gear." (Addon rows 2388, and 2389 with a "(No main arm equipped)"
///     suffix — verified on XIVAPI), and equipping is a plain
///     <c>InventoryManager.MoveItemSlot(source, slot, RetainerEquippedItems, gearSlot)</c> while it is
///     open (VERMAXION and Henchman agree independently).
///
/// SAFETY: candidates never include anything one of the PLAYER'S OWN GEARSETS references — otherwise
/// the best Miner gear in the armoury is the player's own Miner set, and the optimiser would strip it.
/// An empty gearset list reads as "not loaded" and offers nothing (the armoury-cleanup rule).
///
/// Doctrine, as everywhere at the bell: Charon never selects the retainer for this — the player opens
/// her, presses Equip, and one piece moves per beat, each confirmed by re-reading its slot. Two moves
/// the game does not deliver stop the pass with the reason on screen.
/// </summary>
public sealed unsafe class RetainerGearService : IDisposable
{
    private const string GearWindow = "RetainerCharacter";
    private const uint AddonRowGear = 2388;
    private const uint GatheringParam = 72;
    private const uint PerceptionParam = 73;

    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long to leave the game alone after a move lands (or the gear window opens) before sending
    /// the next. LEARNED LIVE (2026-10-11): at one 500ms beat every piece's first attempt came back
    /// code 6 with both slots untouched, and the identical retry 3s later returned 0 and landed — the
    /// previous move was still settling. The FC chest needed the same patience (700ms between moves).
    /// </summary>
    private static readonly TimeSpan SettleAfterMove = TimeSpan.FromMilliseconds(1500);

    /// <summary>A move the game did not accept (non-zero code) is retried sooner than one it took.</summary>
    private static readonly TimeSpan RefusedRetry = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BellGoneGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PlanLife = TimeSpan.FromSeconds(1);
    private const int MaxFailures = 3;

    private static readonly InventoryType[] CandidateContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly GearManager _gear;
    private readonly RetainerReader _retainers;
    private readonly RetainerContentsReader _contents;
    private readonly RetainerPlanner _planner;
    private readonly CharonConfig _config;
    private readonly System.Action _save;
    private readonly Func<ulong> _contentId;
    private readonly Func<bool> _ventureAssistArmed;
    private readonly IChatGui _chat;

    /// <summary>The game's own red error line, if one arrived while a move of ours was in flight.</summary>
    private string _gameError = string.Empty;

    private readonly StepTrace _trace = new(capacity: 16);

    private string _gearEntryText = string.Empty;
    private bool _gearEntryRead;

    // The cached view the window draws (planning walks fifteen containers and runs a search).
    private View? _view;
    private DateTime _viewUtc;

    // The equip pass.
    private string? _equipping;
    private DateTime _nextBeatUtc;
    private DateTime _waitingSinceUtc;
    private DateTime _bellLastSeenUtc;
    private (GearSwap Swap, DateTime DeadlineUtc)? _inFlight;
    private DateTime _notBeforeUtc;
    private bool _windowWasUp;
    private int _done;
    private int _failures;
    private int _closeAttempts;
    private bool _closing;
    private DateTime _unreadableSinceUtc;

    // Remembering stats: only once the same retainer has been at the bell for a moment, because the
    // worn-gear container can still hold the PREVIOUS retainer's pieces for an instant after a switch.
    private static readonly TimeSpan SettleBeforeRemembering = TimeSpan.FromSeconds(2);
    private string? _atBell;
    private DateTime _atBellSinceUtc;
    private DateTime _nextRememberUtc;

    public RetainerGearService(
        IGameGui gameGui, IDataManager data, IPluginLog log, GearManager gear, RetainerReader retainers,
        RetainerContentsReader contents, RetainerPlanner planner, CharonConfig config, System.Action save, Func<ulong> contentId,
        Func<bool> ventureAssistArmed, IChatGui chat)
    {
        _ventureAssistArmed = ventureAssistArmed;
        _chat = chat;
        _chat.ChatMessage += OnChatMessage;
        _gameGui = gameGui;
        _data = data;
        _log = log;
        _gear = gear;
        _retainers = retainers;
        _contents = contents;
        _planner = planner;
        _config = config;
        _save = save;
        _contentId = contentId;
    }

    /// <summary>What the Gear tab shows for the retainer at the bell.</summary>
    /// <param name="CannotEquip">Why the game would refuse a gear change right now (she is out on a venture), or null.</param>
    public sealed record View(
        string Retainer, GearGoal Goal, GearPlan Plan, IReadOnlyList<RetainerPiece> Worn, int Candidates, string? Holding,
        string? CannotEquip);

    public string Status { get; private set; } = "idle";

    public bool Busy => _equipping != null;

    public StepTrace Trace => _trace;

    /// <summary>Who is summoned at the bell right now, whether or not her gear is readable yet.</summary>
    public string? RetainerAtBell => _contents.RetainerAtBell;

    private bool GearWindowUp
    {
        get
        {
            var window = (AtkUnitBase*)_gameGui.GetAddonByName(GearWindow).Address;
            return window != null && window->IsVisible;
        }
    }

    /// <summary>
    /// Opens her gear window from her menu — one explicit click, for when her gear has not loaded yet
    /// (like her bags, it may only be in memory once its window has been opened).
    /// </summary>
    public bool RequestGearWindow()
    {
        if (Busy || _ventureAssistArmed())
            return false;

        var clicked = ClickMenuEntry(GearEntryText());
        Status = clicked ? "opening her gear…" : "open the retainer's menu first";
        _view = null;
        return clicked;
    }

    /// <summary>The remembered numbers for a retainer, or null when her gear has never been read.</summary>
    public RetainerGearSnapshot? Snapshot(string retainer) =>
        _config.RetainerGear.TryGetValue(_planner.Key(_contentId(), retainer), out var snapshot) ? snapshot : null;

    /// <summary>
    /// The plan for whichever retainer is at the bell, or null when none is (her gear is only in memory
    /// while she is summoned). Cached for a second: the window redraws every frame.
    /// </summary>
    public View? Current(DateTime nowUtc)
    {
        if (_view != null && nowUtc - _viewUtc < PlanLife)
            return _view;

        _viewUtc = nowUtc;
        _view = Build(nowUtc);
        return _view;
    }

    /// <summary>Starts putting the planned gear on the retainer at the bell. False (with a reason in Status) if it cannot.</summary>
    public bool StartEquip(DateTime nowUtc)
    {
        var view = Build(nowUtc);
        if (view == null)
        {
            Status = "open a retainer at a bell first";
            return false;
        }

        if (view.Plan.Swaps.Count == 0)
        {
            Status = $"{view.Retainer}: nothing to change — {view.Plan.Note}";
            return false;
        }

        if (view.CannotEquip != null)
        {
            Status = view.CannotEquip;
            return false;
        }

        if (_contents.Busy)
        {
            Status = "a retainer fetch or refresh is running — let it finish";
            return false;
        }

        // The venture assist clicks entries in the same retainer menu; two agents on one menu is how
        // the wrong entry gets clicked.
        if (_ventureAssistArmed())
        {
            Status = "the venture assist is armed — stop it first";
            return false;
        }

        _equipping = view.Retainer;
        _done = 0;
        _failures = 0;
        _closeAttempts = 0;
        _closing = false;
        _inFlight = null;
        _windowWasUp = false;
        _notBeforeUtc = nowUtc;
        _unreadableSinceUtc = DateTime.MinValue;
        _waitingSinceUtc = nowUtc;
        _bellLastSeenUtc = nowUtc;
        _nextBeatUtc = nowUtc;
        _trace.Clear();
        _trace.Record(DateTime.Now, $"Start({view.Retainer}) {view.Plan.Swaps.Count} piece(s)");
        Status = $"equipping {view.Retainer}…";
        return true;
    }

    public void Dispose() => _chat.ChatMessage -= OnChatMessage;

    /// <summary>
    /// The FC chest's refusal watch: when the game refuses a move it says why in a red error line, and
    /// that sentence is worth more than any inference from a slot that did not change.
    /// </summary>
    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (_inFlight == null || message.LogKind != Dalamud.Game.Text.XivChatType.ErrorMessage)
            return;

        _gameError = message.Message.TextValue;
        _trace.Record(DateTime.Now, $"GAME SAID: {_gameError}");
    }

    public void Stop(string reason = "stopped")
    {
        if (_equipping == null)
            return;

        _trace.Record(DateTime.Now, $"Stop — {reason}");
        Status = _done > 0 ? $"{reason} — {_done} piece(s) equipped" : reason;
        _equipping = null;
        _inFlight = null;
        _view = null;
    }

    public void Update(DateTime nowUtc)
    {
        Remember(nowUtc);

        if (_equipping == null || nowUtc < _nextBeatUtc)
            return;
        _nextBeatUtc = nowUtc + Beat;

        var window = (AtkUnitBase*)_gameGui.GetAddonByName(GearWindow).Address;
        var windowUp = window != null && window->IsVisible && window->IsReady;

        // A transient nothing is never a definitive none: her character can blink out between the menu
        // and the gear window, so "gone" has to last before it means the bell was left — and her gear
        // window being on screen is itself proof she is still here, whatever the object table says.
        if (windowUp || _contents.RetainerAtBell == _equipping)
            _bellLastSeenUtc = nowUtc;
        else if (nowUtc - _bellLastSeenUtc > BellGoneGrace)
        {
            Stop($"{_equipping} is no longer at the bell");
            return;
        }
        else
        {
            _trace.Note(DateTime.Now, "bell-gap", "(between windows)");
            return;
        }

        if (_closing)
        {
            CloseGearWindow(window, windowUp);
            return;
        }

        if (!windowUp)
        {
            _windowWasUp = false;
            OpenGearWindow(nowUtc);
            return;
        }

        // The window has only just appeared: give it the same settling time a landed move gets.
        if (!_windowWasUp)
        {
            _windowWasUp = true;
            _notBeforeUtc = nowUtc + SettleAfterMove;
        }

        _waitingSinceUtc = nowUtc;

        if (_inFlight is { } flight)
        {
            ConfirmMove(flight.Swap, flight.DeadlineUtc, nowUtc);
            return;
        }

        // Never send a move while the last one is still settling — by the clock, and by the game's own
        // pending-operation ring.
        if (nowUtc < _notBeforeUtc || InventoryQuantityMover.HasPendingOperation())
        {
            _trace.Note(DateTime.Now, "settling", "(letting the last move settle)");
            return;
        }

        var view = Build(nowUtc);
        if (view == null)
        {
            // Unreadable for a moment is not "finished" — wait for the gear to be readable again, but
            // not forever: a stall has to end with its reason on screen.
            if (_unreadableSinceUtc == DateTime.MinValue)
                _unreadableSinceUtc = nowUtc;
            else if (nowUtc - _unreadableSinceUtc > WindowTimeout)
                Stop($"{_equipping}'s gear could not be read");

            _trace.Note(DateTime.Now, "unreadable", "(gear not readable yet)");
            return;
        }

        _unreadableSinceUtc = DateTime.MinValue;

        if (view.Plan.Swaps.Count == 0)
        {
            // Re-planned from live gear and nothing is left: the job is done.
            _closing = true;
            _trace.Record(DateTime.Now, $"Done — {_done} equipped");
            return;
        }

        Submit(view.Plan.Swaps[0], nowUtc);
    }

    // --- Reading ---

    private View? Build(DateTime nowUtc)
    {
        // Mid-pass the gear window vouches for her (see Update), so her name is the one being equipped.
        var name = _contents.RetainerAtBell ?? (GearWindowUp ? _equipping : null);
        if (name == null)
            return null;

        var row = _retainers.Read(nowUtc).FirstOrDefault(r => r.Name == name);
        var worn = ReadWorn();
        if (row == null || worn == null)
            return null;

        var key = _planner.Key(_contentId(), name);
        var goal = _planner.GearGoalFor(row, key);

        var gearsetIds = _gear.ReadGearsetItemIds();
        string? holding = null;
        List<RetainerPiece> candidates;
        if (gearsetIds.Count == 0)
        {
            candidates = [];
            holding = "your gearsets are not loaded, so nothing of yours is offered";
        }
        else
        {
            candidates = ReadCandidates(row.JobId, row.Level, gearsetIds.ToHashSet());
        }

        var state = VentureBoard.Compose(nowUtc, [row])[0].State;
        return new View(name, goal, RetainerGearOptimizer.Plan(worn, candidates, goal), worn, candidates.Count, holding,
            RetainerGearRules.ChangeBlocker(name, state));
    }

    /// <summary>Keeps each retainer's numbers once her gear is readable, so the venture picker can use them later.</summary>
    private void Remember(DateTime nowUtc)
    {
        var name = _contents.RetainerAtBell;
        if (name != _atBell)
        {
            _atBell = name;
            _atBellSinceUtc = nowUtc;
        }

        if (Busy || name == null || nowUtc < _nextRememberUtc || nowUtc - _atBellSinceUtc < SettleBeforeRemembering)
            return;
        _nextRememberUtc = nowUtc + SettleBeforeRemembering;

        var view = Current(nowUtc);
        if (view == null || view.Retainer != name || view.Worn.Count == 0)
            return;

        var stats = view.Plan.Before;
        var key = _planner.Key(_contentId(), view.Retainer);
        if (_config.RetainerGear.TryGetValue(key, out var held)
            && held.ItemLevel == stats.ItemLevel && held.Gathering == stats.Gathering && held.Perception == stats.Perception)
        {
            held.CapturedUtc = nowUtc; // same numbers: refresh the age in memory, no need to write the file
            return;
        }

        _config.RetainerGear[key] = new RetainerGearSnapshot
        {
            ItemLevel = stats.ItemLevel, Gathering = stats.Gathering, Perception = stats.Perception, CapturedUtc = nowUtc,
        };
        _save();
    }

    private List<RetainerPiece>? ReadWorn()
    {
        try
        {
            var manager = InventoryManager.Instance();
            var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerEquippedItems);
            if (container == null || !container->IsLoaded)
                return null;

            var pieces = new List<RetainerPiece>();
            for (var i = 0; i < container->Size && i <= (int)GearSlot.RingLeft; i++)
            {
                if (i == (int)GearSlot.Waist)
                    continue;

                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    continue;

                if (Piece(item, (GearSlot)i, -1, -1) is { } piece)
                    pieces.Add(piece);
            }

            return pieces;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[RetainerGear] worn gear unreadable");
            return null;
        }
    }

    private List<RetainerPiece> ReadCandidates(uint jobId, int level, HashSet<uint> gearsetIds)
    {
        var pieces = new List<RetainerPiece>();
        try
        {
            var manager = InventoryManager.Instance();
            var sheet = _data.GetExcelSheet<Item>();
            if (manager == null || sheet == null)
                return pieces;

            foreach (var type in CandidateContainers)
            {
                var container = manager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0 || gearsetIds.Contains(item->ItemId))
                        continue;

                    if (!sheet.TryGetRow(item->ItemId, out var row)
                        || row.LevelEquip > level
                        || !_gear.FitsJob(row.ClassJobCategory.RowId, jobId))
                        continue;

                    var slot = GearManager.SlotOf(row.EquipSlotCategory.Value);
                    if (slot is null or GearSlot.Waist)
                        continue;

                    if (Piece(item, slot.Value, (int)type, i) is { } piece)
                        pieces.Add(piece);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[RetainerGear] candidates unreadable");
        }

        return pieces;
    }

    private RetainerPiece? Piece(InventoryItem* item, GearSlot slot, int container, int sourceSlot)
    {
        var sheet = _data.GetExcelSheet<Item>();
        if (sheet == null || !sheet.TryGetRow(item->ItemId, out var row))
            return null;

        var hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        var twoHanded = slot == GearSlot.MainHand && row.EquipSlotCategory.Value.OffHand == -1;

        return new RetainerPiece(
            item->ItemId,
            row.Name.ExtractText(),
            hq,
            slot,
            (int)row.LevelItem.RowId,
            Stat(item, row, GatheringParam, hq, slot, twoHanded),
            Stat(item, row, PerceptionParam, hq, slot, twoHanded),
            twoHanded,
            row.IsUnique,
            container,
            sourceSlot);
    }

    /// <summary>ECommons' GetStat (MIT): base + HQ bonus + materia by grade, capped by item level and slot.</summary>
    private int Stat(InventoryItem* item, Item row, uint param, bool hq, GearSlot slot, bool twoHanded)
    {
        int baseValue = 0, hqBonus = 0;
        for (var i = 0; i < row.BaseParam.Count; i++)
        {
            if (row.BaseParam[i].RowId == param)
                baseValue += row.BaseParamValue[i];
        }

        for (var i = 0; i < row.BaseParamSpecial.Count; i++)
        {
            if (row.BaseParamSpecial[i].RowId == param)
                hqBonus += row.BaseParamValueSpecial[i];
        }

        var melds = new List<int>();
        var materiaSheet = _data.GetExcelSheet<Materia>();
        for (byte i = 0; materiaSheet != null && i < item->GetMateriaCount(); i++)
        {
            var grade = item->GetMateriaGrade(i);
            if (materiaSheet.TryGetRow(item->GetMateriaId(i), out var materia)
                && materia.BaseParam.RowId == param && grade < materia.Value.Count)
                melds.Add(materia.Value[grade]);
        }

        return RetainerGearMath.StatWithMateria(baseValue, hqBonus, hq, melds, Cap(row, param, slot, twoHanded));
    }

    /// <summary>The most of a stat this item may carry: the item level's allowance times the slot's share. 0 = unknown.</summary>
    private int Cap(Item row, uint param, GearSlot slot, bool twoHanded)
    {
        var level = row.LevelItem.ValueNullable;
        var baseParams = _data.GetExcelSheet<BaseParam>();
        if (level == null || baseParams == null || !baseParams.TryGetRow(param, out var share))
            return 0;

        var allowance = param == GatheringParam ? level.Value.Gathering : level.Value.Perception;
        var percent = slot switch
        {
            GearSlot.MainHand => twoHanded ? share.TwoHandWeaponPercent : share.OneHandWeaponPercent,
            GearSlot.OffHand => share.OffHandPercent,
            GearSlot.Head => share.HeadPercent,
            GearSlot.Body => share.ChestPercent,
            GearSlot.Hands => share.HandsPercent,
            GearSlot.Legs => share.LegsPercent,
            GearSlot.Feet => share.FeetPercent,
            GearSlot.Ears => share.EarringPercent,
            GearSlot.Neck => share.NecklacePercent,
            GearSlot.Wrists => share.BraceletPercent,
            GearSlot.RingRight or GearSlot.RingLeft => share.RingPercent,
            _ => (ushort)0,
        };

        return (int)(allowance * (float)percent / 1000f);
    }

    // --- The equip pass ---

    private void OpenGearWindow(DateTime nowUtc)
    {
        if (nowUtc - _waitingSinceUtc > WindowTimeout)
        {
            Stop("the gear window did not open — open the retainer's menu and try again");
            return;
        }

        if (ClickMenuEntry(GearEntryText()))
        {
            _trace.Record(DateTime.Now, "OpenGear");
            Status = $"opening {_equipping}'s gear…";
        }
        else
        {
            _trace.Note(DateTime.Now, "no-menu", "(waiting for the retainer's menu)");
        }
    }

    private void Submit(GearSwap swap, DateTime nowUtc)
    {
        var manager = InventoryManager.Instance();
        var source = manager == null ? null : manager->GetInventoryContainer((InventoryType)swap.Wear.Container);
        var item = source == null ? null : source->GetInventorySlot(swap.Wear.SourceSlot);

        // The plan was built a moment ago from this very slot; if it no longer holds the piece, replan.
        if (item == null || item->ItemId != swap.Wear.ItemId)
        {
            _trace.Record(DateTime.Now, $"Replan — {swap.Wear.Name} moved");
            _view = null;
            return;
        }

        _gameError = string.Empty;
        _inFlight = (swap, nowUtc + MoveTimeout); // before the call: an error line can arrive inside it
        var code = manager->MoveItemSlot((InventoryType)swap.Wear.Container, (ushort)swap.Wear.SourceSlot,
            InventoryType.RetainerEquippedItems, (ushort)swap.Slot, true);

        // A non-zero code has, every time it was seen here, meant "not now" — so do not sit out the full
        // timeout on it. It still decides nothing by itself: the slots do.
        if (code != 0)
            _inFlight = (swap, nowUtc + RefusedRetry);

        // The return code decides nothing (it has read 6 on moves that worked) — it is recorded as evidence.
        _trace.Record(DateTime.Now,
            $"Equip({swap.Slot}: {swap.Wear.Name}) from {(InventoryType)swap.Wear.Container}#{swap.Wear.SourceSlot} code={code}");
        Status = $"equipping {_equipping}: {swap.Wear.Name}…";
    }

    /// <summary>Delivery is the SLOT holding the piece — never the move call's return value.</summary>
    private void ConfirmMove(GearSwap swap, DateTime deadlineUtc, DateTime nowUtc)
    {
        var manager = InventoryManager.Instance();
        var worn = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerEquippedItems);
        var slot = worn == null ? null : worn->GetInventorySlot((int)swap.Slot);

        var source = manager == null ? null : manager->GetInventoryContainer((InventoryType)swap.Wear.Container);
        var from = source == null ? null : source->GetInventorySlot(swap.Wear.SourceSlot);

        // Two ways to know it landed. The worn slot holds the WHOLE piece (id, quality and stats — an HQ
        // or better-melded copy of the same item would otherwise read as delivered before it had moved);
        // or it holds that item AND the slot it came from no longer does, which covers a correct move
        // whose stats happen to read differently once worn.
        var now = slot == null || slot->ItemId == 0 ? null : Piece(slot, swap.Slot, -1, -1);
        var left = from == null || from->ItemId == 0 ? null : Piece(from, swap.Wear.Slot, -1, -1);
        if (Same(now, swap.Wear) || (now != null && now.ItemId == swap.Wear.ItemId && !Same(left, swap.Wear)))
        {
            _done++;
            _failures = 0;
            _inFlight = null;
            _view = null;
            _notBeforeUtc = nowUtc + SettleAfterMove;
            _trace.Record(DateTime.Now, $"Confirmed({swap.Slot})");
            return;
        }

        if (nowUtc < deadlineUtc)
            return;

        _inFlight = null;
        _view = null;
        _failures++;
        _notBeforeUtc = nowUtc + SettleAfterMove;

        // Say what IS there: "her slot still holds the old piece and ours never left the bag" is a refusal,
        // anything else is a read that needs fixing — and the two want different fixes.
        _trace.Record(DateTime.Now,
            $"NOT DELIVERED({swap.Slot}: {swap.Wear.Name}) — her slot holds {Show(now)}; the source slot holds {Show(left)}");
        if (_failures >= MaxFailures)
        {
            Stop(_gameError.Length > 0
                ? $"the game refused {swap.Wear.Name}: \"{_gameError}\""
                : $"the game did not equip {swap.Wear.Name} on {_equipping}");
        }
    }

    private static bool Same(RetainerPiece? a, RetainerPiece b) =>
        a != null && a.ItemId == b.ItemId && a.Hq == b.Hq && a.Gathering == b.Gathering && a.Perception == b.Perception;

    private static string Show(RetainerPiece? piece) =>
        piece == null ? "nothing" : $"{piece.Name}{(piece.Hq ? " HQ" : "")} (G {piece.Gathering} / P {piece.Perception})";

    private void CloseGearWindow(AtkUnitBase* window, bool windowUp)
    {
        if (!windowUp)
        {
            var name = _equipping;
            _equipping = null;
            _view = null;
            Status = $"{name}: {_done} piece(s) equipped";
            return;
        }

        // The window's own X first (what closing the retainer's bags proved safe); its close callback
        // (-1, the value the other plugins fire) if that does nothing.
        _closeAttempts++;
        if (_closeAttempts <= 2)
            window->Close(true);
        else if (_closeAttempts <= 4)
            window->FireCallbackInt(-1);
        else
        {
            var name = _equipping;
            _equipping = null;
            Status = $"{name}: {_done} piece(s) equipped — close the gear window yourself";
            return;
        }

        _trace.Record(DateTime.Now, "CloseGear");
    }

    // --- The retainer's menu ---

    private string GearEntryText()
    {
        if (_gearEntryRead)
            return _gearEntryText;

        _gearEntryRead = true;
        try
        {
            var sheet = _data.GetExcelSheet<Addon>();
            if (sheet != null && sheet.TryGetRow(AddonRowGear, out var row))
                _gearEntryText = row.Text.ExtractText();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[RetainerGear] addon row {0} unreadable", AddonRowGear);
        }

        return _gearEntryText;
    }

    /// <summary>
    /// Clicks the menu entry whose STEM matches (row 2389 is the same entry with a "(No main arm
    /// equipped)" suffix). Blank text matches nothing, so an unreadable sheet can only ever do nothing.
    /// </summary>
    private bool ClickMenuEntry(string wanted)
    {
        if (wanted.Length == 0)
            return false;

        var menu = (AddonSelectString*)_gameGui.GetAddonByName("SelectString").Address;
        if (menu == null || !menu->AtkUnitBase.IsVisible || !menu->AtkUnitBase.IsReady)
            return false;

        var count = menu->PopupMenu.PopupMenu.EntryCount;
        for (var i = 0; i < count; i++)
        {
            var ptr = (nint)menu->PopupMenu.PopupMenu.EntryNames[i].Value;
            if (ptr == 0 || !MenuEntryText.Matches(MemoryHelper.ReadSeStringNullTerminated(ptr).TextValue, wanted))
                continue;

            ((AtkUnitBase*)menu)->FireCallbackInt(i);
            return true;
        }

        return false;
    }
}
