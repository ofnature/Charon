using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Charon.Features.FcChest;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>One executed chest move, for the section's log.</summary>
public sealed record ChestLogEntry(string Name, int Quantity, string Verb);

/// <summary>One item aggregated across its stacks on a chest page, for the contents table.</summary>
public sealed record ChestContentRow(uint ItemId, string Name, int TotalQuantity, int StackCount);

/// <summary>
/// FC chest entrust/withdraw execution for ONE page at a time. Thin unsafe adapter around
/// InventoryManager; planning is <see cref="FcChestPlanner"/> (pure). Manual trigger only.
///
/// Gates: the FC chest window must be OPEN (that is the game's transfer session — proximity
/// alone is not enough, and you cannot open it without being at the chest), and the selected
/// page's container must be loaded (a page loads when its tab is first viewed).
/// Moves are paced one per 250ms tick to stay server-friendly; each move re-checks the gate
/// so closing the chest mid-run aborts cleanly.
/// </summary>
public sealed unsafe class FcChestManager
{
    private const string ChestAddonName = "FreeCompanyChest";
    private static readonly TimeSpan MovePacing = TimeSpan.FromMilliseconds(250);

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private readonly IChatGui _chatGui;
    private string _lastGameError = string.Empty;
    private DateTime _lastGameErrorUtc = DateTime.MinValue;
    private int _qtyConsecutiveFailures;

    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;
    private readonly InventoryQuantityMover _mover;
    private readonly IPluginLog _log;

    // --- Quantity-move pipeline (withdraw-exact / deposit-all) -------------------------------
    // Runs through the native quantity move (InventoryQuantityMover); whole-stack-to-empty
    // moves fall back to MoveItemSlot so a broken sig only removes partial-move capability.
    private readonly Queue<PlannedMove> _qtyQueue = new();
    private PlannedMove? _qtyInFlight;
    private int _qtyDstBefore;
    private int _qtySrcBefore;
    private DateTime _qtyDeadlineUtc;
    private DateTime _qtySubmittedUtc;
    private DateTime _qtyLastSubmitUtc = DateTime.MinValue;
    private DateTime _qtySettleUntilUtc = DateTime.MinValue;
    private bool _qtySwitchFired;
    private DateTime _qtySwitchDeadlineUtc;

    /// <summary>FCCH's production pacing: 700ms between chest moves (250ms proved too hot).</summary>
    private static readonly TimeSpan QtyMovePacing = TimeSpan.FromMilliseconds(700);

    /// <summary>FCCH's production settle: 2s after a tab switch before the next move.</summary>
    private static readonly TimeSpan TabSettle = TimeSpan.FromSeconds(2);
    private int _qtySucceeded;
    private int _qtyTotal;
    private string _qtyVerb = "Moved";

    // --- Tab walker: loads unviewed chest pages by simulating the tab click ------------------
    private int _walkTarget = -1;
    private bool _walkFired;
    private DateTime _walkDeadlineUtc;
    private bool _depositAfterWalk;

    private readonly Queue<ChestMove> _pending = new();
    private readonly List<ChestLogEntry> _operationLog = new();
    private InventoryType _moveDestination;
    private InventoryType _activePage;
    private bool _withdrawing;
    private DateTime _lastMoveUtc = DateTime.MinValue;

    /// <summary>Move submitted last tick, awaiting verification (the source slot emptying is the
    /// ONLY reliable success signal — MoveItemSlot's return code is not: 6 came back on a move
    /// that demonstrably succeeded, verified in testing).</summary>
    private ChestMove? _inFlight;
    private DateTime _inFlightDeadlineUtc;
    private int _succeeded;
    private int _movesVerified;

    /// <summary>
    /// Unit-accurate withdraw is a ROUNDTRIP: SplitItem is a dead end on FC chest containers
    /// (it neither splits nor opens the quantity dialog — verified in testing), so instead we
    /// withdraw EVERY stack of the item, split 1 unit off in the player's own bags (where
    /// SplitItem works normally), and move that single unit back to the page as the seed.
    /// Every primitive in this flow is one that demonstrably works: chest↔bag MoveItemSlot
    /// and an own-inventory split.
    /// </summary>
    private sealed record SeedReturnOp(int Page, uint ItemId, string Name);

    private enum ReturnPhase
    {
        /// <summary>Find (or split off) a 1-unit stack of the item in the bags.</summary>
        EnsureUnitStack,

        /// <summary>Wait for the bag split to land.</summary>
        VerifyUnitStack,

        /// <summary>Move the 1-unit stack back to an empty slot on the page.</summary>
        MoveBack,

        /// <summary>Wait for the seed to show up on the page.</summary>
        VerifyMoveBack,
    }

    private SeedReturnOp? _seedReturn;
    private ReturnPhase _returnPhase;
    private DateTime _returnDeadlineUtc;

    // Contents cache for the UI table — re-reading containers every draw is wasteful.
    private List<ChestContentRow>? _contentsCache;
    private int _contentsCachePage;
    private DateTime _contentsCacheUtc = DateTime.MinValue;

