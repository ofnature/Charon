using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Charon.Features.Leveling;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>
/// Doman Enclave donation support: reads the basket window, splits the exact stack, stages it.
/// Thin unsafe adapter; the arithmetic is <see cref="DonationWindowParser"/> +
/// <see cref="StackSplitCalculator"/> (pure), the context menu is
/// <see cref="InventoryContextMenu"/> (verified mechanics).
///
/// The flow is three phases BY THE GAME'S OWN RULES (user-verified): the basket blocks stack
/// splits while open, and the Donate context entry takes the WHOLE stack. So: read the window →
/// close it → split the exact quantity → reopen → stage the exact stack → deliver.
///
/// Window facts from a live node dump: addon <c>ReconstructionBox</c>; remaining weekly budget
/// is text node 21, the rate node 7, Grand Total node 25, the Donate button node 30 (enabled
/// only once something is staged). Staging is the "Donate" context entry (Addon row 11580),
/// verified from a live menu.
///
/// The Donate press and the budget-exceeded confirmation are AUTOMATED FROM EVIDENCE, never
/// guessed: a recorder captured a real press as ButtonClick param 0 on the window, and the click
/// replays the button node's OWN registered event (ECommons' production ClickHelper mechanism);
/// the confirmation ticks the SelectYesno's typed ConfirmCheckBox then clicks Yes (force-enabled
/// if still greyed — ECommons' shipped Yes() move). Delivery is verified by the Grand Total
/// clearing to 0. The recorders stay on, cheap, in case a patch changes the events.
/// </summary>
public sealed unsafe class DomanDonator : IDisposable
{
    private const string AddonName = "ReconstructionBox";
    private const uint BudgetNodeId = 21;
    private const uint RateNodeId = 7;
    private const uint GrandTotalNodeId = 25;
    private const uint DonateButtonNodeId = 30;
    private const uint DonateTextRow = 11580; // Addon sheet: "Donate"

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SnapshotLife = TimeSpan.FromMilliseconds(500);

    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly ICondition _condition;
    private readonly Func<bool> _otherOpsBusy;
    private readonly Action<string, bool, string> _completed;
    private readonly IPluginLog _log;

    /// <summary>Whether this character already donated this week (plugin-side, config-backed).</summary>
    private readonly Func<bool> _hasDonatedThisWeek;

    /// <summary>Record that this character's weekly budget is spent (idempotent, plugin-side).</summary>
    private readonly System.Action _recordDonated;

    private enum Phase
    {
        Idle, WaitWindowClosed,
        WaitSplitIssue, // inventory window must be visible before SplitItem lands
        WaitStageMenu,  // the context menu needs a frame to build before clicking
        WaitSplitLanded, WaitStaged,
        WaitDeliver,    // press the window's Donate button (node 30)
        WaitConfirm,    // the budget-exceeded SelectYesno: tick Confirm, then Yes
        WaitDelivered,  // Grand Total back to 0 = the donation went through
    }

    private Phase _phase = Phase.Idle;
    private uint _itemId;
    private long _priceLow;
    private int _target;
    private long _grandTotalAtStage;
    private long _stagedTotal;
    private int _bagCountAtStage;
    private long _budgetAtStage;
    private BagStack _menuStack;
    private bool _menuIssued;
    private DateTime _phaseSinceUtc;

    /// <summary>Window numbers cached for the UI (only readable while the window is open).</summary>
    public sealed record WindowSnapshot(bool Open, long BudgetRemaining, long RatePercent, long GrandTotal);

    private WindowSnapshot _snapshot = new(false, -1, -1, -1);
    private DateTime _snapshotUtc = DateTime.MinValue;

    /// <summary>Event kinds already logged, so the evidence log stays one line per kind.</summary>
    private readonly HashSet<(Dalamud.Game.Addon.Events.AddonEventType Type, int Param)> _loggedEvents = new();
    private readonly HashSet<(Dalamud.Game.Addon.Events.AddonEventType Type, int Param)> _loggedYesnoEvents = new();

    /// <summary>When the last operation ended — the SelectYesno recorder listens for a minute after.</summary>
    private DateTime _lastOpUtc = DateTime.MinValue;

