using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Charon.Features.FcChest;
using FFXIVClientStructs.FFXIV.Client.Game;
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

    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;

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
    private int _succeeded;

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

    public FcChestManager(IGameGui gameGui, IDataManager dataManager, IPluginLog log)
    {
        _gameGui = gameGui;
        _dataManager = dataManager;
        _log = log;
    }

    public string Status { get; private set; } = "idle";

    /// <summary>Summary of the last completed operation ("Entrusted 12 stacks to Page 1").</summary>
    public string LastOperation { get; private set; } = "";

    /// <summary>Per-item results of the last operation, newest run only.</summary>
    public IReadOnlyList<ChestLogEntry> OperationLog => _operationLog;

    public bool Busy => _pending.Count > 0 || _inFlight != null || _seedReturn != null;

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
        if (Busy || !FcChestPlanner.CanExecute(IsChestOpen(), IsPageLoaded(page)))
            return 0;

        var moves = FcChestPlanner.PlanEntrust(ReadBags(), ReadPage(page));
        BeginOperation(moves, PageType(page), withdrawing: false, page);
        return moves.Count;
    }

    /// <summary>Withdraw all but the last stack of each item on the page. Returns moves queued.</summary>
    public int StartWithdraw(int page)
    {
        if (Busy || !FcChestPlanner.CanExecute(IsChestOpen(), IsPageLoaded(page)))
            return 0;

        var moves = FcChestPlanner.PlanWithdraw(ReadPage(page));
        BeginOperation(moves, PageType(page), withdrawing: true, page);
        return moves.Count;
    }

    /// <summary>
    /// Drive the move queue: alternate ticks submit a move and VERIFY the previous one by
    /// re-reading its source slot — the only reliable success signal (MoveItemSlot's return
    /// code is not: it returned 6 on a move that demonstrably succeeded).
    /// Call every framework tick.
    /// </summary>
    public void Update(DateTime nowUtc)
    {
        if (_pending.Count == 0 && _inFlight == null && _seedReturn == null)
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
            Status = "aborted — chest closed";
            OperationJustFinished = true;
            return;
        }

        if (_inFlight != null)
        {
            VerifyInFlight();
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
            Status = $"{(_withdrawing ? "Withdrawing" : "Entrusting")} "
                     + $"{_operationLog.Count + 1}/{_operationLog.Count + _pending.Count + 1}…";
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

    /// <summary>Success = the source slot no longer holds the item we moved.</summary>
    private void VerifyInFlight()
    {
        var move = _inFlight!;
        _inFlight = null;

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

        var verb = _withdrawing ? "withdrawn" : "entrusted";
        if (!stillThere)
            _succeeded++;
        _operationLog.Add(new ChestLogEntry(move.Name, move.Quantity, stillThere ? "FAILED (still in place)" : verb));
    }

    private void FinishOperation()
    {
        LastOperation = $"{(_withdrawing ? "Withdrew" : "Entrusted")} {_succeeded}/{_operationLog.Count} "
                        + $"{(_operationLog.Count == 1 ? "stack" : "stacks")} "
                        + $"{(_withdrawing ? "from" : "to")} Page {PageNumber(_activePage)}";
        Status = "idle";
        OperationJustFinished = true;
    }

    private void BeginOperation(List<ChestMove> moves, InventoryType page, bool withdrawing, int pageNumber)
    {
        _operationLog.Clear();
        _succeeded = 0;
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
