using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Charon.Features.FcChest;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>
/// Entrusts every bag stack whose item the chocobo saddlebag already holds — the saddlebag
/// window is the session (it must be open; the containers only load then). Selection is
/// <see cref="SaddlebagDuplicates"/> (pure); each move is the item's "Add All to Saddlebag"
/// context entry (Addon row 881, text-matched — the language-independent doctrine), driven through the
/// same open-then-click phases as every other context-menu use. Behaviour ported from
/// PandorasBox's Saddlebag Entrust Duplicates (BSD-3-Clause, PunishXIV/PandorasBox).
///
/// One stack per pass step, RE-PLANNED from live containers each round — never a replayed batch.
/// </summary>
public sealed unsafe class SaddlebagEntruster
{
    private const string SaddlebagAddon = "InventoryBuddy";
    /// <summary>
    /// Addon row 881 "Add All to Saddlebag" — NOT row 886 "Add to Saddlebag". User-verified:
    /// the singular entry needs a follow-up interaction (fired-but-nothing-moved, three live
    /// timeouts), while Add All acts immediately. Whole-item moves are what this tool wants
    /// anyway.
    /// </summary>
    private const uint AddToSaddlebagTextRow = 881;

    private static readonly TimeSpan ActionPacing = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Far above any real bag; the never-loop cap.</summary>
    private const int MaxMoves = 150;

    private static readonly InventoryType[] SaddlebagContainers =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;
    private readonly Func<bool> _otherOpsBusy;
    private readonly IPluginLog _log;

    private enum Phase { Idle, Round, WaitMenu, WaitMoved }

    private Phase _phase = Phase.Idle;
    private int _moves;
    private BagStack _menuStack;
    private uint _menuItemId;
    private bool _menuIssued;
    private int _bagCountAtMove;

    /// <summary>Items whose entrust never landed this run — the game refused them (or their menu
    /// did), so re-picking them forever would stall the whole pass. Skipped, reported, retried
    /// next run.</summary>
    private readonly HashSet<uint> _refusedItems = new();

    private DateTime _phaseSinceUtc;
    private DateTime _lastActionUtc = DateTime.MinValue;

    public SaddlebagEntruster(IGameGui gameGui, IDataManager dataManager,
        Func<bool> otherOpsBusy, IPluginLog log)
    {
        _gameGui = gameGui;
        _dataManager = dataManager;
        _otherOpsBusy = otherOpsBusy;
        _log = log;
    }

    public bool Busy => _phase != Phase.Idle;

    public string Status { get; private set; } = "idle";

    public bool IsSaddlebagOpen()
    {
        var addon = _gameGui.GetAddonByName(SaddlebagAddon);
        return !addon.IsNull && addon.IsVisible;
    }