    public DomanDonator(IGameGui gameGui, IDataManager dataManager, IAddonLifecycle addonLifecycle,
        ICondition condition, Func<bool> otherOpsBusy, Func<bool> hasDonatedThisWeek,
        System.Action recordDonated, Action<string, bool, string> completed, IPluginLog log)
    {
        _gameGui = gameGui;
        _dataManager = dataManager;
        _addonLifecycle = addonLifecycle;
        _condition = condition;
        _otherOpsBusy = otherOpsBusy;
        _hasDonatedThisWeek = hasDonatedThisWeek;
        _recordDonated = recordDonated;
        _completed = completed;
        _log = log;

        // The evidence recorders: one manual Donate press (and its Confirm-checkbox dialog) logs
        // the events to replay. Cheap — each unique (type, param) pair is logged once per session,
        // and the SelectYesno recorder only listens while a donation was recently in flight.
        _addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, AddonName, OnWindowEvent);
        _addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "SelectYesno", OnYesnoEvent);
    }

    public bool Busy => _phase != Phase.Idle;

    /// <summary>What it is doing, or why the last run ended how it did — for the Debug line.</summary>
    public string Status { get; private set; } = "idle";

    /// <summary>The last computed donation target, for the UI.</summary>
    public int LastTarget => _target;

    /// <summary>Live Doman state straight from the client — the same source the Timers window
    /// reads. <paramref name="Loaded"/> false = fall back to the local record.</summary>
    public sealed record EnclaveState(bool Loaded, bool AcceptingDonations, int BudgetRemaining, int RatePercent);

    /// <summary>
    /// Read <c>DomanEnclaveManager</c> (ClientStructs): IsAcceptingDonations is the flag behind
    /// Timers' "Currently not accepting donations", Allowance − Donated is the remaining weekly
    /// budget, Factor+100 the rate — all available ANYWHERE, no window and no trip. Fail-open to
    /// not-loaded on any error (a patch moving the sig must degrade to the local record).
    /// </summary>
    public EnclaveState ReadEnclaveState()
    {
        try
        {
            var manager = DomanEnclaveManager.Instance();
            if (manager == null || !manager->IsLoaded)
                return new EnclaveState(false, false, -1, -1);

            var state = manager->State;
            return new EnclaveState(true, state.IsAcceptingDonations,
                Math.Max(0, state.Allowance - state.Donated), state.Factor + 100);
        }
        catch
        {
            return new EnclaveState(false, false, -1, -1);
        }
    }

    /// <summary>
    /// Whether this character can still donate this week. GAME STATE FIRST — it is authoritative
    /// and covers manual donations the local record cannot see (a spent basket refuses to open);
    /// the config record only decides when the manager is not loaded.
    /// </summary>
    public bool DonationAvailable
    {
        get
        {
            var state = ReadEnclaveState();
            return state.Loaded
                ? state.AcceptingDonations && state.BudgetRemaining > 0
                : !_hasDonatedThisWeek();
        }
    }

    /// <summary>This character already spent its weekly budget — skip the trip entirely.</summary>
    public bool DonatedThisWeek => !DonationAvailable;

    /// <summary>
    /// Operator override: mark this week spent by hand. Needed because a spent basket REFUSES to
    /// open (user-verified), so a donation Charon didn't perform can never be learned from the
    /// window — the toon would keep looking available until the reset.
    /// </summary>
    public void MarkWeekSpent() => _recordDonated();

    /// <summary>Live-ish window numbers (500ms cache); Open=false when the basket is closed.</summary>
    public WindowSnapshot GetSnapshot()
    {
        if (DateTime.UtcNow - _snapshotUtc < SnapshotLife)
            return _snapshot;

        _snapshotUtc = DateTime.UtcNow;
        var addon = _gameGui.GetAddonByName(AddonName);
        _snapshot = addon.IsNull || !addon.IsVisible
            ? new WindowSnapshot(false, -1, -1, -1)
            : new WindowSnapshot(true,
                ReadNodeAmount(BudgetNodeId),
                ReadNodeAmount(RateNodeId),
                ReadNodeAmount(GrandTotalNodeId));

        // A window showing zero budget IS the "already donated" fact — record it. This can only
        // fire in the brief moment after OUR delivery empties the budget with the window still
        // up: a spent basket refuses to even open (user-verified), so donations made without
        // Charon are marked by hand instead (MarkWeekSpent).
        if (_snapshot is { Open: true, BudgetRemaining: 0 })
            _recordDonated();

        return _snapshot;
    }

    /// <summary>
    /// Phase 1 (basket OPEN): read budget + rate, close the window, split the exact stack.
    /// Completion says what to do next. True = accepted.
    /// </summary>
    public bool RequestPrepare(uint itemId)
    {
        if (Busy || _otherOpsBusy())
        {
            Status = "refused — another leveling operation is running";
            return false;
        }

        var snapshot = GetSnapshot() with { };
        if (!snapshot.Open)
        {
            Status = "refused — the donation basket window is not open";
            return false;
        }

        if (snapshot.BudgetRemaining <= 0)
        {
            Status = snapshot.BudgetRemaining == 0
                ? "refused — this week's donation budget is spent"
                : "refused — could not read the weekly budget from the window";
            return false;
        }

        var sheet = _dataManager.GetExcelSheet<Item>();
        if (sheet == null || !sheet.TryGetRow(itemId, out var item) || item.PriceLow == 0)
        {
            Status = $"refused — item {itemId} has no vendor value to donate";
            return false;
        }

        _itemId = itemId;
        _priceLow = item.PriceLow;

        var held = GilCapSeller.CountInBags(itemId);
        if (held == 0)
        {
            Status = "refused — none of that item in the bags";
            return false;
        }

        _target = DonationWindowParser.TargetQuantity(
            snapshot.BudgetRemaining, _priceLow, snapshot.RatePercent, held);
        if (_target <= 0)
        {
            Status = "refused — nothing to donate (no budget headroom or no value)";
            return false;
        }

        _log.Info("Doman prepare: budget {0:N0}, rate {1}%, {2} × item {3} (gratuity {4} each) → target {5}",
            snapshot.BudgetRemaining, snapshot.RatePercent, held, itemId,
            DonationWindowParser.UnitGratuity(_priceLow, snapshot.RatePercent), _target);

        // Cross-check the window against DomanEnclaveManager — once these agree in practice the
        // manager can carry the whole read and the window becomes display-only.
        var enclave = ReadEnclaveState();
        if (enclave.Loaded)
            _log.Info("Doman manager says: accepting={0}, remaining {1:N0}, rate {2}% (window said {3:N0} / {4}%)",
                enclave.AcceptingDonations, enclave.BudgetRemaining, enclave.RatePercent,
                snapshot.BudgetRemaining, snapshot.RatePercent);

        // The game refuses splits while the basket is open (user-verified), so close it first.
        // FireCloseCallback is the ESC-equivalent — it ends the interaction properly.
        var addon = _gameGui.GetAddonByName(AddonName);
        if (!addon.IsNull)
            ((AtkUnitBase*)addon.Address)->FireCloseCallback();

        EnterPhase(Phase.WaitWindowClosed);
        Status = $"closing the basket to split {_target}";
        return true;
    }

    /// <summary>
    /// Phase 2 (basket OPEN again, exact stack ready): stage the stack via the Donate context
    /// entry. The final Donate press stays manual this build — see the class doc.
    /// </summary>
    public bool RequestStage(uint itemId)
    {
        if (Busy || _otherOpsBusy())
        {
            Status = "refused — another leveling operation is running";
            return false;
        }

        var snapshot = GetSnapshot();
        if (!snapshot.Open)
        {
            Status = "refused — the donation basket window is not open";
            return false;
        }

        _itemId = itemId;

        // The target is RECOMPUTED LIVE from the open window — never remembered. Remembering it
        // is how 999 bones got staged against a reload-wiped number and paid out only the 20,000
        // remaining budget (~340k of vendor value eaten; the cap varies by reconstruction stage
        // and everything over it is lost). The window is required to be open here, the budget
        // only moves when THIS character donates, so the live numbers are always right.
        var sheet = _dataManager.GetExcelSheet<Item>();
        if (sheet == null || !sheet.TryGetRow(itemId, out var item) || item.PriceLow == 0)
        {
            Status = $"refused — item {itemId} has no vendor value to donate";
            return false;
        }

        if (snapshot.BudgetRemaining <= 0)
        {
            Status = snapshot.BudgetRemaining == 0
                ? "refused — this week's donation budget is spent"
                : "refused — could not read the weekly budget from the window";
            return false;
        }

        var held = GilCapSeller.CountInBags(itemId);
        var target = DonationWindowParser.TargetQuantity(
            snapshot.BudgetRemaining, item.PriceLow, snapshot.RatePercent, held);
        if (target <= 0)
        {
            Status = "refused — nothing to donate (no budget headroom or no value)";
            return false;
        }

        var stacks = InventoryContextMenu.FindStacks(itemId);
        BagStack? pick = null;
        foreach (var stack in stacks)
        {
            if (stack.Quantity == target)
            {
                pick = stack;
                break;
            }

            // Under-target stacks are legal (holding less than the target = donate what exists);
            // prefer the largest of those. Over-target stacks are NEVER staged — the excess is eaten.
            if (stack.Quantity < target && stack.Quantity > (pick?.Quantity ?? 0))
                pick = stack;
        }

        if (pick == null)
        {
            Status = $"refused — no stack of {target} or less to stage; run Prepare to split one";
            return false;
        }

        _bagCountAtStage = GilCapSeller.CountInBags(itemId);
        _budgetAtStage = snapshot.BudgetRemaining;
        _grandTotalAtStage = snapshot.GrandTotal;
        _menuStack = pick.Value;
        _menuIssued = false;
        EnterPhase(Phase.WaitStageMenu);
        Status = $"staging a stack of {pick.Value.Quantity}";
        return true;
    }

    /// <summary>Stop the run (UI Stop button). Safe at any phase.</summary>
    public void Cancel()
    {
        if (Busy)
            Finish(false, "cancelled");
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
                case Phase.WaitWindowClosed:
                    DriveWindowClosed(now);
                    break;
                case Phase.WaitSplitIssue:
                    DriveSplitIssue(now);
                    break;
                case Phase.WaitStageMenu:
                    DriveMenuClick(now, DonateTextRow, Phase.WaitStaged,
                        "no Donate entry on the item's menu (is the basket really open?)");
                    break;
                case Phase.WaitSplitLanded:
                    CheckSplitLanded(now);
                    break;
                case Phase.WaitStaged:
                    CheckStaged(now);
                    break;
                case Phase.WaitDeliver:
                    PressDonate(now);
                    break;
                case Phase.WaitConfirm:
                    DriveConfirmDialog(now);
                    break;
                case Phase.WaitDelivered:
                    CheckDelivered(now);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Doman donation threw");
            Finish(false, "threw (see log)");
        }
    }

    private void DriveWindowClosed(DateTime now)
    {
        var addon = _gameGui.GetAddonByName(AddonName);
        if (!addon.IsNull && addon.IsVisible)
        {
            Timeout(now, "the basket window would not close");
            return;
        }

        // The EVENT outlives the window: the occupied state lingers a beat after the addon is
        // gone, and a split fired in that gap bounces off "Unable to execute command while
        // occupied" (observed in-game). Wait for the flags to clear too.
        if (IsOccupied())
        {
            Timeout(now, "still occupied after the basket closed");
            return;
        }

        // Window gone — split, unless a stack of the right size already exists.
        foreach (var stack in InventoryContextMenu.FindStacks(_itemId))
        {
            if (stack.Quantity == _target)
            {
                Finish(true, $"stack of {_target} already ready — reopen the basket and Stage");
                return;
            }
        }

        BagStack? source = null;
        foreach (var stack in InventoryContextMenu.FindStacks(_itemId))
        {
            if (stack.Quantity > _target)
            {
                source = stack;
                break;
            }
        }

        if (source == null)
        {
            // Nothing big enough to split — everything held is under target, so the whole
            // holding IS the donation (as close as this toon gets; staging is per-stack).
            Finish(true, $"holding less than {_target} — reopen the basket and Stage everything");
            return;
        }

        _menuStack = source.Value;
        EnterPhase(Phase.WaitSplitIssue);
        Status = $"splitting {_target} off a stack of {source.Value.Quantity}";
    }

    /// <summary>Direct SplitItem — the FC chest's own verified split — once the inventory is up.</summary>
    private void DriveSplitIssue(DateTime now)
    {
        if (!InventoryContextMenu.EnsureInventoryOpen())
        {
            Timeout(now, "could not open the inventory window for the split");
            return;
        }

        InventoryContextMenu.SplitStack(_menuStack, _target);
        EnterPhase(Phase.WaitSplitLanded);
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

    private void CheckSplitLanded(DateTime now)
    {
        if (Timeout(now, "the split never landed in the bags"))
            return;

        foreach (var stack in InventoryContextMenu.FindStacks(_itemId))
        {
            if (stack.Quantity == _target)
            {
                Finish(true, $"stack of {_target} ready — reopen the basket and Stage");
                return;
            }
        }
    }

    private void CheckStaged(DateTime now)
    {
        // Staging a unique/untradeable item raises a SelectYesno ("are you certain you wish to
        // donate it?" — user-verified). Answering yes is gated on STATE — we are mid-stage on a
        // stack we chose seconds ago — never on the dialog's text (the auto-accept lesson).
        var yesno = _gameGui.GetAddonByName("SelectYesno");
        if (!yesno.IsNull && yesno.IsVisible)
        {
            var text = ((AtkUnitBase*)yesno.Address)->GetTextNodeById(2) is var node && node != null
                ? node->NodeText.ToString().Replace('\n', ' ')
                : "(unreadable)";
            _log.Info("Doman stage: answering yes to the staging dialog — \"{0}\"", text);
            ((AtkUnitBase*)yesno.Address)->FireCallbackInt(0);
            return; // grand total is checked next tick, after the dialog resolves
        }

        var snapshot = GetSnapshot();
        if (snapshot.Open && snapshot.GrandTotal > Math.Max(0, _grandTotalAtStage))
        {
            // Grand Total = gratuity × staged quantity (user-verified formula) — log the
            // window's math next to ours so any drift is immediately visible.
            var agent = AgentReconstructionBox.Instance();
            if (agent != null)
                _log.Info("Doman staged: Grand Total {0:N0} · agent limited {1:N0} / unlimited {2:N0} · budget was {3:N0}",
                    snapshot.GrandTotal, agent->LimitedTotal, agent->UnlimitedTotal, _snapshot.BudgetRemaining);

            _stagedTotal = snapshot.GrandTotal;
            EnterPhase(Phase.WaitDeliver);
            Status = $"staged {snapshot.GrandTotal:N0} — pressing Donate";
            return;
        }

        Timeout(now, "staging never showed up in the Grand Total");
    }

    /// <summary>
    /// Press the window's Donate button (node 30, from the live dump) by replaying its own
    /// registered event — the recorder showed a real press lands as ButtonClick param 0, which
    /// is exactly what the node's event carries.
    /// </summary>
    private void PressDonate(DateTime now)
    {
        var addon = _gameGui.GetAddonByName(AddonName);
        if (addon.IsNull || !addon.IsVisible)
        {
            Finish(false, "the basket window closed before Donate could be pressed");
            return;
        }

        var button = ((AtkUnitBase*)addon.Address)->GetComponentButtonById(DonateButtonNodeId);
        if (button == null)
        {
            Finish(false, "the Donate button was not where the window dump said");
            return;
        }

        if (!button->IsEnabled)
        {
            Timeout(now, "the Donate button never enabled (nothing staged?)");
            return;
        }

        if (AtkClickHelper.ClickButton((AtkUnitBase*)addon.Address, button))
        {
            EnterPhase(Phase.WaitConfirm);
            Status = "Donate pressed — answering the confirmation";
        }
        else
        {
            Finish(false, "could not press the Donate button (no event on its node)");
        }
    }

    /// <summary>
    /// The budget-exceeded SelectYesno (user-verified: a Confirm checkbox gates the Yes). Tick
    /// the box, then click Yes — force-enabled if still greyed, ECommons' shipped Yes() move.
    /// A donation under budget may raise no dialog at all, so delivery is also checked here.
    /// </summary>
    private void DriveConfirmDialog(DateTime now)
    {
        var yesno = _gameGui.GetAddonByName("SelectYesno");
        if (!yesno.IsNull && yesno.IsVisible)
        {
            var addon = (AddonSelectYesno*)yesno.Address;

            if (addon->ConfirmCheckBox != null
                && addon->ConfirmCheckBox->AtkComponentButton.AtkComponentBase.OwnerNode != null
                && !addon->ConfirmCheckBox->IsChecked)
            {
                AtkClickHelper.ClickCheckBox((AtkUnitBase*)yesno.Address, addon->ConfirmCheckBox);
                Status = "ticked Confirm";
                return; // Yes next tick, once the enable state settles
            }

            AtkClickHelper.ForceEnable(addon->YesButton);
            if (AtkClickHelper.ClickButton((AtkUnitBase*)yesno.Address, addon->YesButton))
            {
                EnterPhase(Phase.WaitDelivered);
                Status = "confirmed — waiting for the donation to land";
            }

            return;
        }

        // No dialog: an under-budget donation may deliver straight away.
        if (HasDelivered())
        {
            FinishDelivered();
            return;
        }

        Timeout(now, "no confirmation dialog appeared and nothing was delivered");
    }

    private void CheckDelivered(DateTime now)
    {
        if (HasDelivered())
        {
            FinishDelivered();
            return;
        }

        Timeout(now, "the donation never landed (the items are still in the bags)");
    }

    /// <summary>
    /// The honest delivery signal is the ITEMS LEAVING THE BAGS — the window may close or keep
    /// its Grand Total after delivery (a real donation was once reported FAILED on the old
    /// Grand-Total check while the chat said "You donate…"), but the bags cannot lie.
    /// </summary>
    private bool HasDelivered() => GilCapSeller.CountInBags(_itemId) < _bagCountAtStage;

    private void FinishDelivered()
    {
        _target = 0; // consumed — the next donation must re-Prepare against the live budget

        // A PARTIAL donation (held less than the target) leaves budget on the table — the week
        // is only "done" when the staged gratuity covered what remained.
        if (_stagedTotal >= _budgetAtStage)
            _recordDonated();

        _log.Info("Doman delivered: {0:N0} gratuity donated", _stagedTotal);
        Finish(true, $"delivered — {_stagedTotal:N0} gratuity donated");
    }

    private bool IsOccupied() =>
        _condition[ConditionFlag.Occupied]
        || _condition[ConditionFlag.OccupiedInEvent]
        || _condition[ConditionFlag.OccupiedInQuestEvent]
        || _condition[ConditionFlag.Occupied30]
        || _condition[ConditionFlag.Occupied33]
        || _condition[ConditionFlag.Occupied38]
        || _condition[ConditionFlag.Occupied39];

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
        _lastOpUtc = DateTime.UtcNow;
        Status = ok ? $"done — {detail}" : $"FAILED — {detail}";
        _completed("domanDonate", ok, detail);
    }

    private long ReadNodeAmount(uint nodeId)
    {
        var addon = _gameGui.GetAddonByName(AddonName);
        if (addon.IsNull)
            return -1;

        var node = ((AtkUnitBase*)addon.Address)->GetTextNodeById(nodeId);
        return node == null ? -1 : DonationWindowParser.ParseAmount(node->NodeText.ToString());
    }

    /// <summary>The evidence recorder — one line per unique event kind the window receives.</summary>
    private void OnWindowEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs receive)
            return;

        var key = (receive.AtkEventType, receive.EventParam);
        if (!_loggedEvents.Add(key))
            return;

        _log.Info("ReconstructionBox event: type {0} param {1} — evidence for wiring the Donate press",
            receive.AtkEventType, receive.EventParam);
    }

    /// <summary>
    /// Records the Confirm-checkbox dialog's events (the budget-exceeded SelectYesno needs its
    /// checkbox ticked before Yes — user-verified). Only while a donation ran recently, so
    /// unrelated dialogs elsewhere in the game don't muddy the evidence.
    /// </summary>
    private void OnYesnoEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs receive)
            return;

        if (_phase == Phase.Idle && DateTime.UtcNow - _lastOpUtc > TimeSpan.FromSeconds(60))
            return;

        var key = (receive.AtkEventType, receive.EventParam);
        if (!_loggedYesnoEvents.Add(key))
            return;

        _log.Info("SelectYesno event: type {0} param {1} — evidence for the Confirm+Yes press",
            receive.AtkEventType, receive.EventParam);
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, AddonName, OnWindowEvent);
        _addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "SelectYesno", OnYesnoEvent);
    }
}
