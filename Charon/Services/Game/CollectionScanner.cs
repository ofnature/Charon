using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Charon.Features.Loot;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>
/// Finds collectibles sitting unlearned in the bags and learns one on request. Thin unsafe adapter;
/// the filtering is <see cref="CollectiblePolicy"/> (pure).
///
/// These pile up with no looting involved — MSQ rewards, trust runs and AutoDuty runs hand items
/// straight over — so an unattended toon can hold a stack of unlearned minions and cards for weeks.
///
/// Learning is per-item and explicit: nothing is consumed without a click. The unlock check is
/// type-agnostic (it asks the game, not a hardcoded table), so it covers mounts, minions, cards,
/// orchestrion rolls, emotes and hairstyles alike without needing to classify them first.
/// </summary>
public sealed unsafe class CollectionScanner
{
    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>The bag scan resolves a sheet row per item — too heavy for every UI frame.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);

    private readonly IDataManager _dataManager;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IPluginLog _log;

    private List<CollectibleItem>? _cache;
    private DateTime _cacheUtc = DateTime.MinValue;
    private bool _loggedCategories;

    /// <summary>
    /// Raw IsItemActionUnlocked values seen, with a sample item each. Logged once per session so the
    /// value→meaning mapping comes from observation: assuming it was a bool is what broke this.
    /// </summary>
    private readonly Dictionary<long, string> _unlockStatesSeen = new();

    public CollectionScanner(IDataManager dataManager, IClientState clientState, ICondition condition, IPluginLog log)
    {
        _dataManager = dataManager;
        _clientState = clientState;
        _condition = condition;
        _log = log;
    }

    /// <summary>What it is doing, or why it is not — surfaced in the Debug section.</summary>
    public string Status { get; private set; } = "idle";

    /// <summary>Unlearned collectibles in the bags. Read-only and safe to call from draw code.</summary>
    public IReadOnlyList<CollectibleItem> GetUnlearned()
    {
        if (_cache != null && DateTime.UtcNow - _cacheUtc < CacheLifetime)
            return _cache;

        var all = ReadBagCollectibles();
        _cache = CollectiblePolicy.Unlearned(all);
        _cacheUtc = DateTime.UtcNow;

        // Logged once per session so the allowlist grows from observed values, not guesses.
        if (!_loggedCategories && all.Count > 0)
        {
            _loggedCategories = true;

            // The value→meaning mapping, straight from the bags. 1 is being read as owned; anything
            // else as not-owned. If that is backwards this log says so immediately.
            foreach (var (state, sample) in _unlockStatesSeen)
                _log.Info("IsItemActionUnlocked returned {0} · e.g. '{1}'", state, sample);

            // Unrecognised ItemAction kinds. Mounts, Triple Triad cards and emotes are EXPECTED here
            // until their values are observed — add them to CollectibleKinds.Known once seen. Junk
            // (potions, tickets) will also appear, which is exactly why the filter is an allowlist.
            foreach (var unknown in CollectiblePolicy.UnknownKinds(all))
                _log.Info("Unrecognised ItemAction kind {0} · '{1}' [{2}]",
                    unknown.ActionKind, unknown.Name, unknown.Category);
        }

        Status = _cache.Count == 0 ? "nothing unlearned in bags" : $"{_cache.Count} unlearned in bags";
        return _cache;
    }

    /// <summary>
    /// Whether an item we do NOT own yet is a recognised collectible, and whether its unlock is
    /// already earned. Used by loot rolling so "already unlocked" means the same thing there as in
    /// the Collect list.
    /// </summary>
    public (bool IsCollectible, bool AlreadyUnlocked, bool IsTradeable) AssessForLoot(uint itemId)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<Item>();
            if (sheet == null || !sheet.TryGetRow(itemId, out var row) || row.ItemAction.RowId == 0)
                return (false, false, false);

            var kind = row.ItemAction.Value.Action.RowId;
            if (!CollectibleKinds.Known.Contains(kind))
                return (false, false, !row.IsUntradable);

            var state = ReadUnlockState(itemId);
            return (true, state == UnlockOwned, !row.IsUntradable);
        }
        catch
        {
            return (false, false, false);
        }
    }

    /// <summary>Drop the cache (an item was just learned, or the user asked for a refresh).</summary>
    public void Invalidate() => _cache = null;

    // --- Auto-collect: the toggle, driven from the framework tick ---

    /// <summary>One use per this interval — the item has to actually leave the bags before the
    /// next pick, and using items back-to-back competes with the game's own item lock.</summary>
    private static readonly TimeSpan AutoCollectPacing = TimeSpan.FromSeconds(1.5);

    private DateTime _lastAutoCollectUtc = DateTime.MinValue;

    /// <summary>
    /// Items the game refused (UseAction returned false) — skipped for the session so a single
    /// stubborn item can never pin the loop. Cleared on Refresh so a retry is one click away.
    /// </summary>
    private readonly HashSet<uint> _autoRefused = new();

    /// <summary>What auto-collect did last, or why it is idle — for the Collect and Debug lines.</summary>
    public string AutoStatus { get; private set; } = "off";

    /// <summary>
    /// Learn the next safe collectible unprompted. Out of combat only (the item queue belongs to
    /// the rotation there); never touches the manual-only kinds — an unlearned Antique Lantern
    /// is worth ~1.8M and collecting consumes it, so that stays a deliberate click.
    /// </summary>
    public void UpdateAutoCollect(DateTime now, bool enabled)
    {
        if (!enabled)
        {
            AutoStatus = "off";
            return;
        }

        if (_condition[ConditionFlag.InCombat])
        {
            AutoStatus = "waiting — in combat";
            return;
        }

        if (now - _lastAutoCollectUtc < AutoCollectPacing)
            return;

        var next = CollectiblePolicy.NextAutoCollect(
            GetUnlearned().Where(i => !_autoRefused.Contains(i.ItemId)), _clientState.TerritoryType);
        if (next == null)
        {
            var manualLeft = GetUnlearned().Count(i => !CollectibleKinds.IsAutoCollectSafe(i.ActionKind));
            AutoStatus = manualLeft > 0
                ? $"idle — {manualLeft} left for manual Collect (sellable kinds)"
                : "idle — nothing safe to collect";
            return;
        }

        _lastAutoCollectUtc = now;
        var kind = CollectibleKinds.Describe(next.ActionKind);
        if (TryCollect(next.ItemId, next.ActionKind, highQuality: false))
        {
            AutoStatus = $"collected '{next.Name}' ({kind})";
            _log.Info("Auto-collect: '{0}' ({1}, item {2})", next.Name, kind, next.ItemId);
        }
        else
        {
            _autoRefused.Add(next.ItemId);
            AutoStatus = $"'{next.Name}' refused — skipped for this session";
        }
    }

    /// <summary>Forget the session's refusals so Refresh gives auto-collect another go.</summary>
    public void ResetAutoRefusals() => _autoRefused.Clear();

    /// <summary>
    /// Whether this item can be used where we are standing. Phantom job shards only work in the
    /// Occult Crescent, so they are listed everywhere but only actionable there.
    /// </summary>
    public bool CanCollectHere(CollectibleItem item)
    {
        try
        {
            return CollectiblePolicy.CanCollectHere(item, _clientState.TerritoryType);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Learn one collectible by using the item. Out of combat only — using items competes with the
    /// rotation for the action queue, the same reason Heal Watch stands down.
    /// </summary>
    public bool TryCollect(uint itemId, uint actionKind, bool highQuality)
    {
        try
        {
            if (_condition[ConditionFlag.InCombat])
            {
                Status = "waiting — in combat";
                return false;
            }

            if (!CollectibleKinds.CanCollectHere(actionKind, _clientState.TerritoryType))
            {
                Status = "that one only works in the Occult Crescent";
                return false;
            }

            var manager = ActionManager.Instance();
            if (manager == null)
            {
                Status = "ActionManager unavailable";
                return false;
            }

            // HQ items are addressed as itemId + 1,000,000 (the same convention the gearset module
            // uses, where Charon already strips it with % 1000000).
            var actionId = highQuality ? itemId + 1_000_000 : itemId;
            var used = manager->UseAction(ActionType.Item, actionId, 0, 65535);

            Invalidate();
            Status = used ? $"used item {itemId}" : $"item {itemId} refused";
            _log.Info("Collect: UseAction(Item, {0}) -> {1}", actionId, used);
            return used;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Collect failed for item {0}", itemId);
            Status = "collect threw (see log)";
            return false;
        }
    }

    /// <summary>
    /// Every bag item the game treats as an unlockable collectible, with its unlock state.
    /// Items with no ItemAction are ordinary goods and never appear.
    /// </summary>
    private List<CollectibleItem> ReadBagCollectibles()
    {
        var items = new List<CollectibleItem>();
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
                        continue; // not an unlockable item at all

                    // Anything the game doesn't track an unlock for (potions, materia, gear) is not a
                    // collectible and must never be offered for consumption.
                    var state = ReadUnlockState(slot->ItemId);
                    if (state == UnlockNotTracked || state < 0)
                        continue;

                    var name = row.Name.ExtractText();
                    _unlockStatesSeen.TryAdd(state, name);

                    items.Add(new CollectibleItem(
                        slot->ItemId,
                        name,
                        row.ItemUICategory.ValueNullable?.Name.ExtractText() ?? "—",
                        action.Value.Action.RowId,
                        state == UnlockOwned,
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

    /// <summary>Not unlock-tracked at all — an ordinary usable item, never listed.</summary>
    private const uint UnlockNotTracked = 0;

    /// <summary>Already earned.</summary>
    private const uint UnlockOwned = 1;

    /// <summary>
    /// Ask the GAME whether this item's unlock has been earned, rather than maintaining a table per
    /// collectible type. Returns the RAW value: this is tri-state, not a bool — treating it as
    /// "non-zero means unlocked" hid every genuinely unlearned item, because locked returns 2.
    /// -1 on any failure, which is treated as "leave it alone".
    /// </summary>
    private long ReadUnlockState(uint itemId)
    {
        try
        {
            var itemRow = FFXIVClientStructs.FFXIV.Component.Exd.ExdModule.GetItemRowById(itemId);
            if (itemRow == null)
                return -1;

            return UIState.Instance()->IsItemActionUnlocked(itemRow);
        }
        catch
        {
            return -1;
        }
    }
}
