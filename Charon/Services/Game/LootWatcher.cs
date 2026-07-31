using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Charon.Features.Loot;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>One pending loot roll, with what the rules would do about it.</summary>
public sealed record LootRollPreview(uint ItemId, string Name, RollAction Action, string Reason);

/// <summary>
/// Watches the loot window and works out what Charon WOULD roll — without rolling anything.
///
/// Deliberately read-only for now. The rules themselves are pure and tested, but item resolution
/// and the roll callbacks are exactly the sort of thing that has cost a round of wrong assumptions
/// twice in this project already (/leader placeholders, IsItemActionUnlocked's tri-state). Watching
/// one duty's worth of real drops proves the resolution before anything becomes clickable — and a
/// wrong guess here would mean hitting Need on someone else's drop, which is not recoverable with
/// an apology.
/// </summary>
public sealed unsafe class LootWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IDataManager _dataManager;
    private readonly GearManager _gear;
    private readonly CollectionScanner _collection;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _canSell;
    private readonly Func<bool> _strangersInParty;
    private readonly Func<int> _passBelowIlvlGap;
    private readonly IPluginLog _log;

    private DateTime _lastPollUtc = DateTime.MinValue;
    private List<LootRollPreview> _pending = new();

    /// <summary>Items already logged this session, so a window sitting open doesn't spam.</summary>
    private readonly HashSet<uint> _logged = new();

    public LootWatcher(
        IDataManager dataManager,
        GearManager gear,
        CollectionScanner collection,
        Func<bool> enabled,
        Func<bool> canSell,
        Func<bool> strangersInParty,
        Func<int> passBelowIlvlGap,
        IPluginLog log)
    {
        _dataManager = dataManager;
        _gear = gear;
        _collection = collection;
        _enabled = enabled;
        _canSell = canSell;
        _strangersInParty = strangersInParty;
        _passBelowIlvlGap = passBelowIlvlGap;
        _log = log;
    }

    /// <summary>What it is seeing right now — surfaced in the Debug section.</summary>
    public string Status { get; private set; } = "no loot pending";

    /// <summary>Pending rolls and the decision each would get. Read-only.</summary>
    public IReadOnlyList<LootRollPreview> Pending => _pending;

    /// <summary>Poll the loot window. Call every framework tick.</summary>
    public void Update(DateTime nowUtc)
    {
        if (nowUtc - _lastPollUtc < PollInterval)
            return;
        _lastPollUtc = nowUtc;

        try
        {
            _pending = ReadPending();
            Status = _pending.Count == 0
                ? "no loot pending"
                : $"{_pending.Count} item(s) pending — WOULD {_pending[0].Action} '{_pending[0].Name}'";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Loot watch threw");
            Status = "loot watch threw (see log)";
        }
    }

    private List<LootRollPreview> ReadPending()
    {
        var rows = new List<LootRollPreview>();
        var loot = Loot.Instance();
        if (loot == null)
            return rows;

        var sheet = _dataManager.GetExcelSheet<Item>();
        var context = new LootContext(
            _enabled(), _canSell(), _strangersInParty(), _passBelowIlvlGap());

        foreach (ref var entry in loot->Items)
        {
            var itemId = entry.ItemId;
            if (itemId == 0)
                continue;

            var name = sheet != null && sheet.TryGetRow(itemId, out var row)
                ? row.Name.ExtractText()
                : $"item {itemId}";

            var (isCollectible, alreadyUnlocked, tradeable) = _collection.AssessForLoot(itemId);
            var gear = _gear.AssessForLoot(itemId);

            // Fully qualified: ClientStructs has its own LootItem for the window's own rows.
            var item = new Charon.Features.Loot.LootItem(
                itemId,
                name,
                isCollectible,
                alreadyUnlocked,
                tradeable,
                gear.IsGear,
                gear.CanEquip,
                gear.IsUpgrade,
                gear.WorseThanEquipped,
                gear.ItemLevelsBelowEquipped,
                gear.IsGlamour,
                HasWeeklyLockout: false);

            var decision = LootRollPolicy.Evaluate(item, context);
            rows.Add(new LootRollPreview(itemId, name, decision.Action, decision.Reason));

            // One line per item per session — enough to check the rules against real drops without
            // filling the log while a window sits open.
            if (_logged.Add(itemId))
            {
                _log.Info("Loot: '{0}' (item {1}) → WOULD {2} · {3}", name, itemId, decision.Action, decision.Reason);
                _log.Info("      collectible={0} owned={1} tradeable={2} · gear={3} canEquip={4} upgrade={5} worse={6} below={7}",
                    isCollectible, alreadyUnlocked, tradeable,
                    gear.IsGear, gear.CanEquip, gear.IsUpgrade, gear.WorseThanEquipped, gear.ItemLevelsBelowEquipped);
            }
        }

        return rows;
    }
}
