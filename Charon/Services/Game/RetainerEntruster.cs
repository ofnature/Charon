using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Diagnostics;
using Charon.Features.Retainers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// "Entrust duplicates to every retainer": a round at the bell that opens each retainer worth visiting
/// (<see cref="EntrustRound"/>) and presses the GAME'S OWN Entrust Duplicates for her — the game then
/// moves every bag item she already holds a stack of. Charon moves no item itself.
///
/// The window navigation is not here: this names the retainer it wants (<see cref="WantedRetainer"/>)
/// and the bell selector — the cycle proven live for Refresh all — opens her bags, then closes them
/// and quits once she is done. This only acts while HER BAGS ARE OPEN, three buttons in order.
///
/// THE GAME'S FLOW, from AutoRetainer's field-proven handlers (BSD-3: <c>RetainerHandlers</c>
/// ClickEntrustDuplicates / ClickEntrustDuplicatesConfirm / ClickCloseEntrustWindow), facts only:
///   1. her inventory window (<c>InventoryRetainerLarge</c>, button at node-list index 8, else
///      <c>InventoryRetainer</c>, index 5) — callback 0, updateState false, while the button is visible
///      AND enabled. A disabled button means she holds nothing you are carrying: she is done;
///   2. <c>RetainerItemTransferList</c> — the game's own list of what will move — Confirm, node index 3;
///   3. <c>RetainerItemTransferProgress</c> — Close Window (node index 2) once the transfer has finished,
///      recognised by its label being Addon row 13530 "Close Window".
/// Every window read is IsReady-guarded first: an access violation from a window mid-setup cannot be caught.
/// </summary>
public sealed unsafe class RetainerEntruster
{
    private const string TransferList = "RetainerItemTransferList";
    private const string TransferProgress = "RetainerItemTransferProgress";
    private const uint AddonRowCloseWindow = 13530;

    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PerRetainerLimit = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BellGoneGrace = TimeSpan.FromSeconds(8);

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.Crystals,
    ];

    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly ICondition _condition;
    private readonly IPluginLog _log;
    private readonly RetainerReader _retainers;
    private readonly Func<IReadOnlyList<RetainerBag>> _snapshots;
    private readonly Func<ulong> _contentId;
    private readonly Func<string?> _openRetainer;
    private readonly StepTrace _trace = new(capacity: 16);

    private readonly List<string> _visited = new();
    private string? _lastWanted;
    private string? _working;
    private DateTime _workingSinceUtc;
    private DateTime _nextBeatUtc;
    private DateTime _bellLastSeenUtc;
    private int _entrusted;
    private string _closeText = string.Empty;

    public RetainerEntruster(
        IGameGui gameGui, IDataManager data, ICondition condition, IPluginLog log, RetainerReader retainers,
        Func<IReadOnlyList<RetainerBag>> snapshots, Func<ulong> contentId, Func<string?> openRetainer)
    {
        _gameGui = gameGui;
        _data = data;
        _condition = condition;
        _log = log;
        _retainers = retainers;
        _snapshots = snapshots;
        _contentId = contentId;
        _openRetainer = openRetainer;
    }

    public bool Armed { get; private set; }

    public string Status { get; private set; } = "idle";

    public StepTrace Trace => _trace;

    /// <summary>
    /// The retainer the round wants open next, or null. Re-planned from live state on every ask, and
    /// a reloading roster (it reads empty while a retainer is dismissed) answers with the last target —
    /// a transient nothing is never "nobody left".
    /// </summary>
    public string? WantedRetainer
    {
        get
        {
            if (!Armed)
                return null;

            var names = _retainers.Read(DateTime.UtcNow).Select(r => r.Name).ToList();
            if (names.Count == 0)
                return _lastWanted;

            _lastWanted = EntrustRound.Next(Plan(names), _visited);
            return _lastWanted;
        }
    }

    /// <summary>Who a round would visit right now — for the button's tooltip before it is pressed.</summary>
    public IReadOnlyList<EntrustVisit> Preview() =>
        Plan(_retainers.Read(DateTime.UtcNow).Select(r => r.Name).ToList());

    public bool Start(DateTime nowUtc)
    {
        if (Preview().Count == 0)
        {
            Status = "nothing to entrust — no retainer holds anything you are carrying";
            return false;
        }

        Armed = true;
        _visited.Clear();
        _lastWanted = null;
        _working = null;
        _entrusted = 0;
        _bellLastSeenUtc = nowUtc;
        _trace.Clear();
        _trace.Record(DateTime.Now, "Start");
        Status = "entrusting duplicates — at a bell";
        return true;
    }

    public void Stop(string reason = "stopped")
    {
        if (!Armed)
            return;

        Armed = false;
        _working = null;
        _trace.Record(DateTime.Now, $"Stop — {reason}");
        Status = reason;
    }

    public void Update(DateTime nowUtc)
    {
        if (!Armed || nowUtc < _nextBeatUtc)
            return;
        _nextBeatUtc = nowUtc + Beat;

        if (_condition[ConditionFlag.OccupiedSummoningBell])
            _bellLastSeenUtc = nowUtc;
        else if (nowUtc - _bellLastSeenUtc > BellGoneGrace)
        {
            Stop(_entrusted > 0 ? $"left the bell — entrusted to {_entrusted}" : "left the bell");
            return;
        }

        var open = _openRetainer();
        if (open == null)
        {
            // Nothing open and nobody left to want: the round is complete.
            if (WantedRetainer == null)
            {
                Stop(_entrusted > 0 ? $"done — entrusted to {_entrusted} retainer(s)" : "done — nothing needed entrusting");
                return;
            }

            _trace.Note(DateTime.Now, $"wait:{WantedRetainer}", $"(waiting for {WantedRetainer}'s bags)");
            Status = $"waiting for {WantedRetainer} — open her at the bell, or turn on At the bell";
            return;
        }

        if (_visited.Contains(open))
            return; // done with her; the selector is closing up

        if (_working != open)
        {
            _working = open;
            _workingSinceUtc = nowUtc;
        }
        else if (nowUtc - _workingSinceUtc > PerRetainerLimit)
        {
            Finish(open, $"gave up on {open}: the entrust never finished", counted: false);
            return;
        }

        Step(open);
    }

    private void Step(string retainer)
    {
        // 3. The transfer is running or done: close it once its button says "Close Window".
        var progress = Ready(TransferProgress);
        if (progress != null)
        {
            var node = Node(progress, 2);
            var button = node == null ? null : node->GetAsAtkComponentButton();
            if (button != null && node->IsVisible() && button->IsEnabled && ButtonText(node) == CloseText())
            {
                AtkClickHelper.ClickButton(progress, button);
                _trace.Record(DateTime.Now, $"CloseProgress({retainer})");
                Finish(retainer, $"{retainer} done", counted: true);
            }
            else
            {
                Status = $"entrusting to {retainer}…";
            }

            return;
        }

        // 2. The game's own list of what will move: confirm it.
        var list = Ready(TransferList);
        if (list != null)
        {
            var node = Node(list, 3);
            var button = node == null ? null : node->GetAsAtkComponentButton();
            if (button != null && node->IsVisible() && button->IsEnabled)
            {
                AtkClickHelper.ClickButton(list, button);
                _trace.Record(DateTime.Now, $"Confirm({retainer})");
            }

            return;
        }

        // 1. Her bags: press Entrust Duplicates — or, greyed out, she holds nothing you carry.
        var inventory = Ready("InventoryRetainerLarge");
        var index = 8;
        if (inventory == null)
        {
            inventory = Ready("InventoryRetainer");
            index = 5;
        }

        if (inventory == null)
            return;

        var entrust = Node(inventory, index);
        var entrustButton = entrust == null ? null : entrust->GetAsAtkComponentButton();
        if (entrustButton == null || !entrust->IsVisible())
            return;

        if (!entrustButton->IsEnabled)
        {
            Finish(retainer, $"{retainer}: nothing of yours to entrust", counted: false);
            return;
        }

        inventory->FireCallbackInt(0);
        _trace.Record(DateTime.Now, $"EntrustDuplicates({retainer})");
        Status = $"entrusting to {retainer}…";
    }

    private void Finish(string retainer, string line, bool counted)
    {
        _visited.Add(retainer);
        _working = null;
        if (counted)
            _entrusted++;
        _trace.Record(DateTime.Now, line);
        Status = line;
    }

    /// <summary>Retainers worth visiting, from each one's last snapshot against what the bags hold now.</summary>
    private IReadOnlyList<EntrustVisit> Plan(IReadOnlyList<string> names)
    {
        var prefix = $"{_contentId()}:";
        var snapshots = _snapshots()
            .Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(b => b.Name, b => (IReadOnlyCollection<uint>)b.Stacks.Select(s => s.ItemId).ToList());
        return EntrustRound.Plan(names, snapshots, BagItems());
    }

    private static HashSet<uint> BagItems()
    {
        var items = new HashSet<uint>();
        var manager = InventoryManager.Instance();
        if (manager == null)
            return items;

        foreach (var type in PlayerBags)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId != 0)
                    items.Add(slot->ItemId);
            }
        }

        return items;
    }

    private AtkUnitBase* Ready(string name)
    {
        var unit = (AtkUnitBase*)_gameGui.GetAddonByName(name).Address;
        return unit != null && unit->IsReady && unit->IsVisible ? unit : null;
    }

    private static AtkResNode* Node(AtkUnitBase* unit, int index) =>
        index < unit->UldManager.NodeListCount ? unit->UldManager.NodeList[index] : null;

    /// <summary>The label on a button component (its text node at component node-list index 2).</summary>
    private static string ButtonText(AtkResNode* node)
    {
        var component = node->GetComponent();
        if (component == null || component->UldManager.NodeListCount <= 2)
            return string.Empty;

        var text = component->UldManager.NodeList[2]->GetAsAtkTextNode();
        return text == null ? string.Empty : Dalamud.Utility.Utf8StringExtensions.ExtractText(text->NodeText);
    }

    private string CloseText()
    {
        if (_closeText.Length > 0)
            return _closeText;

        try
        {
            var sheet = _data.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
            if (sheet != null && sheet.TryGetRow(AddonRowCloseWindow, out var row))
                _closeText = row.Text.ExtractText();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Entrust] addon row {0} unreadable", AddonRowCloseWindow);
        }

        return _closeText;
    }
}
