using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using Charon.Features.Leveling;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>
/// Sells exactly enough of one item to meet-or-exceed the free trial's 300k gil cap. Thin unsafe
/// adapter; the quantity is <see cref="StackSplitCalculator"/> (pure) and the context-menu
/// mechanics are <see cref="InventoryContextMenu"/> (verified from production plugins).
///
/// Two entry points:
/// - <see cref="Request"/> (IPC): the vendor shop must already be OPEN — SealBreaker walked
///   there. That window is the session, same doctrine as the FC chest.
/// - <see cref="RequestTrip"/> (the GIL section's button): also finds the nearest gil vendor,
///   walks over via vnavmesh and opens the shop first. A gil vendor is an EventNpc whose
///   ENpcBase data carries a GilShop event handler — GilShop row ids start at 0x40000
///   (262144, verified against the sheet), so (handler >> 16) == 4 is the test.
///
/// Selling context-menu-sells whole stacks, so exact quantities are SPLIT off first (same menu,
/// row 92) and sold as their own stack. One action per tick, RE-PLANNED from live bags and live
/// gil every round — never a replayed batch.
/// </summary>
public sealed class GilCapSeller
{
    /// <summary>The free trial's personal gil ceiling — the target this seller fills to.</summary>
    public const long FreeTrialGilCap = 300_000;

    private const string ShopAddon = "Shop";
    private static readonly TimeSpan ActionPacing = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NavTimeout = TimeSpan.FromSeconds(90);
    private const float VendorSearchRadius = 80f;
    private const float InteractRange = 4f;
    private const int MaxInteractAttempts = 3;

    /// <summary>Rounds converge fast (one split + one sale is typical); this is the never-loop cap.</summary>
    private const int MaxRounds = 20;

    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;
    private readonly NavClient _nav;
    private readonly InteractHelper _interact;
    private readonly Func<bool> _isFreeTrial;
    private readonly Func<bool> _otherOpsBusy;
    private readonly Func<bool> _navOwnedElsewhere;
    private readonly Action<string, bool, string> _completed;
    private readonly IPluginLog _log;

    private enum Phase
    {
        Idle, NavToVendor, OpenShop, Round,
        WaitSellMenu,   // the context menu needs a frame to build before its entry can be clicked
        WaitSplitIssue, // inventory window must be visible before SplitItem lands
        WaitSplitLanded, WaitSold,
    }

    private Phase _phase = Phase.Idle;
    private uint _itemId;
    private long _unitPrice;
    private int _splitQuantity;
    private int _rounds;
    private long _gilAtSale;
    private int _interactAttempts;
    private ulong _vendorEntityId;
    private bool _navIssued;
    private BagStack _menuStack;
    private bool _menuIssued;
    private DateTime _phaseSinceUtc;
    private DateTime _lastActionUtc = DateTime.MinValue;

    public GilCapSeller(IGameGui gameGui, IDataManager dataManager, IObjectTable objectTable,
        NavClient nav, InteractHelper interact,
        Func<bool> isFreeTrial, Func<bool> otherOpsBusy, Func<bool> navOwnedElsewhere,
        Action<string, bool, string> completed, IPluginLog log)
    {
        _gameGui = gameGui;
        _dataManager = dataManager;
        _objectTable = objectTable;
        _nav = nav;
        _interact = interact;
        _isFreeTrial = isFreeTrial;
        _otherOpsBusy = otherOpsBusy;
        _navOwnedElsewhere = navOwnedElsewhere;
        _completed = completed;
        _log = log;
    }

    public bool Busy => _phase != Phase.Idle;

    /// <summary>What it is doing, or why the last run ended how it did — for the Debug line.</summary>
    public string Status { get; private set; } = "idle";

    /// <summary>Gil headroom left under the cap, for the GIL section's display.</summary>
    public static long Headroom => Math.Max(0, FreeTrialGilCap - InventoryContextMenu.CurrentGil());

    public static long CurrentGil() => InventoryContextMenu.CurrentGil();

    /// <summary>Name + vendor sell price of an item, for the GIL section's display.</summary>
    public (string Name, long Price) ItemInfo(uint itemId)
    {
        var sheet = _dataManager.GetExcelSheet<Item>();
        return sheet != null && sheet.TryGetRow(itemId, out var item)
            ? (item.Name.ExtractText(), item.PriceLow)
            : ($"item {itemId}", 0);
    }