    public FcChestManager(IGameGui gameGui, IDataManager dataManager, InventoryQuantityMover mover,
        IChatGui chatGui, IPluginLog log)
    {
        _gameGui = gameGui;
        _dataManager = dataManager;
        _mover = mover;
        _chatGui = chatGui;
        _log = log;
        _chatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose() => _chatGui.ChatMessage -= OnChatMessage;

    /// <summary>
    /// The game's refusal channel: chest writes that the server rejects raise an ErrorMessage
    /// toast ("Unable to store item. Another player is using the chest.", stack full, …) and
    /// then fail SILENTLY on every further attempt. Any error toast while one of our moves is
    /// in flight is treated as the verdict on that move — the run aborts with the game's own
    /// words instead of grinding the rest of the queue through delivery timeouts (learned the
    /// hard way: 93 queued moves, one toast, four minutes of silent failures).
    /// </summary>
    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (message.LogKind != Dalamud.Game.Text.XivChatType.ErrorMessage)
            return;
        if (_qtyInFlight == null && _inFlight == null)
            return;

        _lastGameErrorUtc = DateTime.UtcNow;
        _lastGameError = message.Message.TextValue;
    }

    public string Status { get; private set; } = "idle";

    /// <summary>Summary of the last completed operation ("Entrusted 12 stacks to Page 1").</summary>
    public string LastOperation { get; private set; } = "";

    /// <summary>Per-item results of the last operation, newest run only.</summary>
    public IReadOnlyList<ChestLogEntry> OperationLog => _operationLog;

    public bool Busy => _pending.Count > 0 || _inFlight != null || _seedReturn != null
                        || _qtyQueue.Count > 0 || _qtyInFlight != null || _walkTarget > 0;

    /// <summary>The native quantity move resolved — exact-amount withdrawals are possible.</summary>
    public bool QuantityMovesAvailable => _mover.Available;

    /// <summary>Aggregated contents of the page for the UI table (cached ~500ms). Empty when unloaded.</summary>
    public IReadOnlyList<ChestContentRow> GetPageContents(int page)
    {
        if (_contentsCache != null && _contentsCachePage == page
            && DateTime.UtcNow - _contentsCacheUtc < TimeSpan.FromMilliseconds(500))
            return _contentsCache;

        _contentsCache = ReadPage(page)
            .GroupBy(s => s.ItemId)
            .Select(g => new ChestContentRow(g.Key, g.First().Name, g.Sum(s => s.Quantity), g.Count()))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _contentsCachePage = page;
        _contentsCacheUtc = DateTime.UtcNow;
        return _contentsCache;
    }

    /// <summary>
    /// Withdraw all but QUANTITY 1 of one item (the contents table's per-row button): the seed
    /// stack is split so a single unit remains, then every other stack is withdrawn.
    /// </summary>
    public int StartWithdrawItem(int page, uint itemId)
    {
        if (Busy || !FcChestPlanner.CanExecute(IsChestOpen(), IsPageLoaded(page)))
            return 0;

        var stacks = ReadPage(page).Where(s => s.ItemId == itemId).ToList();
        var total = stacks.Sum(s => s.Quantity);
        if (stacks.Count == 0 || total <= 1)
        {
            LastOperation = "Withdraw: nothing to take — only the seed unit remains";
            OperationJustFinished = true;
            return 0;
        }

        _operationLog.Clear();
        _succeeded = 0;
        _movesVerified = 0;
        _inFlight = null;
        _withdrawing = true;
        _activePage = PageType(page);
        _moveDestination = InventoryType.Inventory1;

        // Withdraw EVERYTHING, then return exactly 1 unit as the seed (see SeedReturnOp).
        foreach (var stack in stacks)
            _pending.Enqueue(new ChestMove(stack.ItemId, stack.Name, stack.Quantity, stack.Container, stack.Slot));

        _seedReturn = new SeedReturnOp(page, itemId, stacks[0].Name);
        _returnPhase = ReturnPhase.EnsureUnitStack;
        Status = $"queued {_pending.Count} moves";
        return _pending.Count;
    }

    /// <summary>True when an operation just finished (UI expands the log once, then clears this).</summary>
    public bool OperationJustFinished { get; set; }