    /// <summary>How many bag stacks would be entrusted right now, for the button label.</summary>
    public int CountEntrustable()
    {
        try
        {
            return IsSaddlebagOpen() ? PlanRound().Count : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Start entrusting. True = accepted; progress lands in <see cref="Status"/>.</summary>
    public bool Start()
    {
        if (Busy || _otherOpsBusy())
        {
            Status = "refused — another operation is running";
            return false;
        }

        if (!IsSaddlebagOpen())
        {
            Status = "refused — the saddlebag window is not open (that window is the session)";
            return false;
        }

        _moves = 0;
        _refusedItems.Clear();
        EnterPhase(Phase.Round);
        Status = "entrusting duplicates";
        return true;
    }

    public void Cancel()
    {
        if (Busy)
            Finish("cancelled");
    }

    public void Update(DateTime now)
    {
        if (_phase == Phase.Idle)
            return;

        try
        {
            if (!IsSaddlebagOpen())
            {
                Finish("saddlebag closed mid-run");
                return;
            }

            switch (_phase)
            {
                case Phase.Round:
                    RunRound(now);
                    break;
                case Phase.WaitMenu:
                    DriveMenuClick(now);
                    break;
                case Phase.WaitMoved:
                    CheckMoved(now);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Saddlebag entrust threw");
            Finish("threw (see log)");
        }
    }

    private void RunRound(DateTime now)
    {
        if (now - _lastActionUtc < ActionPacing)
            return;

        var plan = PlanRound();
        plan.RemoveAll(c => _refusedItems.Contains(c.ItemId));
        if (plan.Count == 0)
        {
            var skipped = _refusedItems.Count == 0 ? "" : $" ({_refusedItems.Count} item(s) refused — see log)";
            Finish(_moves == 0 && _refusedItems.Count == 0
                ? "nothing to entrust — no duplicates in the bags"
                : $"done — {_moves} stack(s) entrusted{skipped}");
            return;
        }

        if (_moves >= MaxMoves)
        {
            Finish($"gave up after {MaxMoves} moves");
            return;
        }

        var next = plan[0];
        _menuStack = new BagStack((InventoryType)next.Container, next.Slot, next.Quantity);
        _menuItemId = next.ItemId;
        _menuIssued = false;
        _bagCountAtMove = CountInBags(next.ItemId);
        EnterPhase(Phase.WaitMenu);
        Status = $"entrusting stack of {next.Quantity} ({_moves + 1})";
    }

    private void DriveMenuClick(DateTime now)
    {
        if (!_menuIssued)
        {
            _menuIssued = InventoryContextMenu.OpenMenu(_menuStack);
            Timeout(now, "could not open the inventory window for the item's menu");
            return; // clicked next tick at the earliest — the menu needs a frame to build
        }

        switch (InventoryContextMenu.TryClickEntry(_dataManager, AddToSaddlebagTextRow, _log))
        {
            case InventoryContextMenu.ClickResult.Clicked:
                EnterPhase(Phase.WaitMoved);
                break;
            case InventoryContextMenu.ClickResult.EntryMissing:
                Finish("no Add All to Saddlebag entry (is the saddlebag really open?)");
                break;
            default:
                Timeout(now, "the context menu never opened");
                break;
        }
    }

    private void CheckMoved(DateTime now)
    {
        // The honest signal: the bags hold fewer of THE ITEM WE MOVED than before the click —
        // compared by the id captured at move time, since a successful move empties the slot
        // (and sorting can slide something else into it).
        if (CountInBags(_menuItemId) < _bagCountAtMove || SlotIsEmpty(_menuStack))
        {
            _moves++;
            _lastActionUtc = now;
            EnterPhase(Phase.Round); // re-plan from live containers
            return;
        }

        // Adding a stack can raise the quantity prompt — answer it with the whole stack (this
        // is an entrust-everything tool; partials are not a thing here).
        var numeric = _gameGui.GetAddonByName("InputNumeric");
        if (!numeric.IsNull && numeric.IsVisible)
        {
            ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)numeric.Address)
                ->FireCallbackInt(_menuStack.Quantity);
            return; // the bag count check next tick confirms it landed
        }

        if (now - _phaseSinceUtc > StepTimeout)
        {
            // The click fired and nothing moved — the game refused this item (seen live with
            // Allagan Silver Pieces). Skip it for the run and keep entrusting the rest; a stall
            // on one refused item must never cost the whole pass.
            _log.Warning("Saddlebag entrust refused by the game: item {0} qty {1} — skipping it this run",
                _menuItemId, _menuStack.Quantity);
            _refusedItems.Add(_menuItemId);
            _lastActionUtc = now;
            EnterPhase(Phase.Round);
        }
    }

    private bool Timeout(DateTime now, string reason)
    {
        if (now - _phaseSinceUtc <= StepTimeout)
            return false;

        Finish(reason);
        return true;
    }

    private void EnterPhase(Phase phase)
    {
        _phase = phase;
        _phaseSinceUtc = DateTime.UtcNow;
    }

    private void Finish(string detail)
    {
        _phase = Phase.Idle;
        Status = detail;
    }

    // --- Live reads ---

    private List<SaddlebagCandidate> PlanRound()
    {
        var saddleIds = new HashSet<uint>();
        var manager = InventoryManager.Instance();
        var sheet = _dataManager.GetExcelSheet<Item>();
        if (manager == null || sheet == null)
            return [];

        foreach (var type in SaddlebagContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->GetBaseItemId() != 0)
                    saddleIds.Add(slot->GetBaseItemId());
            }
        }

        var bagStacks = new List<SaddlebagCandidate>();
        foreach (var bag in InventoryContextMenu.PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->GetBaseItemId() == 0)
                    continue;

                var unique = sheet.TryGetRow(slot->GetBaseItemId(), out var row) && row.IsUnique;
                bagStacks.Add(new SaddlebagCandidate(
                    slot->GetBaseItemId(), (int)bag, (short)i, (int)slot->GetQuantity(), unique));
            }
        }

        return SaddlebagDuplicates.FindEntrustable(bagStacks, saddleIds);
    }

    private static int CountInBags(uint itemId) => GilCapSeller.CountInBags(itemId);

    private static uint BagItemIdAt(BagStack stack)
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(stack.Container);
        var slot = container == null ? null : container->GetInventorySlot(stack.Slot);
        return slot == null ? 0 : slot->GetBaseItemId();
    }

    private static bool SlotIsEmpty(BagStack stack) => BagItemIdAt(stack) == 0;
}