    /// <summary>How many of the item the bags hold, for the GIL section's display.</summary>
    public static int CountInBags(uint itemId)
    {
        var total = 0;
        foreach (var stack in InventoryContextMenu.FindStacks(itemId))
            total += stack.Quantity;
        return total;
    }

    /// <summary>IPC entry: sell at the ALREADY-OPEN vendor shop. True = accepted.</summary>
    public bool Request(uint itemId)
    {
        if (!CheckCommon(itemId))
            return false;

        if (!IsShopOpen())
        {
            Status = "SellToGilCap → refused (no vendor shop open — that window is the session)";
            return false;
        }

        _rounds = 0;
        EnterPhase(Phase.Round);
        Status = $"selling item {itemId} to the gil cap ({_unitPrice} gil each)";
        return true;
    }

    /// <summary>
    /// UI entry: the whole trip — nearest gil vendor, walk over, open the shop, then sell
    /// (splitting the exact stack once the shop is open). True = accepted.
    /// </summary>
    public bool RequestTrip(uint itemId)
    {
        if (!CheckCommon(itemId))
            return false;

        if (Headroom <= 0)
        {
            Status = $"nothing to do — already at the cap ({CurrentGil():N0} gil)";
            return false;
        }

        if (CountInBags(itemId) == 0)
        {
            Status = "nothing to sell — no stacks of that item in the bags";
            return false;
        }

        if (!_nav.IsAvailable)
        {
            Status = "refused — vnavmesh unavailable (needed to reach the vendor)";
            return false;
        }

        if (_navOwnedElsewhere())
        {
            Status = "refused — navigation is busy (following/boarding); stop that first";
            return false;
        }

        var vendor = FindNearestGilVendor();
        if (vendor == null)
        {
            Status = $"no gil vendor within {VendorSearchRadius:0}y";
            return false;
        }

        _vendorEntityId = vendor.GameObjectId;
        _rounds = 0;
        _interactAttempts = 0;
        _navIssued = false;
        EnterPhase(Phase.NavToVendor);
        Status = $"heading to {vendor.Name.TextValue} to sell";
        return true;
    }

    /// <summary>Stop the run (UI Stop button). Safe at any phase; releases our nav path.</summary>
    public void Cancel()
    {
        if (!Busy)
            return;

        if (_navIssued)
            _nav.Stop();
        Finish(false, "cancelled");
    }

    private bool CheckCommon(uint itemId)
    {
        if (Busy || _otherOpsBusy())
        {
            Status = "refused — another leveling operation is running";
            return false;
        }

        if (!_isFreeTrial())
        {
            // The 300k cap IS the free trial; a paid account has no target to fill to.
            Status = "refused — no gil cap on this account";
            return false;
        }

        var sheet = _dataManager.GetExcelSheet<Item>();
        if (sheet == null || !sheet.TryGetRow(itemId, out var item) || item.PriceLow == 0)
        {
            Status = $"refused — item {itemId} has no vendor sell price";
            return false;
        }

        _itemId = itemId;
        _unitPrice = item.PriceLow;
        return true;
    }