    /// <summary>The transfer gate: FC chest window open (implies proximity) — buttons disable on this.</summary>
    public bool IsChestOpen()
    {
        try
        {
            var addon = _gameGui.GetAddonByName(ChestAddonName);
            return !addon.IsNull && addon.IsVisible;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the page's container data has arrived (its tab was viewed this session).</summary>
    public bool IsPageLoaded(int page)
    {
        try
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(PageType(page));
            return container != null && container->IsLoaded;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Entrust inventory stacks of items already seeded on the page. Returns moves queued.</summary>
    public int StartEntrust(int page)
    {
        // The button is disabled when this gate fails, but the confirm modal can outlive it — walk
        // away from the chest with the dialog open and Confirm would otherwise do nothing silently.
        if (Busy || !FcChestPlanner.CanExecute(IsChestOpen(), IsPageLoaded(page)))
        {
            LastOperation = Busy
                ? "Entrust: another operation is still running"
                : !IsChestOpen()
                    ? "Entrust: the FC chest isn't open"
                    : $"Entrust: Page {page} hasn't loaded — view that tab in the chest once";
            OperationJustFinished = true;
            return 0;
        }

        var moves = FcChestPlanner.PlanEntrust(ReadBags(), ReadPage(page));
        BeginOperation(moves, PageType(page), withdrawing: false, page);
        return moves.Count;
    }

    /// <summary>Withdraw all but the last stack of each item on the page. Returns moves queued.</summary>
    public int StartWithdraw(int page)
    {
        if (Busy || !FcChestPlanner.CanExecute(IsChestOpen(), IsPageLoaded(page)))
        {
            LastOperation = Busy
                ? "Withdraw: another operation is still running"
                : !IsChestOpen()
                    ? "Withdraw: the FC chest isn't open"
                    : $"Withdraw: Page {page} hasn't loaded — view that tab in the chest once";
            OperationJustFinished = true;
            return 0;
        }

        var moves = FcChestPlanner.PlanWithdraw(ReadPage(page));
        BeginOperation(moves, PageType(page), withdrawing: true, page);
        return moves.Count;
    }

    // --- Quantity-accurate operations (built on the native quantity move) ---------------------

    /// <summary>
    /// Withdraw exactly <paramref name="amount"/> units of one item from the page into the bags.
    /// Needs the quantity native (<see cref="QuantityMovesAvailable"/>); merges into existing bag
    /// stacks first, NQ stacks are drawn before HQ.
    /// </summary>
    public int StartWithdrawAmount(int page, uint itemId, int amount)
    {
        if (Busy || amount <= 0 || !_mover.Available
            || !FcChestPlanner.CanExecute(IsChestOpen(), IsPageLoaded(page)))
        {
            LastOperation = !_mover.Available
                ? "Withdraw: quantity moves unavailable (signature not resolved)"
                : Busy ? "Withdraw: another operation is still running" : "Withdraw: chest/page not ready";
            OperationJustFinished = true;
            return 0;
        }

        var moves = new List<PlannedMove>();
        var empties = FreeSlots(PlayerBags);

        // Per quality bucket: merging NQ into an HQ stack (or vice versa) is refused by the game,
        // so each bucket only sees partial bag stacks of its own quality. NQ drains first.
        var remaining = amount;
        foreach (var hq in new[] { false, true })
        {
            if (remaining <= 0)
                break;

            var chestStacks = ReadInvStacks(PageType(page)).Where(s => s.ItemId == itemId && s.Hq == hq).ToList();
            if (chestStacks.Count == 0)
                continue;

            var bagPartials = PlayerBags.SelectMany(ReadInvStacks)
                .Where(s => s.ItemId == itemId && s.Hq == hq && s.Quantity < s.MaxStack).ToList();
            var planned = QuantityMovePlanner.PlanWithdraw(chestStacks, bagPartials, empties, remaining);
            moves.AddRange(planned);
            remaining -= QuantityMovePlanner.TotalUnits(planned);

            var used = planned.Where(m => empties.Any(e => e.Container == m.DstContainer && e.Slot == m.DstSlot))
                .Select(m => (m.DstContainer, m.DstSlot)).ToHashSet();
            empties = empties.Where(e => !used.Contains((e.Container, e.Slot))).ToList();
        }

        if (moves.Count == 0)
        {
            LastOperation = "Withdraw: no room in bags (or nothing to take)";
            OperationJustFinished = true;
            return 0;
        }

        BeginQuantityOperation(moves, "Withdrew");
        return moves.Count;
    }

    /// <summary>
    /// Deposit every tradeable bag stack into the chest (crystals and gil excluded — they have
    /// their own containers). Unviewed tabs are loaded first by simulating their tab click; the
    /// deposit plan runs once every page can answer. Partial-stack top-ups need the quantity
    /// native; without it only whole-stack-to-empty moves are planned.
    /// </summary>
    public bool StartDepositAll()
    {
        if (Busy || !IsChestOpen())
        {
            LastOperation = Busy ? "Deposit all: another operation is still running" : "Deposit all: the FC chest isn't open";
            OperationJustFinished = true;
            return false;
        }

        var unloaded = FirstUnloadedPage();
        if (unloaded > 0)
        {
            _walkTarget = unloaded;
            _walkFired = false;
            _walkDeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            _depositAfterWalk = true;
            Status = $"loading chest tab {unloaded}…";
            return true;
        }

        return PlanAndQueueDepositAll();
    }

    private bool PlanAndQueueDepositAll()
    {
        // Duplicates-only doctrine, same as the per-page entrust: only items the chest ALREADY
        // holds move in — the chest's contents are the shopping list, never seeds of new items.
        var chestStacks = new List<InvStack>();
        var empties = new List<(int Container, short Slot)>();
        for (var page = 1; page <= 5; page++)
        {
            if (!IsPageLoaded(page))
                continue;
            chestStacks.AddRange(ReadInvStacks(PageType(page)));
            empties.AddRange(FreeSlots([PageType(page)]));
        }

        var chestItemIds = chestStacks.Select(c => c.ItemId).ToHashSet();
        var chestPartials = chestStacks.Where(c => c.Quantity < c.MaxStack).ToList();

        var skippedUntradeable = 0;
        var skippedNotInChest = 0;
        var bagStacks = new List<InvStack>();
        foreach (var stack in PlayerBags.SelectMany(ReadInvStacks))
        {
            // Gil (1) and crystals (2-19) live in their own chest containers; untradeables the
            // chest refuses outright (LogMessage 1866) — skip rather than collect refusals.
            if (stack.ItemId <= 19)
                continue;
            if (!chestItemIds.Contains(stack.ItemId))
            {
                skippedNotInChest++;
                continue;
            }

            if (IsUntradable(stack.ItemId))
            {
                skippedUntradeable++;
                continue;
            }

            bagStacks.Add(stack);
        }

        var moves = QuantityMovePlanner.PlanDepositAll(bagStacks, chestPartials, empties, _mover.Available);
        if (moves.Count == 0)
        {
            LastOperation = $"Deposit all: nothing to deposit ({skippedNotInChest} stacks not in the chest, "
                            + $"{skippedUntradeable} untradeable — both stay put)";
            OperationJustFinished = true;
            return false;
        }

        BeginQuantityOperation(moves, "Deposited");
        if (skippedNotInChest > 0)
            _operationLog.Add(new ChestLogEntry($"{skippedNotInChest} stacks of items not in the chest", 0, "skipped"));
        if (skippedUntradeable > 0)
            _operationLog.Add(new ChestLogEntry($"{skippedUntradeable} untradeable stacks", 0, "skipped"));
        return true;
    }

    /// <summary>Walks unviewed tabs by firing the addon's own tab-click callback (two ints: 0, page index 0-4).</summary>
    private void DriveTabWalk(DateTime nowUtc)
    {
        if (IsPageLoaded(_walkTarget))
        {
            _walkTarget = FirstUnloadedPage();
            _walkFired = false;
            _walkDeadlineUtc = nowUtc + TimeSpan.FromSeconds(4);
            if (_walkTarget > 0)
            {
                Status = $"loading chest tab {_walkTarget}…";
                return;
            }

            if (_depositAfterWalk)
            {
                _depositAfterWalk = false;
                if (PlanAndQueueDepositAll())
                    _qtySettleUntilUtc = nowUtc + TabSettle; // just walked tabs — let the session settle
            }

            return;
        }

        if (!_walkFired)
        {
            var addon = _gameGui.GetAddonByName(ChestAddonName);
            if (addon.IsNull)
                return;

            FcChestUi.SwitchToPage((AtkUnitBase*)addon.Address, _walkTarget);
            _walkFired = true;
            return;
        }

        if (nowUtc > _walkDeadlineUtc)
        {
            _walkTarget = -1;
            _depositAfterWalk = false;
            LastOperation = $"Deposit all: tab {FirstUnloadedPage()} would not load — view it in the chest once and retry";
            Status = "tab walk timed out";
            OperationJustFinished = true;
        }
    }

    private void BeginQuantityOperation(IReadOnlyList<PlannedMove> moves, string verb)
    {
        _operationLog.Clear();
        _qtyQueue.Clear();
        // Grouped by chest page: the game's own UI can only ever touch the DISPLAYED tab, so
        // the executor switches the real tab to each group's page — grouping minimizes switches.
        foreach (var move in moves.OrderBy(ChestPageOf))
            _qtyQueue.Enqueue(move);
        _qtyInFlight = null;
        _qtySucceeded = 0;
        _qtyTotal = moves.Count;
        _qtyVerb = verb;
        _qtyConsecutiveFailures = 0;
        _lastGameError = string.Empty;
        _lastGameErrorUtc = DateTime.MinValue;
        _qtySwitchFired = false;
        _qtySettleUntilUtc = DateTime.MinValue;
        _qtyLastSubmitUtc = DateTime.MinValue;
        Status = $"queued {moves.Count} moves";
    }

    private void DriveQuantityQueue(DateTime nowUtc)
    {
        if (_qtyInFlight != null)
        {
            if (!TryVerifyQuantityMove(nowUtc))
                return;

            _contentsCache = null;
            if (_qtyQueue.Count > 0)
                return; // next tick submits
        }

        if (_qtyQueue.Count == 0)
        {
            // _qtyTotal is zeroed by AbortQuantityRun — never overwrite its message.
            if (_qtyInFlight == null && _qtyTotal > 0)
                FinishQuantityOperation();
            return;
        }

        // The game's own pending-operation ring: never stack a move on one still settling.
        if (InventoryQuantityMover.HasPendingOperation())
            return;

        if (nowUtc < _qtySettleUntilUtc || nowUtc - _qtyLastSubmitUtc < QtyMovePacing)
            return;

        // The chest only reliably accepts moves for the DISPLAYED tab (the game's own UI can do
        // nothing else) — switch the real tab to the next move's page and let it settle first.
        var nextPage = ChestPageOf(_qtyQueue.Peek());
        if (nextPage >= 1 && !EnsureDisplayedPage(nextPage, nowUtc))
            return;

        var move = _qtyQueue.Dequeue();
        var src = ReadSlot(move.SrcContainer, move.SrcSlot);
        if (src == null || src.Value.ItemId != move.ItemId || src.Value.Quantity < move.Quantity)
        {
            _operationLog.Add(new ChestLogEntry(ItemName(move.ItemId), move.Quantity, "FAILED — source changed"));
            return;
        }

        var dst = ReadSlot(move.DstContainer, move.DstSlot);
        _qtySrcBefore = src.Value.Quantity;
        _qtyDstBefore = dst?.Quantity ?? 0;

        // FCCH parity: EVERY chest transfer goes through the quantity native when it resolved
        // (their MoveItemSlot use is same-tab sort swaps only); MoveItemSlot is the fallback for
        // whole-stack moves when the sig is dead.
        var ok = _mover.Available
            ? _mover.Move((InventoryType)move.SrcContainer, (ushort)move.SrcSlot,
                (InventoryType)move.DstContainer, (ushort)move.DstSlot, move.Quantity)
            : move.WholeStack
              && InventoryManager.Instance()->MoveItemSlot((InventoryType)move.SrcContainer, (ushort)move.SrcSlot,
                  (InventoryType)move.DstContainer, (ushort)move.DstSlot, true) >= 0;

        if (!ok)
        {
            _operationLog.Add(new ChestLogEntry(ItemName(move.ItemId), move.Quantity, "FAILED — move refused"));
            return;
        }

        _log.Debug("FC qty move: {0} ×{1} {2}:{3} -> {4}:{5} ({6}, displayed tab {7})",
            ItemName(move.ItemId), move.Quantity,
            (InventoryType)move.SrcContainer, move.SrcSlot, (InventoryType)move.DstContainer, move.DstSlot,
            _mover.Available ? "native" : "MoveItemSlot", DisplayedPageForLog());
        _qtyInFlight = move;
        _qtySubmittedUtc = nowUtc;
        _qtyLastSubmitUtc = nowUtc;
        _qtyDeadlineUtc = nowUtc + TimeSpan.FromSeconds(2.5);
        Status = $"moving {_qtySucceeded + 1} of {_qtyTotal}…";
    }

    /// <summary>Delivery check: the destination gained the units (or the source lost them).</summary>
    private bool TryVerifyQuantityMove(DateTime nowUtc)
    {
        var move = _qtyInFlight!;
        var src = ReadSlot(move.SrcContainer, move.SrcSlot);
        var dst = ReadSlot(move.DstContainer, move.DstSlot);

        var srcNow = src?.ItemId == move.ItemId ? src.Value.Quantity : 0;
        var dstNow = dst?.ItemId == move.ItemId ? dst.Value.Quantity : 0;
        if (dstNow >= _qtyDstBefore + move.Quantity || srcNow <= _qtySrcBefore - move.Quantity)
        {
            _qtySucceeded++;
            _qtyConsecutiveFailures = 0;
            _operationLog.Add(new ChestLogEntry(ItemName(move.ItemId), move.Quantity, _qtyVerb.ToLowerInvariant()));
            _qtyInFlight = null;
            return true;
        }

        // The game said no out loud — its toast is the verdict, and every further attempt
        // would fail silently. Abort the whole run with the game's own words.
        if (_lastGameErrorUtc >= _qtySubmittedUtc && _lastGameError.Length > 0)
        {
            _operationLog.Add(new ChestLogEntry(ItemName(move.ItemId), move.Quantity, $"FAILED — {_lastGameError}"));
            AbortQuantityRun($"the game refused: {_lastGameError}");
            return true;
        }

        if (nowUtc > _qtyDeadlineUtc)
        {
            _operationLog.Add(new ChestLogEntry(ItemName(move.ItemId), move.Quantity, "FAILED — not delivered"));
            _qtyInFlight = null;

            // Two silent no-deliveries in a row = something environmental (chest locked by
            // another toon, view-only permissions) — stop grinding the queue.
            if (++_qtyConsecutiveFailures >= 2)
                AbortQuantityRun("moves are not landing — is another toon using the chest?");
            return true;
        }

        return false;
    }

    private void AbortQuantityRun(string reason)
    {
        var dropped = _qtyQueue.Count;
        _qtyQueue.Clear();
        _qtyInFlight = null;
        LastOperation = $"{_qtyVerb} {_qtySucceeded} of {_qtyTotal} — aborted ({reason}"
                        + (dropped > 0 ? $", {dropped} moves dropped)" : ")");
        Status = "aborted";
        _qtyTotal = 0;
        OperationJustFinished = true;
        _contentsCache = null;
    }

    private void FinishQuantityOperation()
    {
        LastOperation = $"{_qtyVerb} {_qtySucceeded} of {_qtyTotal} moves";
        Status = "idle";
        _qtyTotal = 0;
        OperationJustFinished = true;
        _contentsCache = null;
    }

    /// <summary>The chest page (1-5) a move touches, from whichever side is an FC container.</summary>
    private static int ChestPageOf(PlannedMove move)
    {
        const int page1 = (int)InventoryType.FreeCompanyPage1;
        if (move.DstContainer >= page1 && move.DstContainer <= page1 + 4)
            return move.DstContainer - page1 + 1;
        if (move.SrcContainer >= page1 && move.SrcContainer <= page1 + 4)
            return move.SrcContainer - page1 + 1;
        return -1;
    }

    /// <summary>
    /// True when the chest window is showing <paramref name="page"/>; otherwise fires the tab
    /// click (once) and waits, with a settle pause after the switch lands. A tab that will not
    /// come up aborts the run rather than feeding moves to the wrong view.
    /// </summary>
    private bool EnsureDisplayedPage(int page, DateTime nowUtc)
    {
        var addon = _gameGui.GetAddonByName(ChestAddonName);
        if (addon.IsNull)
            return false;

        var unit = (AtkUnitBase*)addon.Address;
        if (FcChestUi.CurrentPage(unit) == page)
        {
            if (_qtySwitchFired)
            {
                // The switch just landed — give the session FCCH's settle before moving.
                _qtySwitchFired = false;
                _qtySettleUntilUtc = nowUtc + TabSettle;
                return false;
            }

            return true;
        }

        if (!_qtySwitchFired)
        {
            _log.Debug("FC chest: switching displayed tab {0} -> {1}", FcChestUi.CurrentPage(unit), page);
            FcChestUi.SwitchToPage(unit, page);
            _qtySwitchFired = true;
            _qtySwitchDeadlineUtc = nowUtc + TimeSpan.FromSeconds(4);
            return false;
        }

        if (nowUtc > _qtySwitchDeadlineUtc)
        {
            _qtySwitchFired = false;
            AbortQuantityRun($"could not switch the chest to tab {page}");
        }

        return false;
    }

    private int DisplayedPageForLog()
    {
        try
        {
            var addon = _gameGui.GetAddonByName(ChestAddonName);
            return addon.IsNull ? -1 : FcChestUi.CurrentPage((AtkUnitBase*)addon.Address);
        }
        catch
        {
            return -1;
        }
    }

    private int FirstUnloadedPage()
    {
        for (var page = 1; page <= 5; page++)
        {
            if (!IsPageLoaded(page))
                return page;
        }

        return -1;
    }

    private List<InvStack> ReadInvStacks(InventoryType type)
    {
        var stacks = new List<InvStack>();
        try
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                return stacks;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0)
                    continue;

                var hq = (item->Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.HighQuality) != 0;
                stacks.Add(new InvStack((int)type, (short)i, item->ItemId, (int)item->Quantity, MaxStackOf(item->ItemId), hq));
            }
        }
        catch
        {
            // fail-open: an unreadable container plans as empty
        }

        return stacks;
    }

