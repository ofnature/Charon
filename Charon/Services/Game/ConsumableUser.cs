using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Charon.Features.Consumables;

namespace Charon.Services.Game;

/// <summary>
/// Uses the bag items that exist only to be consumed — the MGP card family today. They are
/// untradable and unsellable, so they are dead weight until used, which is the whole argument for
/// having this at all.
///
/// Gates are <see cref="CollectionScanner"/>'s, deliberately: out of combat AND not occupied, one
/// use per 1.5s, and an item the game refuses is skipped for the session so a single stubborn
/// stack can never pin the loop. Delivery is judged by THE STACK SHRINKING, never by UseAction's
/// return value — the same rule the FC chest and the Doman donator run on, because the call
/// reporting success proves only that it was accepted, not that anything happened.
/// </summary>
public sealed unsafe class ConsumableUser
{
    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>The bag scan resolves a sheet row per item — too heavy for every UI frame.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);

    /// <summary>Using items back-to-back competes with the game's own item lock.</summary>
    private static readonly TimeSpan UsePacing = TimeSpan.FromSeconds(1.5);

    private readonly IDataManager _dataManager;
    private readonly ICondition _condition;
    private readonly Func<bool> _autoEnabled;
    private readonly IPluginLog _log;

    private readonly HashSet<uint> _refused = new();
    private List<ConsumableItem>? _cache;
    private DateTime _cacheUtc = DateTime.MinValue;
    private DateTime _lastUseUtc = DateTime.MinValue;

    /// <summary>The stack we last used from, and how many it held — the delivery check.</summary>
    private uint _pendingItemId;
    private int _pendingQuantity;

    public ConsumableUser(IDataManager dataManager, ICondition condition, Func<bool> autoEnabled, IPluginLog log)
    {
        _dataManager = dataManager;
        _condition = condition;
        _autoEnabled = autoEnabled;
        _log = log;
    }

    /// <summary>What it is doing, or why it is not — surfaced in the Debug section.</summary>
    public string Status { get; private set; } = "idle";

    /// <summary>Everything worth using right now. Safe to call from draw code.</summary>
    public IReadOnlyList<ConsumableItem> GetUsable()
    {
        if (_cache != null && DateTime.UtcNow - _cacheUtc < CacheLifetime)
            return _cache;

        _cache = ConsumablePolicy.Usable(ReadBags());
        _cacheUtc = DateTime.UtcNow;
        return _cache;
    }

    public long TotalValue() => ConsumablePolicy.TotalValue(GetUsable());

    public void Invalidate() => _cache = null;

    /// <summary>Clears the session's refusals, so a Refresh really does retry everything.</summary>
    public void ResetRefusals()
    {
        _refused.Clear();
        Invalidate();
    }

    /// <summary>
    /// The automatic path, off unless the toggle says otherwise. It only ever reaches for the next
    /// item the policy picked, so turning it on cannot do anything the button would not.
    /// </summary>
    public void Update(DateTime nowUtc)
    {
        ConfirmDelivery();

        if (!_autoEnabled())
        {
            Status = GetUsable().Count == 0 ? "idle — nothing to use" : $"off — {Summary()}";
            return;
        }

        if (_condition[ConditionFlag.InCombat])
        {
            Status = "waiting — in combat";
            return;
        }

        if (Occupied)
        {
            Status = "waiting — busy";
            return;
        }

        if (nowUtc - _lastUseUtc < UsePacing)
            return;

        var next = ConsumablePolicy.Next(GetUsable(), _refused);
        if (next == null)
        {
            Status = _refused.Count > 0
                ? $"idle — nothing left ({_refused.Count} refused this session)"
                : "idle — nothing to use";
            return;
        }

        _lastUseUtc = nowUtc;
        TryUse(next);
    }

    /// <summary>Uses one stack. The per-item button and the automatic path share this.</summary>
    public bool TryUse(ConsumableItem item)
    {
        try
        {
            if (_condition[ConditionFlag.InCombat])
            {
                Status = "waiting — in combat";
                return false;
            }

            var manager = ActionManager.Instance();
            if (manager == null)
            {
                Status = "ActionManager unavailable";
                return false;
            }

            // Remember what the stack held so the next pass can tell whether anything happened.
            _pendingItemId = item.ItemId;
            _pendingQuantity = item.Quantity;

            var accepted = manager->UseAction(ActionType.Item, item.ItemId, 0, 65535);
            Invalidate();

            Status = accepted ? $"used {item.Name}" : $"the game refused {item.Name}";
            if (!accepted)
            {
                _refused.Add(item.ItemId);
                _pendingItemId = 0;
            }

            _log.Info("Consumables: UseAction(Item, {0}) -> {1}", item.ItemId, accepted);
            return accepted;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Consumable use threw");
            Status = "threw (see log)";
            return false;
        }
    }

    /// <summary>
    /// Did the last use actually consume anything? A stack that is unchanged after an accepted call
    /// is a silent refusal, and the item is skipped for the session rather than retried forever.
    /// </summary>
    private void ConfirmDelivery()
    {
        if (_pendingItemId == 0 || DateTime.UtcNow - _lastUseUtc < UsePacing)
            return;

        var held = 0;
        foreach (var item in ReadBags())
        {
            if (item.ItemId == _pendingItemId)
                held += item.Quantity;
        }

        if (held >= _pendingQuantity)
        {
            _refused.Add(_pendingItemId);
            _log.Info("Consumables: {0} did not leave the bags — skipping it this session", _pendingItemId);
        }

        _pendingItemId = 0;
    }

    private string Summary() => ConsumablePolicy.Summarize(GetUsable(), TotalValue());

    private bool Occupied =>
        _condition[ConditionFlag.Occupied]
        || _condition[ConditionFlag.OccupiedInEvent]
        || _condition[ConditionFlag.OccupiedInQuestEvent]
        || _condition[ConditionFlag.OccupiedSummoningBell]
        || _condition[ConditionFlag.Occupied30]
        || _condition[ConditionFlag.Occupied33]
        || _condition[ConditionFlag.Occupied38]
        || _condition[ConditionFlag.Occupied39]
        || _condition[ConditionFlag.BetweenAreas]
        || _condition[ConditionFlag.OccupiedInCutSceneEvent]
        || _condition[ConditionFlag.WatchingCutscene]
        || _condition[ConditionFlag.Casting]
        || _condition[ConditionFlag.Mounted];

    private List<ConsumableItem> ReadBags()
    {
        var items = new List<ConsumableItem>();
        var sheet = _dataManager.GetExcelSheet<Item>();
        if (sheet == null)
            return items;

        foreach (var bag in PlayerBags)
        {
            try
            {
                var container = InventoryManager.Instance()->GetInventoryContainer(bag);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    if (!sheet.TryGetRow(slot->ItemId, out var row))
                        continue;

                    var action = row.ItemAction;
                    if (action.RowId == 0)
                        continue;

                    var kind = action.Value.Action.RowId;
                    if (!ConsumableKinds.Known.Contains(kind))
                        continue;

                    items.Add(new ConsumableItem(
                        slot->ItemId,
                        row.Name.ExtractText(),
                        kind,
                        slot->Quantity,
                        row.IsUntradable,
                        action.Value.Data[0],
                        (int)bag,
                        (short)i));
                }
            }
            catch
            {
                // container unreadable mid-transition — skip it
            }
        }

        return items;
    }
}
