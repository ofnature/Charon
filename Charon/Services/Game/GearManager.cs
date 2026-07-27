using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Charon.Features.Gear;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>One executed gear step, for the section's log.</summary>
public sealed record GearLogEntry(string Name, string Detail);

/// <summary>
/// One item in the armoury-cleanup preview, aggregated across its stacks. <paramref name="Kept"/>
/// items stay put — they are still listed so the veto can be undone. <paramref name="ExpBonus"/>
/// is non-empty for EXP gear, which explains why it came pre-ticked.
/// </summary>
public sealed record CleanupRow(uint ItemId, string Name, int StackCount, bool Kept, string ExpBonus);

/// <summary>
/// Gear scanning + equipping. Thin unsafe adapter around InventoryManager; the selection and
/// cleanup decisions are pure (<see cref="GearSelector"/>, <see cref="ArmouryCleanupPlanner"/>).
///
/// Equipping goes BAGS → ARMOURY → EQUIPPED, never bag→equipped directly: equipping out of the
/// armoury makes the game swap the displaced piece back into that armoury slot, so the main bags
/// stay clear for loot. An upgrade sitting in a bag therefore costs two moves.
///
/// Every step is RE-PLANNED from live containers (never a replayed batch — that is exactly how
/// the prior art desyncs when inventory shifts mid-pass), paced one move per 250ms, and verified
/// before the next one starts.
/// </summary>
public sealed unsafe class GearManager
{
    private static readonly TimeSpan MovePacing = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan PassTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The same upgrade re-planned this many times without landing = give up (never loop).</summary>
    private const int MaxAttemptsPerUpgrade = 3;

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>Armoury containers in slot order — also the cleanup sweep order.</summary>
    private static readonly InventoryType[] ArmouryContainers =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IDataManager _dataManager;
    private readonly ICondition _condition;
    private readonly IPluginLog _log;
    private readonly Func<bool> _includeMainBags;
    private readonly Func<bool> _updateGearsetAfterPass;
    private readonly Func<IReadOnlyCollection<uint>> _keepItemIds;

    // Job-eligibility answers are stable per (category, job) — the lookup behind them is reflection.
    private readonly Dictionary<(uint Category, uint Job), bool> _jobFitCache = new();

    // Equip pass state.
    private bool _passRunning;
    private DateTime _passDeadlineUtc;
    private DateTime _lastMoveUtc = DateTime.MinValue;
    private int _equipped;
    private readonly List<GearLogEntry> _operationLog = new();

    /// <summary>Move submitted last tick, awaiting verification.</summary>
    private PendingMove? _inFlight;
    private DateTime _inFlightDeadlineUtc;

    /// <summary>Which upgrade we are pushing, and how many times we have re-planned it.</summary>
    private (uint ItemId, GearSlot Slot) _currentTarget;
    private int _targetAttempts;

    /// <summary>Upgrades that refused to equip this pass — set aside so the rest still get done.</summary>
    private readonly HashSet<(uint ItemId, GearSlot Slot)> _skipped = new();

    // Armoury cleanup state (its own simple queue — no re-planning needed, nothing shifts under it).
    private readonly Queue<ArmouryItem> _cleanupQueue = new();
    private int _cleanupMoved;

    // Draw-time caches. The window redraws its tables every frame and every scan walks a dozen
    // containers with sheet lookups — far too heavy at frame rate.
    private static readonly TimeSpan PreviewCacheLifetime = TimeSpan.FromMilliseconds(500);
    private List<CleanupRow>? _cleanupPreviewCache;
    private DateTime _cleanupPreviewCacheUtc = DateTime.MinValue;
    private List<GearUpgrade>? _upgradePreviewCache;
    private DateTime _upgradePreviewCacheUtc = DateTime.MinValue;

    /// <summary>What a submitted move was for — decides how it is verified and logged.</summary>
    private enum MoveKind
    {
        /// <summary>Armoury → equipped. Verified on the destination slot.</summary>
        Equip,

        /// <summary>Bag → armoury, staging an upgrade. Silent: the equip that follows reports it.</summary>
        Stage,

        /// <summary>Armoury → bag, evicting a non-gearset item.</summary>
        Cleanup,
    }

    /// <summary>A submitted move: verified by re-reading the destination (equip) or source (the rest).</summary>
    private sealed record PendingMove(uint ItemId, string Name, GearSlot Slot, MoveKind Kind, int SrcContainer, short SrcSlot);