    private List<(int Container, short Slot)> FreeSlots(InventoryType[] containers)
    {
        var free = new List<(int, short)>();
        try
        {
            foreach (var type in containers)
            {
                var container = InventoryManager.Instance()->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item == null || item->ItemId == 0)
                        free.Add(((int)type, (short)i));
                }
            }
        }
        catch
        {
            // fail-open
        }

        return free;
    }

    private (uint ItemId, int Quantity)? ReadSlot(int containerType, short slot)
    {
        try
        {
            var container = InventoryManager.Instance()->GetInventoryContainer((InventoryType)containerType);
            if (container == null || !container->IsLoaded || slot >= container->Size)
                return null;

            var item = container->GetInventorySlot(slot);
            return item == null ? null : (item->ItemId, (int)item->Quantity);
        }
        catch
        {
            return null;
        }
    }

    private readonly Dictionary<uint, (string Name, int MaxStack, bool Untradable)> _itemInfoCache = new();

    private (string Name, int MaxStack, bool Untradable) ItemInfo(uint itemId)
    {
        if (_itemInfoCache.TryGetValue(itemId, out var info))
            return info;

        var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        info = sheet != null && sheet.TryGetRow(itemId, out var row)
            ? (row.Name.ExtractText(), (int)row.StackSize, row.IsUntradable)
            : ($"Item#{itemId}", 999, false);
        _itemInfoCache[itemId] = info;
        return info;
    }

    private string ItemName(uint itemId) => ItemInfo(itemId).Name;
    private int MaxStackOf(uint itemId) => ItemInfo(itemId).MaxStack;
    private bool IsUntradable(uint itemId) => ItemInfo(itemId).Untradable;

    /// <summary>
    /// Drive the move queue: alternate ticks submit a move and VERIFY the previous one by
    /// re-reading its source slot — the only reliable success signal (MoveItemSlot's return
    /// code is not: it returned 6 on a move that demonstrably succeeded).
    /// Call every framework tick.
    /// </summary>
    public void Update(DateTime nowUtc)
    {
        if (_pending.Count == 0 && _inFlight == null && _seedReturn == null
            && _qtyQueue.Count == 0 && _qtyInFlight == null && _walkTarget <= 0)
            return;

        if (nowUtc - _lastMoveUtc < MovePacing)
            return;
        _lastMoveUtc = nowUtc;

        // Closing the chest mid-run kills the transfer session — abort instead of spamming errors.
        if (!IsChestOpen())
        {
            _pending.Clear();
            _inFlight = null;
            _seedReturn = null;
            _qtyQueue.Clear();
            _qtyInFlight = null;
            _walkTarget = -1;
            _depositAfterWalk = false;
            Status = "aborted — chest closed";
            OperationJustFinished = true;
            return;
        }

        if (_walkTarget > 0)
        {
            DriveTabWalk(nowUtc);
            return;
        }

        if (_qtyInFlight != null || _qtyQueue.Count > 0)
        {
            DriveQuantityQueue(nowUtc);
            return;
        }

        if (_inFlight != null)
        {
            if (!TryVerifyInFlight())
                return; // server round trip still settling — keep waiting

            _contentsCache = null; // the page just changed
            if (_pending.Count == 0 && _seedReturn == null)
                FinishOperation();
            return;
        }

        if (_pending.Count > 0)
        {
            SubmitNextMove();
            return;
        }

        if (_seedReturn != null)
            DriveSeedReturn();
    }

    /// <summary>
    /// Seed-return state machine (runs after the withdraw queue drains): find or make a
    /// 1-unit stack of the item in the bags (own-inventory SplitItem — silent, unlike the
    /// FC chest containers), then move it back to an empty slot on the page as the seed.
    /// </summary>
    private void DriveSeedReturn()
    {
        var op = _seedReturn!;

        switch (_returnPhase)
        {
            case ReturnPhase.EnsureUnitStack:
                var unit = FindBagUnitStack(op.ItemId);
                if (unit != null)
                {
                    _returnPhase = ReturnPhase.MoveBack;
                    return;
                }

                var donor = FindBagDonorStack(op.ItemId);
                if (donor == null)
                {
                    AbortSeedReturn("aborted — item vanished from bags before seed return");
                    return;
                }

                try
                {
                    var code = InventoryManager.Instance()->SplitItem(
                        (InventoryType)donor.Container, (ushort)donor.Slot, 1);
                    _log.Debug("FC chest: bag split 1 off {0} (code {1})", op.Name, code);
                    _returnPhase = ReturnPhase.VerifyUnitStack;
                    _returnDeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                    Status = "splitting seed unit in bags…";
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "FC chest bag split threw");
                    AbortSeedReturn("aborted — bag split failed");
                }
                return;

            case ReturnPhase.VerifyUnitStack:
                if (FindBagUnitStack(op.ItemId) != null)
                {
                    _returnPhase = ReturnPhase.MoveBack;
                    return;
                }

                if (DateTime.UtcNow > _returnDeadlineUtc)
                    AbortSeedReturn("aborted — bag split did not land (bags full?)");
                return;

            case ReturnPhase.MoveBack:
                var seedStack = FindBagUnitStack(op.ItemId);
                if (seedStack == null)
                {
                    AbortSeedReturn("aborted — seed unit disappeared");
                    return;
                }

                var chestSlot = FindFreeSlot([PageType(op.Page)], out var chestType);
                if (chestSlot < 0)
                {
                    AbortSeedReturn("aborted — no free slot on the page for the seed");
                    return;
                }

                try
                {
                    InventoryManager.Instance()->MoveItemSlot(
                        (InventoryType)seedStack.Container, (ushort)seedStack.Slot,
                        chestType, (ushort)chestSlot, true);
                    _returnPhase = ReturnPhase.VerifyMoveBack;
                    _returnDeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                    Status = "returning seed to the chest…";
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "FC chest seed return threw");
                    AbortSeedReturn("aborted — seed return failed");
                }
                return;

            case ReturnPhase.VerifyMoveBack:
                _contentsCache = null;
                if (ReadPage(op.Page).Any(s => s.ItemId == op.ItemId && s.Quantity == 1))
                {
                    _operationLog.Add(new ChestLogEntry(op.Name, 1, "seed returned"));
                    _seedReturn = null;
                    FinishOperation();
                    return;
                }

                if (DateTime.UtcNow > _returnDeadlineUtc)
                    AbortSeedReturn("aborted — seed did not reach the page");
                return;
        }
    }

    /// <summary>An exact 1-unit stack of the item in the bags (ready to become the seed).</summary>
    private ItemStack? FindBagUnitStack(uint itemId) =>
        ReadBags().FirstOrDefault(s => s.ItemId == itemId && s.Quantity == 1);

    /// <summary>Any bag stack of the item with 2+ units we can split the seed off of.</summary>
    private ItemStack? FindBagDonorStack(uint itemId) =>
        ReadBags().FirstOrDefault(s => s.ItemId == itemId && s.Quantity >= 2);

    private void AbortSeedReturn(string status)
    {
        // The withdrawal itself succeeded — only the seed hand-back failed; say so honestly.
        _seedReturn = null;
        Status = status;
        LastOperation = $"Withdrew {_succeeded} stacks — seed NOT returned ({status})";
        OperationJustFinished = true;
    }

    private void SubmitNextMove()
    {
        var move = _pending.Dequeue();
        try
        {
            // Prefer MERGING into an existing stack of the same item (that is the point of
            // consolidating duplicates) — strict fit only, so the source stack always empties
            // completely and verification stays simple. Empty slot is the fallback.
            var destinationSlot = FindDestination(move, _withdrawing ? PlayerBags : [_moveDestination],
                out var destinationType, out var merged);
            if (destinationSlot < 0)
            {
                _pending.Clear();
                Status = _withdrawing ? "aborted — inventory full" : "aborted — chest page full";
                OperationJustFinished = true;
                return;
            }

            var code = InventoryManager.Instance()->MoveItemSlot(
                (InventoryType)move.SrcContainer, (ushort)move.SrcSlot,
                destinationType, (ushort)destinationSlot, true);
            _log.Debug("FC chest: submitted {0} ×{1} ({2}, code {3})",
                move.Name, move.Quantity, merged ? "merge" : "free slot", code);

            _inFlight = move;
            _inFlightDeadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
            Status = $"{(_withdrawing ? "Withdrawing" : "Entrusting")} "
                     + $"{_movesVerified + 1}/{_movesVerified + _pending.Count + 1}…";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "FC chest move threw ({0})", move.Name);
            _operationLog.Add(new ChestLogEntry(move.Name, move.Quantity, "FAILED"));
            if (_pending.Count == 0)
                FinishOperation();
        }
    }

    /// <summary>
    /// Destination slot: a same-item stack with room for the WHOLE source stack (same HQ/
    /// collectable flags, strict fit — partial merges would leave a remainder the plan doesn't
    /// know about), else the first empty slot. -1 when neither exists.
    /// </summary>
    private int FindDestination(ChestMove move, InventoryType[] candidates, out InventoryType type, out bool merged)
    {
        var stackSize = GetStackSize(move.ItemId);
        var sourceFlags = ReadItemFlags((InventoryType)move.SrcContainer, move.SrcSlot);

        if (stackSize > 1 && sourceFlags != null)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var container = InventoryManager.Instance()->GetInventoryContainer(candidate);
                    if (container == null || !container->IsLoaded)
                        continue;

                    for (var i = 0; i < container->Size; i++)
                    {
                        var item = container->GetInventorySlot(i);
                        if (item != null
                            && item->ItemId == move.ItemId
                            && item->Flags == sourceFlags.Value
                            && item->Quantity > 0
                            && item->Quantity + move.Quantity <= stackSize)
                        {
                            type = candidate;
                            merged = true;
                            return i;
                        }
                    }
                }
                catch
                {
                    // skip unreadable container
                }
            }
        }

        merged = false;
        return FindFreeSlot(candidates, out type);
    }

    private FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags? ReadItemFlags(InventoryType container, short slot)
    {
        try
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(container);
            if (inv == null || !inv->IsLoaded || slot >= inv->Size)
                return null;

            var item = inv->GetInventorySlot(slot);
            return item == null ? null : item->Flags;
        }
        catch
        {
            return null;
        }
    }

    private int GetStackSize(uint itemId)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<Item>();
            if (sheet != null && sheet.TryGetRow(itemId, out var row))
                return (int)row.StackSize;
        }
        catch
        {
            // fall through
        }

        return 1; // unknown = never merge
    }

    /// <summary>
    /// Success = the source slot no longer holds the item we moved. FC chest moves are a
    /// SERVER round trip — the slot clears noticeably later than the call (verified in
    /// testing: a snapshot check 250ms after submit reported "still in place" on a move that
    /// landed fine). So this WAITS until the slot clears or the deadline passes; returns
    /// false while still waiting.
    /// </summary>
    private bool TryVerifyInFlight()
    {
        var move = _inFlight!;

        var stillThere = false;
        try
        {
            var container = InventoryManager.Instance()->GetInventoryContainer((InventoryType)move.SrcContainer);
            if (container != null && container->IsLoaded && move.SrcSlot < container->Size)
            {
                var item = container->GetInventorySlot(move.SrcSlot);
                stillThere = item != null && item->ItemId == move.ItemId && item->Quantity > 0;
            }
        }
        catch
        {
            // unreadable — assume it moved; the log stays honest enough
        }

        if (stillThere && DateTime.UtcNow < _inFlightDeadlineUtc)
            return false; // still settling — check again next tick

        _inFlight = null;
        _movesVerified++;
        var verb = _withdrawing ? "withdrawn" : "entrusted";
        if (!stillThere)
            _succeeded++;
        _operationLog.Add(new ChestLogEntry(move.Name, move.Quantity, stillThere ? "FAILED (still in place)" : verb));
        return true;
    }

    private void FinishOperation()
    {
        LastOperation = $"{(_withdrawing ? "Withdrew" : "Entrusted")} {_succeeded}/{_movesVerified} "
                        + $"{(_movesVerified == 1 ? "stack" : "stacks")} "
                        + $"{(_withdrawing ? "from" : "to")} Page {PageNumber(_activePage)}";
        Status = "idle";
        OperationJustFinished = true;
    }

    private void BeginOperation(List<ChestMove> moves, InventoryType page, bool withdrawing, int pageNumber)
    {
        _operationLog.Clear();
        _succeeded = 0;
        _movesVerified = 0;
        _inFlight = null;
        _withdrawing = withdrawing;
        _activePage = page;
        _moveDestination = withdrawing ? InventoryType.Inventory1 : page; // withdraw picks bags per-move
        foreach (var move in moves)
            _pending.Enqueue(move);

        Status = moves.Count == 0 ? "nothing to do" : $"queued {moves.Count} moves";
        if (moves.Count == 0)
        {
            LastOperation = $"{(withdrawing ? "Withdraw" : "Entrust")}: nothing eligible on Page {pageNumber}";
            OperationJustFinished = true;
        }
    }

    private List<ItemStack> ReadBags()
    {
        var stacks = new List<ItemStack>();
        foreach (var bag in PlayerBags)
            ReadContainer(bag, stacks);
        return stacks;
    }

    private List<ItemStack> ReadPage(int page)
    {
        var stacks = new List<ItemStack>();
        ReadContainer(PageType(page), stacks);
        return stacks;
    }

    private void ReadContainer(InventoryType type, List<ItemStack> into)
    {
        try
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                return;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId == 0 || item->Quantity <= 0)
                    continue;

                into.Add(new ItemStack(item->ItemId, ResolveItemName(item->ItemId), item->Quantity,
                    (int)type, (short)i));
            }
        }
        catch
        {
            // container unreadable mid-transition — treat as empty (planner then does nothing)
        }
    }

    private int FindFreeSlot(InventoryType[] candidates, out InventoryType type)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                var container = InventoryManager.Instance()->GetInventoryContainer(candidate);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item != null && item->ItemId == 0)
                    {
                        type = candidate;
                        return i;
                    }
                }
            }
            catch
            {
                // skip unreadable container
            }
        }

        type = InventoryType.Inventory1;
        return -1;
    }

    private string ResolveItemName(uint itemId)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<Item>();
            if (sheet != null && sheet.TryGetRow(itemId, out var row))
                return row.Name.ExtractText();
        }
        catch
        {
            // fall through
        }

        return $"item {itemId}";
    }

    private static InventoryType PageType(int page) => page switch
    {
        2 => InventoryType.FreeCompanyPage2,
        3 => InventoryType.FreeCompanyPage3,
        4 => InventoryType.FreeCompanyPage4,
        5 => InventoryType.FreeCompanyPage5,
        _ => InventoryType.FreeCompanyPage1,
    };

    private static int PageNumber(InventoryType type) => type switch
    {
        InventoryType.FreeCompanyPage2 => 2,
        InventoryType.FreeCompanyPage3 => 3,
        InventoryType.FreeCompanyPage4 => 4,
        InventoryType.FreeCompanyPage5 => 5,
        _ => 1,
    };
}