    /// <summary>Drive the state machine. Call every framework tick.</summary>
    public void Update(DateTime now)
    {
        if (_phase == Phase.Idle)
            return;

        try
        {
            switch (_phase)
            {
                case Phase.NavToVendor:
                    DriveToVendor(now);
                    break;
                case Phase.OpenShop:
                    TryOpenShop(now);
                    break;
                default:
                    DriveSale(now);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Gil cap sale threw");
            Finish(false, "threw (see log)");
        }
    }

    // --- Trip phases ---

    private void DriveToVendor(DateTime now)
    {
        var vendor = FindVendorById();
        if (vendor == null)
        {
            Finish(false, "lost sight of the vendor on the way");
            return;
        }

        var local = _objectTable.LocalPlayer;
        if (local == null)
            return;

        if (Vector3.Distance(local.Position, vendor.Position) <= InteractRange)
        {
            if (_navIssued)
            {
                _nav.Stop();
                _navIssued = false;
            }

            EnterPhase(Phase.OpenShop);
            return;
        }

        if (now - _phaseSinceUtc > NavTimeout)
        {
            if (_navIssued)
                _nav.Stop();
            Finish(false, "could not reach the vendor in time");
            return;
        }

        if (!_navIssued || (!_nav.IsPathRunning && now - _lastActionUtc > TimeSpan.FromSeconds(1)))
        {
            if (_nav.MoveCloseTo(vendor.Position, InteractRange - 1f))
            {
                _navIssued = true;
                _lastActionUtc = now;
            }
        }

        Status = $"walking to {vendor.Name.TextValue} ({Vector3.Distance(local.Position, vendor.Position):F1}y)";
    }

    private void TryOpenShop(DateTime now)
    {
        if (IsShopOpen())
        {
            _rounds = 0;
            EnterPhase(Phase.Round);
            Status = "shop open — selling";
            return;
        }

        if (now - _lastActionUtc < TimeSpan.FromSeconds(2))
            return;

        if (_interactAttempts >= MaxInteractAttempts)
        {
            Finish(false, "the shop never opened (a menu vendor? see log for the addon state)");
            return;
        }

        var vendor = FindVendorById();
        if (vendor == null)
        {
            Finish(false, "vendor vanished before the shop opened");
            return;
        }

        _interactAttempts++;
        _lastActionUtc = now;
        _interact.TryInteract(vendor);
        Status = $"opening the shop (attempt {_interactAttempts})";
    }

    // --- Sale phases (shop open from here on) ---

    private void DriveSale(DateTime now)
    {
        // The shop closing mid-run ends the session — same doctrine as the FC chest abort.
        if (!IsShopOpen())
        {
            Finish(false, "vendor shop closed mid-run");
            return;
        }

        switch (_phase)
        {
            case Phase.Round:
                RunRound(now);
                break;
            case Phase.WaitSellMenu:
                DriveMenuClick(now, InventoryContextMenu.SellTextRow, Phase.WaitSold,
                    "no Sell entry on the item's menu (shop not interactable?)");
                break;
            case Phase.WaitSplitIssue:
                DriveSplitIssue(now);
                break;
            case Phase.WaitSplitLanded:
                CheckSplitLanded(now);
                break;
            case Phase.WaitSold:
                CheckSold(now);
                break;
        }
    }

    /// <summary>
    /// Open-then-click across ticks: first get the inventory window + context menu up (retried —
    /// the inventory may need opening and the menu a frame to build), then click the entry.
    /// </summary>
    private void DriveMenuClick(DateTime now, uint textRow, Phase onClicked, string missingReason)
    {
        if (!_menuIssued)
        {
            _menuIssued = InventoryContextMenu.OpenMenu(_menuStack);
            Timeout(now, "could not open the inventory window for the item's menu");
            return; // clicked next tick at the earliest — the menu needs a frame to build
        }

        switch (InventoryContextMenu.TryClickEntry(_dataManager, textRow))
        {
            case InventoryContextMenu.ClickResult.Clicked:
                _lastActionUtc = now;
                EnterPhase(onClicked);
                break;
            case InventoryContextMenu.ClickResult.EntryMissing:
                Finish(false, missingReason);
                break;
            default:
                Timeout(now, "the context menu never opened");
                break;
        }
    }

    private void RunRound(DateTime now)
    {
        if (now - _lastActionUtc < ActionPacing)
            return;

        var gil = InventoryContextMenu.CurrentGil();
        var headroom = FreeTrialGilCap - gil;
        if (headroom <= 0)
        {
            Finish(true, $"gil cap met ({gil:N0})");
            return;
        }

        if (++_rounds > MaxRounds)
        {
            Finish(false, $"gave up after {MaxRounds} rounds at {gil:N0} gil");
            return;
        }

        var stacks = InventoryContextMenu.FindStacks(_itemId);
        var total = 0;
        foreach (var stack in stacks)
            total += stack.Quantity;

        if (total == 0)
        {
            Finish(false, $"out of items at {gil:N0} gil — {headroom:N0} short of the cap");
            return;
        }

        var wanted = StackSplitCalculator.QuantityToReach(headroom, _unitPrice, total);
        Status = $"round {_rounds}: {gil:N0} gil, need {wanted} of {total} held";

        // Exact stack → sell it. Bigger stack → split the exact amount off (sold next round).
        // Only smaller stacks → sell the largest and recompute; the tail round goes exact.
        foreach (var stack in stacks)
        {
            if (stack.Quantity == wanted)
            {
                FireSell(stack, now);
                return;
            }
        }

        foreach (var stack in stacks)
        {
            if (stack.Quantity > wanted)
            {
                FireSplit(stack, wanted, now);
                return;
            }
        }

        var largest = stacks[0];
        foreach (var stack in stacks)
        {
            if (stack.Quantity > largest.Quantity)
                largest = stack;
        }

        FireSell(largest, now);
    }

    private void FireSell(BagStack stack, DateTime now)
    {
        _gilAtSale = InventoryContextMenu.CurrentGil();
        _menuStack = stack;
        _menuIssued = false;
        _lastActionUtc = now;
        EnterPhase(Phase.WaitSellMenu);
        Status = $"selling a stack of {stack.Quantity}";
    }

    private void FireSplit(BagStack stack, int quantity, DateTime now)
    {
        _splitQuantity = quantity;
        _menuStack = stack;
        _lastActionUtc = now;
        EnterPhase(Phase.WaitSplitIssue);
        Status = $"splitting {quantity} off a stack of {stack.Quantity}";
    }

    /// <summary>Direct SplitItem — the FC chest's own verified split — once the inventory is up.</summary>
    private void DriveSplitIssue(DateTime now)
    {
        if (!InventoryContextMenu.EnsureInventoryOpen())
        {
            Timeout(now, "could not open the inventory window for the split");
            return;
        }

        InventoryContextMenu.SplitStack(_menuStack, _splitQuantity);
        _lastActionUtc = now;
        EnterPhase(Phase.WaitSplitLanded);
    }

    private void CheckSplitLanded(DateTime now)
    {
        if (Timeout(now, "the split never landed in the bags"))
            return;

        foreach (var stack in InventoryContextMenu.FindStacks(_itemId))
        {
            if (stack.Quantity == _splitQuantity)
            {
                EnterPhase(Phase.Round); // next round finds the exact stack and sells it
                return;
            }
        }
    }

    private void CheckSold(DateTime now)
    {
        if (InventoryContextMenu.CurrentGil() > _gilAtSale)
        {
            EnterPhase(Phase.Round); // recompute; usually completes with the cap met
            return;
        }

        Timeout(now, "the sale never went through (gil did not change)");
    }

    private bool Timeout(DateTime now, string reason)
    {
        if (now - _phaseSinceUtc <= StepTimeout)
            return false;

        Finish(false, reason);
        return true;
    }

    private void EnterPhase(Phase phase)
    {
        _phase = phase;
        _phaseSinceUtc = DateTime.UtcNow;
    }

    private void Finish(bool ok, string detail)
    {
        _phase = Phase.Idle;
        _vendorEntityId = 0;
        _navIssued = false;
        Status = ok ? $"done — {detail}" : $"FAILED — {detail}";
        _completed("sellToCap", ok, detail);
    }

    // --- Vendor discovery ---

    private bool IsShopOpen()
    {
        var shop = _gameGui.GetAddonByName(ShopAddon);
        return !shop.IsNull && shop.IsVisible;
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindVendorById()
    {
        if (_vendorEntityId == 0)
            return null;

        foreach (var obj in _objectTable)
        {
            if (obj.GameObjectId == _vendorEntityId)
                return obj;
        }

        return null;
    }

    /// <summary>
    /// Nearest targetable EventNpc carrying a GilShop event handler. GilShop rows start at
    /// 0x40000 (262144, verified), so the handler's high word is 4.
    /// </summary>
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNearestGilVendor()
    {
        var local = _objectTable.LocalPlayer;
        if (local == null)
            return null;

        var sheet = _dataManager.GetExcelSheet<ENpcBase>();
        if (sheet == null)
            return null;

        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDistance = VendorSearchRadius;

        foreach (var obj in _objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc || !obj.IsTargetable)
                continue;

            var distance = Vector3.Distance(local.Position, obj.Position);
            if (distance >= bestDistance)
                continue;

            if (!sheet.TryGetRow(obj.BaseId, out var npc))
                continue;

            foreach (var handler in npc.ENpcData)
            {
                if ((handler.RowId >> 16) == 4)
                {
                    bestDistance = distance;
                    best = obj;
                    break;
                }
            }
        }

        return best;
    }
}