    public GearManager(
        IClientState clientState,
        IObjectTable objectTable,
        IDataManager dataManager,
        ICondition condition,
        Func<bool> includeMainBags,
        Func<bool> updateGearsetAfterPass,
        Func<IReadOnlyCollection<uint>> keepItemIds,
        IPluginLog log)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _dataManager = dataManager;
        _condition = condition;
        _includeMainBags = includeMainBags;
        _updateGearsetAfterPass = updateGearsetAfterPass;
        _keepItemIds = keepItemIds;
        _log = log;
    }

    /// <summary>Human-readable state for the Debug section — says what it is doing, or why it is not.</summary>
    public string Status { get; private set; } = "idle";

    /// <summary>Summary of the last completed pass ("Equipped 4 pieces (+52 ilvl)").</summary>
    public string LastOperation { get; private set; } = "";

    /// <summary>Per-step results of the newest run only.</summary>
    public IReadOnlyList<GearLogEntry> OperationLog => _operationLog;

    public bool Busy => _passRunning || _cleanupQueue.Count > 0 || _inFlight != null;

    // --- Read-only scanning (preview + the IPC count gate) ---

    /// <summary>Every upgrade available right now. Read-only — safe to call from IPC at any time.</summary>
    public List<GearUpgrade> GetUpgrades()
    {
        try
        {
            var local = _objectTable.LocalPlayer;
            if (local == null)
                return new List<GearUpgrade>();

            var jobId = local.ClassJob.RowId;
            var (race, sex) = ReadLocalAppearance(); // read once — it cannot change mid-scan
            var candidates = ReadCandidates(jobId, race, sex);
            return GearSelector.Plan(ReadEquipped(jobId, race, sex), candidates, local.Level);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Gear scan threw");
            return new List<GearUpgrade>();
        }
    }

    /// <summary>Armoury stacks that would actually be moved out — keep-list items excluded.</summary>
    public List<ArmouryItem> GetCleanupPlan()
    {
        try
        {
            return ArmouryCleanupPlanner.Plan(ReadArmouryStacks(), ReadGearsetItemIds(), _keepItemIds());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Armoury cleanup scan threw");
            return new List<ArmouryItem>();
        }
    }

    /// <summary>
    /// The cleanup preview, one row per item: everything no gearset references, INCLUDING kept
    /// items (so the veto can be undone). Cached briefly — the window redraws this every frame and
    /// each scan walks a dozen containers.
    /// </summary>
    public IReadOnlyList<CleanupRow> GetCleanupPreview()
    {
        if (_cleanupPreviewCache != null && DateTime.UtcNow - _cleanupPreviewCacheUtc < PreviewCacheLifetime)
            return _cleanupPreviewCache;

        try
        {
            var kept = new HashSet<uint>(_keepItemIds());
            _cleanupPreviewCache = ArmouryCleanupPlanner
                .Unregistered(ReadArmouryStacks(), ReadGearsetItemIds())
                .GroupBy(i => i.ItemId)
                .Select(g => new CleanupRow(g.Key, g.First().Name, g.Count(), kept.Contains(g.Key),
                    ExpBonusItems.BonusFor(g.Key)))
                .OrderBy(r => r.Kept)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Armoury cleanup preview threw");
            _cleanupPreviewCache = new List<CleanupRow>();
        }

        _cleanupPreviewCacheUtc = DateTime.UtcNow;
        return _cleanupPreviewCache;
    }

    /// <summary>Drop the cached previews (the keep list changed, or an item just moved).</summary>
    public void InvalidatePreview()
    {
        _cleanupPreviewCache = null;
        _upgradePreviewCache = null;
    }

    /// <summary>
    /// Upgrade list for the window. Cached like the cleanup preview — the EXECUTOR deliberately
    /// does not use this: it re-plans from live containers before every single step.
    /// </summary>
    public IReadOnlyList<GearUpgrade> GetUpgradePreview()
    {
        if (_upgradePreviewCache != null && DateTime.UtcNow - _upgradePreviewCacheUtc < PreviewCacheLifetime)
            return _upgradePreviewCache;

        _upgradePreviewCache = GetUpgrades();
        _upgradePreviewCacheUtc = DateTime.UtcNow;
        return _upgradePreviewCache;
    }

    // --- Equip pass ---

    /// <summary>
    /// Start an equip pass. Returns false when refused — the caller (SealBreaker) then falls back
    /// to the game's Equip Recommended rather than waiting on us.
    /// </summary>
    public bool StartEquipPass()
    {
        if (Busy)
        {
            Status = "refused — already running";
            return false;
        }

        if (!CanAct(out var reason))
        {
            Status = $"refused — {reason}";
            return false;
        }

        if (GetUpgrades().Count == 0)
        {
            Status = "nothing to equip";
            LastOperation = "Gear: already wearing the best available";
            return true; // "nothing to do" is success, not a refusal
        }

        _operationLog.Clear();
        _equipped = 0;
        _targetAttempts = 0;
        _currentTarget = default;
        _skipped.Clear();
        _inFlight = null;
        _passRunning = true;
        _passDeadlineUtc = DateTime.UtcNow + PassTimeout;
        Status = "starting equip pass";
        return true;
    }

    /// <summary>Queue the armoury cleanup (manual button only). Returns moves queued.</summary>
    public int StartArmouryCleanup()
    {
        if (Busy || !CanAct(out _))
            return 0;

        var plan = GetCleanupPlan();
        _operationLog.Clear();
        _cleanupMoved = 0;
        foreach (var item in plan)
            _cleanupQueue.Enqueue(item);

        if (plan.Count == 0)
        {
            Status = "idle";
            LastOperation = "Armoury cleanup: nothing to remove";
        }
        else
        {
            Status = $"cleanup: {plan.Count} queued";
        }

        return plan.Count;
    }

    /// <summary>Drive the active pass. Call every framework tick.</summary>
    public void Update(DateTime nowUtc)
    {
        if (!Busy)
            return;

        if (nowUtc - _lastMoveUtc < MovePacing)
            return;
        _lastMoveUtc = nowUtc;

        if (_inFlight != null)
        {
            if (!TryVerifyInFlight(nowUtc))
                return; // still settling
        }

        if (_cleanupQueue.Count > 0)
        {
            DriveCleanup();
            return;
        }

        if (_passRunning)
            DriveEquipPass(nowUtc);
    }

    /// <summary>
    /// One step of the equip pass: re-plan against live containers, then either stage the next
    /// upgrade into its armoury container (bag source) or equip it (armoury source).
    /// </summary>
    private void DriveEquipPass(DateTime nowUtc)
    {
        if (nowUtc > _passDeadlineUtc)
        {
            FinishPass("timed out");
            return;
        }

        // Combat/duty can start mid-pass — stop rather than fight the game for the action queue.
        if (!CanAct(out var reason))
        {
            FinishPass($"aborted — {reason}");
            return;
        }

        // Anything that refused to equip is set aside for the rest of this pass, so one stubborn
        // item cannot strand the upgrades behind it (a race-locked piece used to abort everything).
        var upgrades = GetUpgrades()
            .Where(u => !_skipped.Contains((u.Item.ItemId, u.Slot)))
            .ToList();

        if (upgrades.Count == 0)
        {
            FinishPass("done");
            return;
        }

        var next = upgrades[0];
        var target = (next.Item.ItemId, next.Slot);
        if (target == _currentTarget)
        {
            // Same upgrade still outstanding after a completed step — it is not landing. Skip it
            // and carry on with the others rather than abandoning the pass.
            if (++_targetAttempts >= MaxAttemptsPerUpgrade)
            {
                _operationLog.Add(new GearLogEntry(next.Item.Name, "SKIPPED — would not equip"));
                _log.Warning("Gear: {0} would not equip into {1} — skipping it", next.Item.Name, next.Slot);
                _skipped.Add(target);
                _currentTarget = default;
                _targetAttempts = 0;
                return;
            }
        }
        else
        {
            _currentTarget = target;
            _targetAttempts = 0;
        }

        var armoury = ArmouryContainerFor(next.Slot);
        if (next.Item.Container == (int)armoury)
            SubmitEquip(next, armoury);
        else
            SubmitStageToArmoury(next, armoury);
    }

    /// <summary>Armoury → equipped. The displaced piece swaps back into the vacated armoury slot.</summary>
    private void SubmitEquip(GearUpgrade upgrade, InventoryType armoury)
    {
        try
        {
            InventoryManager.Instance()->MoveItemSlot(
                armoury, (ushort)upgrade.Item.SourceSlot,
                InventoryType.EquippedItems, (ushort)(int)upgrade.Slot, true);

            _inFlight = new PendingMove(upgrade.Item.ItemId, upgrade.Item.Name, upgrade.Slot,
                MoveKind.Equip, (int)armoury, upgrade.Item.SourceSlot);
            _inFlightDeadlineUtc = DateTime.UtcNow + StepTimeout;
            Status = $"equipping {upgrade.Item.Name} ({upgrade.Slot})";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Gear equip move threw ({0})", upgrade.Item.Name);
            FinishPass("aborted — equip call failed");
        }
    }

    /// <summary>Bag → armoury, so the equip that follows leaves the displaced piece out of the bags.</summary>
    private void SubmitStageToArmoury(GearUpgrade upgrade, InventoryType armoury)
    {
        var free = FindFreeSlot([armoury], out _);
        if (free < 0)
        {
            _operationLog.Add(new GearLogEntry(upgrade.Item.Name, "SKIPPED — armoury full"));
            FinishPass($"aborted — {armoury} is full");
            return;
        }

        try
        {
            InventoryManager.Instance()->MoveItemSlot(
                (InventoryType)upgrade.Item.Container, (ushort)upgrade.Item.SourceSlot,
                armoury, (ushort)free, true);

            _inFlight = new PendingMove(upgrade.Item.ItemId, upgrade.Item.Name, upgrade.Slot,
                MoveKind.Stage, upgrade.Item.Container, upgrade.Item.SourceSlot);
            _inFlightDeadlineUtc = DateTime.UtcNow + StepTimeout;
            Status = $"moving {upgrade.Item.Name} to the armoury";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Gear staging move threw ({0})", upgrade.Item.Name);
            FinishPass("aborted — armoury move failed");
        }
    }

    private void DriveCleanup()
    {
        var item = _cleanupQueue.Dequeue();

        var free = FindFreeSlot(PlayerBags, out var bag);
        if (free < 0)
        {
            _cleanupQueue.Clear();
            Status = "cleanup aborted — bags full";
            LastOperation = $"Armoury cleanup: moved {_cleanupMoved} before the bags filled up";
            return;
        }

        try
        {
            InventoryManager.Instance()->MoveItemSlot(
                (InventoryType)item.Container, (ushort)item.Slot, bag, (ushort)free, true);

            _inFlight = new PendingMove(item.ItemId, item.Name, GearSlot.MainHand,
                MoveKind.Cleanup, item.Container, item.Slot);
            _inFlightDeadlineUtc = DateTime.UtcNow + StepTimeout;
            Status = $"cleanup: {item.Name} → bags ({_cleanupQueue.Count} left)";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Armoury cleanup move threw ({0})", item.Name);
            _operationLog.Add(new GearLogEntry(item.Name, "FAILED"));
        }

        if (_cleanupQueue.Count == 0 && _inFlight == null)
            FinishCleanup();
    }

    /// <summary>
    /// An equip is confirmed by the DESTINATION (the slot now holds the item); a staging/cleanup
    /// move by the SOURCE emptying. Both are server round trips, so this waits out its deadline
    /// rather than judging on the first look (learned from the FC chest work: a 250ms snapshot
    /// reports "still in place" on moves that landed fine).
    /// </summary>
    private bool TryVerifyInFlight(DateTime nowUtc)
    {
        var move = _inFlight!;
        var landed = move.Kind == MoveKind.Equip
            ? ReadSlotItemId(InventoryType.EquippedItems, (short)(int)move.Slot) == move.ItemId
            : ReadSlotItemId((InventoryType)move.SrcContainer, move.SrcSlot) != move.ItemId;

        if (!landed && nowUtc < _inFlightDeadlineUtc)
            return false;

        _inFlight = null;
        InvalidatePreview(); // containers just changed under the window

        switch (move.Kind)
        {
            case MoveKind.Equip:
                if (landed)
                {
                    _equipped++;
                    _operationLog.Add(new GearLogEntry(move.Name, $"equipped ({move.Slot})"));
                    _log.Info("Gear: equipped {0} in {1}", move.Name, move.Slot);
                }
                else
                {
                    _operationLog.Add(new GearLogEntry(move.Name, "FAILED — slot unchanged"));
                }
                break;

            case MoveKind.Cleanup:
                if (landed)
                    _cleanupMoved++;
                _operationLog.Add(new GearLogEntry(move.Name, landed ? "moved to bags" : "FAILED"));
                if (_cleanupQueue.Count == 0)
                    FinishCleanup();
                break;

            case MoveKind.Stage:
                // Silent by design — the equip step that follows reports the outcome. A staging
                // move that did not land just re-plans from the bag next tick.
                break;
        }

        return true;
    }

    private void FinishPass(string status)
    {
        _passRunning = false;
        _inFlight = null;
        Status = status;

        // Skips are part of the result, not a footnote — an item the game refuses is the single
        // most useful thing to surface (race-locked gear, for instance).
        var skipped = _skipped.Count > 0
            ? $", {_skipped.Count} skipped (would not equip)"
            : string.Empty;

        LastOperation = _equipped == 0
            ? $"Gear: nothing equipped ({status}{skipped})"
            : $"Equipped {_equipped} {(_equipped == 1 ? "piece" : "pieces")} ({status}{skipped})";

        if (_equipped > 0 && _updateGearsetAfterPass())
            TryUpdateCurrentGearset();
    }

    private void FinishCleanup()
    {
        Status = "idle";
        LastOperation = $"Armoury cleanup: moved {_cleanupMoved} "
                        + $"{(_cleanupMoved == 1 ? "item" : "items")} to the bags";
    }

    /// <summary>Save the new gear onto the active gearset so it survives a job swap.</summary>
    private void TryUpdateCurrentGearset()
    {
        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return;

            var index = module->CurrentGearsetIndex;
            if (module->IsValidGearset(index))
                module->UpdateGearset(index);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Gearset update after equip pass threw");
        }
    }

    // --- Guards ---

    /// <summary>Never equip mid-fight, mid-duty, or mid-transition — refuse and say why.</summary>
    private bool CanAct(out string reason)
    {
        try
        {
            if (!_clientState.IsLoggedIn || _objectTable.LocalPlayer == null)
                reason = "not logged in";
            else if (_condition[ConditionFlag.InCombat])
                reason = "in combat";
            else if (_condition[ConditionFlag.BoundByDuty])
                reason = "bound by duty";
            else if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
                reason = "zoning";
            else if (_condition[ConditionFlag.Occupied] || _condition[ConditionFlag.OccupiedInEvent]
                     || _condition[ConditionFlag.OccupiedInCutSceneEvent])
                reason = "occupied";
            else if (_condition[ConditionFlag.Casting])
                reason = "casting";
            else
                reason = string.Empty;
        }
        catch
        {
            reason = "game state unreadable";
        }

        return reason.Length == 0;
    }

    // --- Container reads ---

    private Dictionary<GearSlot, GearItem?> ReadEquipped(uint jobId, byte race, byte sex)
    {
        var equipped = new Dictionary<GearSlot, GearItem?>();

        foreach (GearSlot slot in Enum.GetValues<GearSlot>())
        {
            var itemId = ReadSlotItemId(InventoryType.EquippedItems, (short)(int)slot);
            equipped[slot] = itemId == 0 ? null : BuildItem(itemId, jobId, slot, -1, -1, race, sex);
        }

        return equipped;
    }

    /// <summary>Armoury always; the main bags too unless the "armoury only" checkbox is set.</summary>
    private List<GearItem> ReadCandidates(uint jobId, byte race, byte sex)
    {
        var containers = _includeMainBags()
            ? ArmouryContainers.Concat(PlayerBags)
            : ArmouryContainers.AsEnumerable();

        var items = new List<GearItem>();
        foreach (var container in containers)
        {
            try
            {
                var inventory = InventoryManager.Instance()->GetInventoryContainer(container);
                if (inventory == null || !inventory->IsLoaded)
                    continue;

                for (var i = 0; i < inventory->Size; i++)
                {
                    var slot = inventory->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    var item = BuildItem(slot->ItemId, jobId, null, (int)container, (short)i, race, sex);
                    if (item != null)
                        items.Add(item);
                }
            }
            catch
            {
                // container unreadable mid-transition — skip it (selection just sees fewer options)
            }
        }

        return items;
    }

    private List<ArmouryItem> ReadArmouryStacks()
    {
        var items = new List<ArmouryItem>();
        var containers = ArmouryContainers.Append(InventoryType.ArmorySoulCrystal);

        foreach (var container in containers)
        {
            try
            {
                var inventory = InventoryManager.Instance()->GetInventoryContainer(container);
                if (inventory == null || !inventory->IsLoaded)
                    continue;

                for (var i = 0; i < inventory->Size; i++)
                {
                    var slot = inventory->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    items.Add(new ArmouryItem(slot->ItemId, ResolveItemName(slot->ItemId),
                        (int)container, (short)i, container == InventoryType.ArmorySoulCrystal));
                }
            }
            catch
            {
                // skip unreadable container
            }
        }

        return items;
    }

    /// <summary>Every item id referenced by every saved gearset — the cleanup's keep-list.</summary>
    private List<uint> ReadGearsetItemIds()
    {
        var ids = new List<uint>();

        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return ids;

            for (var i = 0; i < module->NumGearsets; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var gearset = module->GetGearset(i);
                if (gearset == null)
                    continue;

                foreach (var item in gearset->Items)
                {
                    // Gearset item ids carry HQ/glamour flags in the high bits; the container
                    // ids we compare against do not.
                    var id = item.ItemId % 1000000;
                    if (id != 0)
                        ids.Add(id);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Gearset read threw");
            return new List<uint>(); // empty = the planner refuses to evict anything
        }

        return ids;
    }

    private uint ReadSlotItemId(InventoryType container, short slot)
    {
        try
        {
            var inventory = InventoryManager.Instance()->GetInventoryContainer(container);
            if (inventory == null || !inventory->IsLoaded || slot < 0 || slot >= inventory->Size)
                return 0;

            var item = inventory->GetInventorySlot(slot);
            return item == null ? 0 : item->ItemId;
        }
        catch
        {
            return 0;
        }
    }

    private int FindFreeSlot(IReadOnlyList<InventoryType> candidates, out InventoryType type)
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

    // --- Sheet lookups ---

    /// <summary>
    /// Sheet row → the pure model. <paramref name="forcedSlot"/> pins equipped items to the slot
    /// they are actually worn in (a ring's sheet row cannot say which hand).
    /// Returns null for anything that is not equippable gear for this job's slots.
    /// </summary>
    private GearItem? BuildItem(
        uint itemId, uint jobId, GearSlot? forcedSlot, int container, short sourceSlot, byte race, byte sex)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<Item>();
            if (sheet == null || !sheet.TryGetRow(itemId, out var row))
                return null;

            var slot = forcedSlot ?? SlotOf(row.EquipSlotCategory.Value);
            if (slot == null)
                return null;

            return new GearItem(
                itemId,
                row.Name.ExtractText(),
                slot.Value,
                (int)row.LevelItem.RowId,
                row.LevelEquip,
                FitsJob(row.ClassJobCategory.RowId, jobId),
                row.IsUnique,
                row.EquipSlotCategory.Value.OffHand != 0,
                StatScore(row, jobId),
                container,
                sourceSlot,
                StatsFitJob(row, jobId),
                HasJobMainStat(row, jobId),
                FitsRace(row, race, sex));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The slot an item occupies (the column set to 1). Rings report RingRight.</summary>
    private static GearSlot? SlotOf(EquipSlotCategory category)
    {
        if (category.MainHand == 1) return GearSlot.MainHand;
        if (category.OffHand == 1) return GearSlot.OffHand;
        if (category.Head == 1) return GearSlot.Head;
        if (category.Body == 1) return GearSlot.Body;
        if (category.Gloves == 1) return GearSlot.Hands;
        if (category.Waist == 1) return GearSlot.Waist;
        if (category.Legs == 1) return GearSlot.Legs;
        if (category.Feet == 1) return GearSlot.Feet;
        if (category.Ears == 1) return GearSlot.Ears;
        if (category.Neck == 1) return GearSlot.Neck;
        if (category.Wrists == 1) return GearSlot.Wrists;
        if (category.FingerR == 1 || category.FingerL == 1) return GearSlot.RingRight;
        return null; // soul crystal or not equippable
    }

    /// <summary>
    /// Whether a ClassJobCategory admits this job. The sheet stores one bool column per job,
    /// named by the job's abbreviation, so the lookup is reflective — cached per (category, job)
    /// pair, which makes it a handful of lookups per session rather than per item.
    /// </summary>
    private bool FitsJob(uint categoryId, uint jobId)
    {
        if (_jobFitCache.TryGetValue((categoryId, jobId), out var cached))
            return cached;

        var fits = false;
        try
        {
            var jobs = _dataManager.GetExcelSheet<ClassJob>();
            var categories = _dataManager.GetExcelSheet<ClassJobCategory>();
            if (jobs != null && categories != null
                && jobs.TryGetRow(jobId, out var job)
                && categories.TryGetRow(categoryId, out var category))
            {
                var abbreviation = job.Abbreviation.ExtractText();
                var property = typeof(ClassJobCategory).GetProperty(abbreviation);
                if (property?.GetValue(category) is bool allowed)
                    fits = allowed;
            }
        }
        catch
        {
            fits = false; // unknown = do not equip it
        }

        _jobFitCache[(categoryId, jobId)] = fits;
        return fits;
    }

    // BaseParam row ids (verified against the sheet, not assumed).
    private const uint ParamStrength = 1;
    private const uint ParamDexterity = 2;
    private const uint ParamIntelligence = 4;
    private const uint ParamMind = 5;
    private const uint ParamGatheringPoints = 10;  // GP
    private const uint ParamCraftingPoints = 11;   // CP
    private const uint ParamCraftsmanship = 70;
    private const uint ParamControl = 71;
    private const uint ParamGathering = 72;
    private const uint ParamPerception = 73;
    private const uint ParamVitality = 3;
    private const uint ParamPiety = 6;
    private const uint ParamTenacity = 19;
    private const uint ParamDirectHit = 22;
    private const uint ParamCriticalHit = 27;
    private const uint ParamDetermination = 44;
    private const uint ParamSkillSpeed = 45;
    private const uint ParamSpellSpeed = 46;

    /// <summary>
    /// "Main Attribute" / "Secondary Attribute": adaptive stats on gear that scales to whatever job
    /// you are playing (the EXP-bonus earrings use them). They stand in for the job's own main stat
    /// and a substat — without these, such items score near zero and lose every comparison.
    /// </summary>
    private const uint ParamMainAttribute = 55;
    private const uint ParamSecondaryAttribute = 56;

    // ClassJob.Role: 1 = tank, 2 = melee DPS, 3 = ranged/caster DPS, 4 = healer (verified).
    private const byte RoleTank = 1;
    private const byte RoleHealer = 4;

    /// <summary>
    /// Whether this character's race and sex may wear the item. Starting gear is frequently locked
    /// to one race and sex ("Roegadyn Bodice" is female-Roegadyn only) yet still reads as "All
    /// Classes" at equip level 1 — so on a fresh alt with empty slots it looks like an ideal fit,
    /// and the game then refuses the equip without saying anything.
    ///
    /// The EquipRaceCategory rows are NOT ordered by race id (16-19 run Hrothgar M, Viera F,
    /// Viera M, Hrothgar F), so the booleans are read rather than computed. Restriction 0/1 mean
    /// unrestricted, and anything unreadable fails OPEN.
    /// </summary>
    private bool FitsRace(Item row, byte race, byte sex)
    {
        try
        {
            var restriction = row.EquipRestriction.RowId;
            if (restriction <= 1)
                return true; // 0 = none recorded, 1 = all races and sexes

            var sheet = _dataManager.GetExcelSheet<EquipRaceCategory>();
            if (sheet == null || !sheet.TryGetRow(restriction, out var category))
                return true;

            // Race sheet ids: 1 Hyur, 2 Elezen, 3 Lalafell, 4 Miqo'te, 5 Roegadyn, 6 Au Ra,
            // 7 Hrothgar, 8 Viera.
            var raceAllowed = race switch
            {
                1 => category.Hyur,
                2 => category.Elezen,
                3 => category.Lalafell,
                4 => category.Miqote,
                5 => category.Roegadyn,
                6 => category.AuRa,
                7 => category.Hrothgar,
                8 => category.Viera,
                _ => true, // unknown race — do not block on a guess
            };

            var sexAllowed = sex == 0 ? category.Male : category.Female;
            return raceAllowed && sexAllowed;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Local character's race and sex (sex: 0 male, 1 female). (0, 0) when unreadable.</summary>
    private (byte Race, byte Sex) ReadLocalAppearance()
    {
        try
        {
            var local = _objectTable.LocalPlayer;
            if (local == null || local.Address == 0)
                return (0, 0);

            var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)local.Address;
            var customize = character->DrawData.CustomizeData;
            return (customize.Race, customize.Sex);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Whether an item's STATS belong to this job, which is a separate question from whether the
    /// game lets the job equip it. Gathering and crafting gear is frequently in the "All Classes"
    /// category (row 1 is true for PLD and MIN alike — verified), so a combat job passes the
    /// category gate on a ring statted for Perception and GP, and its item level then wins the
    /// ranking outright. That is how a gathering piece gets swapped onto a Paladin.
    ///
    /// The test is the presence of a DoL/DoH-exclusive stat — those never appear on combat gear, so
    /// it cannot reject a real combat piece. Gatherer and crafter jobs are left alone entirely
    /// (Charon does not rank their gear), and anything unreadable fails OPEN, keeping today's
    /// behaviour rather than silently emptying the candidate list.
    /// </summary>
    private bool StatsFitJob(Item row, uint jobId)
    {
        try
        {
            var jobs = _dataManager.GetExcelSheet<ClassJob>();
            if (jobs == null || !jobs.TryGetRow(jobId, out var job))
                return true;

            // DoH/DoL jobs have no primary combat stat (verified: PrimaryStat 0, Role 0).
            if (job.PrimaryStat == 0)
                return true;

            for (var i = 0; i < row.BaseParam.Count; i++)
            {
                if (row.BaseParamValue[i] == 0)
                    continue;

                switch (row.BaseParam[i].RowId)
                {
                    case ParamGathering:
                    case ParamPerception:
                    case ParamGatheringPoints:
                    case ParamCraftsmanship:
                    case ParamControl:
                    case ParamCraftingPoints:
                        return false; // gatherer/crafter gear — not for a combat job
                }
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Whether this item's MAIN stat is the one this job uses. False only when the item carries a
    /// main stat belonging to a different job — that is dead weight, however high the item level.
    ///
    /// Accessories are the trap: many are "All Classes" with a role-specific main stat, so the game
    /// lets a Paladin wear Augmented Shire Conservator's Choker (ilvl 270, Dexterity 47) and
    /// ilvl-first ranking would take it over a lower-ilvl Strength piece.
    ///
    /// Neutral cases return TRUE so nothing legitimate is downranked: adaptive "Main Attribute"
    /// gear (the EXP earrings), items with no main stat at all, and anything unreadable.
    /// </summary>
    private bool HasJobMainStat(Item row, uint jobId)
    {
        try
        {
            var jobs = _dataManager.GetExcelSheet<ClassJob>();
            if (jobs == null || !jobs.TryGetRow(jobId, out var job))
                return true;

            uint primary = job.PrimaryStat;
            if (primary == 0)
                return true; // DoH/DoL — not ranked by main stat at all

            var carriesAMainStat = false;
            for (var i = 0; i < row.BaseParam.Count; i++)
            {
                if (row.BaseParamValue[i] == 0)
                    continue;

                var param = row.BaseParam[i].RowId;
                if (param == primary || param == ParamMainAttribute)
                    return true; // the job's own stat (or an adaptive one) — good

                if (param is ParamStrength or ParamDexterity or ParamIntelligence or ParamMind)
                    carriesAMainStat = true;
            }

            // Carries someone else's main stat and not ours; no main stat at all is neutral.
            return !carriesAMainStat;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// How good this piece is for the job. Main stat dominates by an order of magnitude — it always
    /// does in practice — with the offensive substats next and the role-specific ones (tenacity for
    /// tanks, piety for healers) only counted where they mean anything. A stat the job cannot use
    /// scores ZERO, which is what keeps caster gear off a tank at equal item level.
    ///
    /// Deliberately unopinionated: no per-job speed breakpoints or tier tables. This ranks gear for
    /// leveling alts and casual cap play, not for optimizing a specific rotation. Materia is not
    /// counted (these are the item's base values).
    ///
    /// Only ever compares pieces at the SAME item level — see <see cref="GearSelector"/>.
    /// </summary>
    private int StatScore(Item row, uint jobId)
    {
        try
        {
            var jobs = _dataManager.GetExcelSheet<ClassJob>();
            if (jobs == null || !jobs.TryGetRow(jobId, out var job))
                return 0;

            uint primary = job.PrimaryStat;
            var role = job.Role;

            var score = 0;
            for (var i = 0; i < row.BaseParam.Count; i++)
            {
                var param = row.BaseParam[i].RowId;
                var value = (int)row.BaseParamValue[i];
                if (value == 0)
                    continue;

                score += param switch
                {
                    _ when param == primary => value * 10,
                    ParamMainAttribute => value * 10,
                    ParamCriticalHit or ParamDetermination or ParamDirectHit => value * 3,
                    ParamSecondaryAttribute => value * 3,
                    ParamTenacity when role == RoleTank => value * 3,
                    ParamPiety when role == RoleHealer => value * 2,
                    ParamSkillSpeed or ParamSpellSpeed => value,
                    ParamVitality => value,
                    _ => 0, // a stat this job cannot use is worth nothing
                };
            }

            return score;
        }
        catch
        {
            return 0;
        }
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

    private static InventoryType ArmouryContainerFor(GearSlot slot) => slot switch
    {
        GearSlot.MainHand => InventoryType.ArmoryMainHand,
        GearSlot.OffHand => InventoryType.ArmoryOffHand,
        GearSlot.Head => InventoryType.ArmoryHead,
        GearSlot.Body => InventoryType.ArmoryBody,
        GearSlot.Hands => InventoryType.ArmoryHands,
        GearSlot.Waist => InventoryType.ArmoryWaist,
        GearSlot.Legs => InventoryType.ArmoryLegs,
        GearSlot.Feet => InventoryType.ArmoryFeets,
        GearSlot.Ears => InventoryType.ArmoryEar,
        GearSlot.Neck => InventoryType.ArmoryNeck,
        GearSlot.Wrists => InventoryType.ArmoryWrist,
        _ => InventoryType.ArmoryRings,
    };
}
