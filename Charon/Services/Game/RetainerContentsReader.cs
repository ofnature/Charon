using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Charon.Features.Retainers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// The retainer contents store: what each retainer holds, as last SEEN, plus the two passes that use it —
/// a refresh (open each retainer at a bell so the store fills) and a fetch (bring items back out).
///
/// The client has no retainer inventory until that retainer's window has been opened, so this is a snapshot
/// by nature and says so everywhere: every answer carries the timestamp it rests on, and a retainer nobody
/// has opened is UNKNOWN rather than empty. Other plugins (Hephaestus's material sourcing) read it over IPC.
///
/// One move per tick, re-planned from the store every tick, and — the FC chest lesson — a move's return
/// code is NOT proof: each move is confirmed by re-reading its slot, and the pass stops after two silent
/// failures rather than reporting items that never arrived.
/// </summary>
public sealed unsafe class RetainerContentsReader
{
    private const string AddonSmall = "InventoryRetainer";
    private const string AddonLarge = "InventoryRetainerLarge";

    private static readonly TimeSpan ActionPacing = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MoveSettle = TimeSpan.FromSeconds(2);

    /// <summary>How often an OPEN retainer is re-read: the player may be moving things around in it.</summary>
    private static readonly TimeSpan ReReadWhileOpen = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The containers that make up the store. The market is deliberately NOT here: items listed for sale
    /// are not available to hand over, so counting them would answer a question nobody asked.
    /// </summary>
    private static readonly InventoryType[] Store =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    ];

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly RetainerReader _retainers;
    private readonly CharonConfig _config;
    private readonly Action _save;
    private readonly Func<ulong> _contentId;
    private readonly IPluginLog _log;

    private string? _openKey;
    private DateTime _lastCaptureUtc = DateTime.MinValue;

    private readonly HashSet<string> _refreshSeen = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshArmed;

    private bool _fetchArmed;
    private uint _itemId;
    private int _wanted;
    private bool _hqOnly;
    private string? _plannedRetainer;
    private int _movedNq;
    private int _movedHq;
    private DateTime _lastActionUtc = DateTime.MinValue;

    private FetchDecision? _pendingMove;
    private int _failedMoves;

    public RetainerContentsReader(
        IGameGui gameGui,
        IObjectTable objectTable,
        ICondition condition,
        RetainerReader retainers,
        CharonConfig config,
        Action save,
        Func<ulong> contentId,
        IPluginLog log)
    {
        _gameGui = gameGui;
        _objectTable = objectTable;
        _condition = condition;
        _retainers = retainers;
        _config = config;
        _save = save;
        _contentId = contentId;
        _log = log;
    }

    public string Status { get; private set; } = "idle";

    /// <summary>The last completed pass's outcome — what actually happened, not what was asked for.</summary>
    public string LastResult { get; private set; } = "nothing fetched yet";

    public bool FetchBusy => _fetchArmed;

    public bool RefreshBusy => _refreshArmed;

    public bool Busy => _fetchArmed || _refreshArmed;

    /// <summary>The store in the pure layer's shape, for planning and for IPC answers.</summary>
    public IReadOnlyList<RetainerBag> Bags()
    {
        var bags = new List<RetainerBag>();
        foreach (var (key, snapshot) in _config.RetainerContents)
        {
            var name = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;
            bags.Add(new RetainerBag(
                key,
                name,
                snapshot.CapturedUtc,
                snapshot.Stacks.Select(s => new RetainerStackCount(s.ItemId, s.Qty, s.Hq)).ToList()));
        }

        return bags;
    }

    public void Update(DateTime nowUtc)
    {
        CaptureIfOpen(nowUtc);

        if (_refreshArmed)
            UpdateRefresh();

        if (_fetchArmed)
            UpdateFetch(nowUtc);
    }

    // ------------------------------------------------------------------ capture ---

    /// <summary>
    /// Snapshot whichever retainer's inventory is open. Called every tick; it reads when the window is new
    /// and then at most every 20s while it stays up, so a player shuffling items in it keeps the store right
    /// without a read per frame.
    /// </summary>
    private void CaptureIfOpen(DateTime nowUtc)
    {
        var open = OpenRetainerName();
        if (open == null)
        {
            _openKey = null;
            return;
        }

        var isNew = !string.Equals(_openKey, open, StringComparison.OrdinalIgnoreCase);
        if (!isNew && nowUtc - _lastCaptureUtc < ReReadWhileOpen)
            return;

        if (!Capture(open, nowUtc))
            return;

        _openKey = open;
        _lastCaptureUtc = nowUtc;
        _refreshSeen.Add(Key(open));
    }

    private bool Capture(string retainer, DateTime nowUtc)
    {
        var stacks = new List<CharonConfig.RetainerStack>();
        var pages = 0;

        foreach (var type in Store)
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            pages++;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                    continue;

                stacks.Add(new CharonConfig.RetainerStack
                {
                    ItemId = item->ItemId,
                    Qty = item->Quantity,
                    Hq = (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
                });
            }
        }

        // Not one container loaded: the window is up but the client has not filled anything yet. Recording
        // that as an empty retainer would be a lie a caller cannot detect.
        if (pages == 0)
        {
            Status = $"{retainer}: its inventory is not loaded yet — nothing recorded";
            return false;
        }

        var gil = _retainers.Read(nowUtc)
            .FirstOrDefault(r => string.Equals(r.Name, retainer, StringComparison.OrdinalIgnoreCase))?.Gil ?? 0;

        _config.RetainerContents[Key(retainer)] = new CharonConfig.RetainerSnapshot
        {
            CapturedUtc = nowUtc,
            Gil = (int)Math.Min(int.MaxValue, gil),
            Stacks = stacks,
        };
        _save();

        Status = $"captured {retainer}: {stacks.Count} stack(s) from {pages} page(s)";
        return true;
    }

    // ------------------------------------------------------------------ refresh ---

    /// <summary>
    /// Arm a pass over every retainer on this character. It captures each one as its window opens — Charon
    /// does not select retainers (the list's selection is not a mechanism this repo has ever verified), so
    /// the pass reports which retainer to open next and finishes when they are all in.
    /// </summary>
    public bool ArmRefresh()
    {
        var now = DateTime.UtcNow;
        var all = _retainers.Read(now).Select(r => (Key(r.Name), r.Name)).ToList();
        if (all.Count == 0)
        {
            Status = "no retainers to refresh on this character";
            return false;
        }

        var plan = RetainerContents.PlanRefresh(Bags(), all, now);
        if (plan.Count == 0)
        {
            Status = $"nothing to refresh — all {all.Count} retainer(s) are current";
            return false;
        }

        _refreshArmed = true;
        _refreshSeen.Clear();
        Status = $"refresh 0/{all.Count} — open {plan[0].Name} at a bell ({plan[0].Reason})";
        return true;
    }

    private void UpdateRefresh()
    {
        var now = DateTime.UtcNow;
        var names = _retainers.Read(now).Select(r => r.Name).ToList();
        if (names.Count == 0)
        {
            _refreshArmed = false;
            Status = "refresh stopped — no retainers on this character";
            return;
        }

        var done = names.Count(n => _refreshSeen.Contains(Key(n)));
        if (done >= names.Count)
        {
            _refreshArmed = false;
            Status = $"refresh complete — {done} retainer(s) captured";
            LastResult = Status;
            return;
        }

        var next = names.First(n => !_refreshSeen.Contains(Key(n)));
        Status = $"refresh {done}/{names.Count} — open {next} at a bell";
    }

    // -------------------------------------------------------------------- fetch ---

    /// <summary>Ask for items back. The store decides whether it is possible; the pass does the rest.</summary>
    public bool ArmFetch(uint itemId, int quantity, bool highQuality)
    {
        var plan = RetainerContents.PlanFetch(Bags(), itemId, quantity, highQuality);
        if (!plan.Possible)
        {
            Status = $"fetch refused — {plan.Refusal}";
            LastResult = Status;
            _log.Debug("[Retainers] fetch refused: {0}", plan.Refusal ?? "unknown");
            return false;
        }

        _fetchArmed = true;
        _itemId = itemId;
        _wanted = quantity;
        _hqOnly = highQuality;
        _plannedRetainer = plan.Retainer;
        _movedNq = 0;
        _movedHq = 0;
        _failedMoves = 0;
        _pendingMove = null;
        _lastActionUtc = DateTime.MinValue;
        Status = $"fetch armed — open {plan.Retainer} at a bell";
        return true;
    }

    public void Stop(string reason = "stopped")
    {
        _fetchArmed = false;
        _refreshArmed = false;
        _pendingMove = null;
        Status = reason;
    }

    private void UpdateFetch(DateTime nowUtc)
    {
        var open = OpenRetainerName();

        // Confirm the previous move before deciding the next one: MoveItemSlot's return code is not proof,
        // the observed change is (the FC chest lesson).
        if (_pendingMove is { } pending)
        {
            if (SlotStillHolds(pending) && nowUtc - _lastActionUtc < MoveSettle)
                return; // give the game its moment before calling it failed

            var landed = !SlotStillHolds(pending);
            _pendingMove = null;

            if (landed)
            {
                if (pending.Hq)
                    _movedHq += pending.Quantity;
                else
                    _movedNq += pending.Quantity;

                _failedMoves = 0;
                if (open != null)
                    Capture(open, nowUtc); // the retainer just changed: keep the store honest
            }
            else
            {
                _failedMoves++;
                if (_failedMoves >= 2)
                {
                    Finish($"the move did not land — {pending.Quantity} stayed put; the retainer window is not "
                        + "taking MoveItemSlot");
                    return;
                }
            }
        }

        var remaining = _hqOnly ? _wanted - _movedHq : _wanted - (_movedNq + _movedHq);
        if (remaining <= 0)
        {
            Finish($"fetched {_movedNq} normal and {_movedHq} high-quality");
            return;
        }

        // Re-planned from the store every tick, so a retainer emptied by our own moves (or by the player) is
        // reflected immediately instead of us walking into a slot that is not there any more.
        var plan = RetainerContents.PlanFetch(Bags(), _itemId, remaining, _hqOnly);
        if (!plan.Possible)
        {
            Finish(plan.Refusal!);
            return;
        }

        _plannedRetainer = plan.Retainer;

        var decision = RetainerFetchStep.Decide(
            _fetchArmed, open, _plannedRetainer, _itemId, remaining, _hqOnly, 0, 0, ReadSlots(), FreePlayerSlots(), null);

        Status = decision.Reason;

        switch (decision.Action)
        {
            case FetchAction.Done:
                Finish(decision.Reason);
                break;

            case FetchAction.Abort:
                _log.Debug("[Retainers] fetch aborted: {0}", decision.Reason);
                Finish(decision.Reason);
                break;

            case FetchAction.Move when nowUtc - _lastActionUtc >= ActionPacing:
                AttemptMove(decision, nowUtc);
                break;
        }
    }

    private void AttemptMove(FetchDecision decision, DateTime nowUtc)
    {
        // Re-read the exact slot immediately before touching it: the decision came from a read one tick old,
        // and moving "whatever is in that slot" is how a player ends up with the wrong items.
        var slot = ReadSlot(decision.Container, decision.Slot);
        if (slot == null || slot.Value.ItemId != _itemId || slot.Value.Quantity != decision.Quantity)
        {
            Finish("the slot changed between reading it and moving it — nothing was taken");
            return;
        }

        var destination = FirstFreeSlot();
        if (destination == null)
        {
            Finish("no free bag slot for what would come back");
            return;
        }

        var (dstContainer, dstSlot) = destination.Value;

        var rc = InventoryManager.Instance()->MoveItemSlot(
            (InventoryType)decision.Container, (ushort)decision.Slot,
            dstContainer, (ushort)dstSlot, true);

        _log.Debug("[Retainers] retrieve {0} x{1}: {2}:{3} -> {4}:{5} (rc {6})",
            _itemId, decision.Quantity, (InventoryType)decision.Container, decision.Slot, dstContainer, dstSlot, rc);

        _pendingMove = decision;
        _lastActionUtc = nowUtc;
    }

    private void Finish(string reason)
    {
        _fetchArmed = false;
        _pendingMove = null;
        Status = reason;
        LastResult = reason;
    }

    // ------------------------------------------------------------------- reads ---

    /// <summary>Which retainer's inventory is open — the bell flag plus the nearest retainer object.</summary>
    private string? OpenRetainerName()
    {
        if (!_condition[ConditionFlag.OccupiedSummoningBell])
            return null;

        if (!IsVisible(AddonSmall) && !IsVisible(AddonLarge))
            return null;

        var player = _objectTable.LocalPlayer;
        if (player == null)
            return null;

        return _objectTable
            .Where(o => o.ObjectKind == ObjectKind.Retainer)
            .OrderBy(o => (o.Position - player.Position).LengthSquared())
            .Select(o => o.Name.TextValue)
            .FirstOrDefault(n => n.Length > 0);
    }

    private bool IsVisible(string addon)
    {
        var unit = (AtkUnitBase*)_gameGui.GetAddonByName(addon).Address;
        return unit != null && unit->IsVisible;
    }

    /// <summary>Every stack in every loaded retainer container, in the shape the pure step wants.</summary>
    private List<FetchSlot> ReadSlots()
    {
        var slots = new List<FetchSlot>();
        foreach (var type in Store)
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                    continue;

                slots.Add(new FetchSlot((int)type, i, item->ItemId, item->Quantity,
                    (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0));
            }
        }

        return slots;
    }

    private (uint ItemId, int Quantity)? ReadSlot(int container, int slot)
    {
        var page = InventoryManager.Instance()->GetInventoryContainer((InventoryType)container);
        if (page == null || !page->IsLoaded || slot >= page->Size)
            return null;

        var item = page->GetInventorySlot(slot);
        return item == null || item->ItemId == 0 ? null : (item->ItemId, item->Quantity);
    }

    private bool SlotStillHolds(FetchDecision decision)
    {
        var slot = ReadSlot(decision.Container, decision.Slot);

        // Empty or something else in that slot means our stack left it — which is the only success signal
        // that means anything here.
        return slot is { } value && value.ItemId == _itemId;
    }

    private int FreePlayerSlots()
    {
        var free = 0;
        foreach (var type in PlayerBags)
        {
            var bag = InventoryManager.Instance()->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded)
                continue;

            for (var i = 0; i < bag->Size; i++)
            {
                var item = bag->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    free++;
            }
        }

        return free;
    }

    private (InventoryType Container, int Slot)? FirstFreeSlot()
    {
        foreach (var type in PlayerBags)
        {
            var bag = InventoryManager.Instance()->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded)
                continue;

            for (var i = 0; i < bag->Size; i++)
            {
                var item = bag->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    return (type, i);
            }
        }

        return null;
    }

    // --------------------------------------------------------------------- IPC ---

    /// <summary>
    /// The store as JSON for other plugins: every known retainer with its stacks (HQ its own flag), when it
    /// was captured, and — separately — the retainers nobody has opened yet, because a caller that reads
    /// unknown as empty will give up on materials it actually has.
    /// </summary>
    public string ContentsJson()
    {
        var now = DateTime.UtcNow;

        var known = _config.RetainerContents
            .Select(kv => new
            {
                retainer = kv.Key,
                capturedUtc = kv.Value.CapturedUtc,
                ageMinutes = Math.Round((now - kv.Value.CapturedUtc).TotalMinutes, 1),
                gil = kv.Value.Gil,
                stacks = kv.Value.Stacks.Select(s => new { itemId = s.ItemId, qty = s.Qty, hq = s.Hq }).ToList(),
                nqTotal = kv.Value.Stacks.Where(s => !s.Hq).Sum(s => s.Qty),
                hqTotal = kv.Value.Stacks.Where(s => s.Hq).Sum(s => s.Qty),
            })
            .OrderBy(x => x.retainer, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unknown = _retainers.Read(now)
            .Select(r => Key(r.Name))
            .Where(k => !_config.RetainerContents.ContainsKey(k))
            .ToList();

        return JsonSerializer.Serialize(new { known, unknown, status = Status });
    }

    /// <summary>"Does anyone hold this, and how much of each quality?" — the question crafting asks.</summary>
    public string ItemJson(uint itemId)
    {
        var now = DateTime.UtcNow;
        var bags = Bags();
        var holdings = RetainerContents.Find(bags, itemId);
        var (nq, hq) = RetainerContents.Total(bags, itemId);

        var unknown = _retainers.Read(now)
            .Select(r => r.Name)
            .Where(n => !bags.Any(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return JsonSerializer.Serialize(new
        {
            itemId,
            nq,
            hq,
            holdings = holdings.Select(h => new
            {
                retainer = h.Retainer,
                nq = h.Nq,
                hq = h.Hq,
                capturedUtc = h.CapturedUtc,
                ageMinutes = Math.Round((now - h.CapturedUtc).TotalMinutes, 1),
            }),
            unknown,
        });
    }

    private string Key(string retainer) => $"{_contentId()}:{retainer}";
}
