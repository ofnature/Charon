using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Charon.Features.DeepDungeon;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>
/// Walk within reach of a treasure chest and it opens itself. Ported from PandorasBox's
/// AutoOpenChests (BSD-3-Clause, PunishXIV/PandorasBox) — the gates are theirs, production-
/// verified: only ObjectKind.Treasure, only targetable, skip chests whose flags say Opened or
/// FadedOut, skip chests already on the loot window (ChestObjectId match), and never in
/// high-end duties. Charon additions: out-of-combat only (bots must not interact mid-fight),
/// and the interact goes through the same InteractHelper the follow portals use.
/// </summary>
public sealed unsafe class ChestOpener
{
    private static readonly TimeSpan ScanThrottle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan InteractCooldown = TimeSpan.FromSeconds(1);

    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IDataManager _dataManager;
    private readonly InteractHelper _interact;
    private readonly Func<bool> _enabled;
    private readonly Func<float> _openRange;
    private readonly IPluginLog _log;

    private DateTime _lastScanUtc = DateTime.MinValue;
    private DateTime _lastOpenUtc = DateTime.MinValue;

    /// <summary>Coffers we have already reached for, by entity id — see <see cref="TryOpenCoffer"/>.</summary>
    private readonly Dictionary<uint, DateTime> _cofferTouched = new();
    private static readonly TimeSpan CofferRetry = TimeSpan.FromSeconds(10);

    public ChestOpener(IObjectTable objectTable, ICondition condition, IDataManager dataManager,
        InteractHelper interact, Func<bool> enabled, Func<float> openRange, IPluginLog log)
    {
        _objectTable = objectTable;
        _condition = condition;
        _dataManager = dataManager;
        _interact = interact;
        _enabled = enabled;
        _openRange = openRange;
        _log = log;
    }

    public string Status { get; private set; } = "off";

    public void Update(DateTime now)
    {
        if (!_enabled())
        {
            Status = "off";
            return;
        }

        if (now - _lastScanUtc < ScanThrottle || now - _lastOpenUtc < InteractCooldown)
            return;
        _lastScanUtc = now;

        try
        {
            if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.InCombat])
            {
                Status = "waiting — combat/zoning";
                return;
            }

            // High-end duties are excluded outright (Pandora makes this a config; bots have no
            // business auto-interacting in savage anyway).
            var cfcId = GameMain.Instance()->CurrentContentFinderConditionId;
            if (cfcId != 0)
            {
                var sheet = _dataManager.GetExcelSheet<ContentFinderCondition>();
                if (sheet != null && sheet.TryGetRow(cfcId, out var cfc) && cfc.HighEndDuty)
                {
                    Status = "idle — high-end duty";
                    return;
                }
            }

            var local = _objectTable.LocalPlayer;
            if (local == null)
                return;

            foreach (var obj in _objectTable)
            {
                if (!obj.IsTargetable)
                    continue;

                var distance = Vector3.Distance(local.Position, obj.Position);

                // Deep-dungeon coffers are EventObj, NOT ObjectKind.Treasure — the Pandora rules
                // below can never see them, which is why they went unopened in Eureka Orthos.
                if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
                {
                    if (TryOpenCoffer(obj, distance, local, now))
                        return;
                    continue;
                }

                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure
                    || distance > _openRange())
                    continue;

                var treasure = (FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure*)obj.Address;
                if (treasure->Flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags.Opened)
                    || treasure->Flags.HasFlag(FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags.FadedOut))
                    continue;

                // Already on the loot window = already opened by someone in the party.
                var onLootWindow = false;
                foreach (var item in Loot.Instance()->Items)
                {
                    if (item.ChestObjectId == obj.GameObjectId)
                    {
                        onLootWindow = true;
                        break;
                    }
                }

                if (onLootWindow)
                    continue;

                _lastOpenUtc = now;
                _interact.TryInteract(obj);
                Status = "opened a chest";
                _log.Info("Auto-chest: opened {0}", obj.GameObjectId);
                return;
            }

            Status = "idle — no chest in reach";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Chest opener threw");
            Status = "threw (see log)";
        }
    }

    /// <summary>
    /// Deep-dungeon coffers (bronze/silver/gold and the dug-up Accursed Hoard), which the game
    /// models as EventObj rather than Treasure. Rules are NecroLens's (MIT), which are stricter
    /// than the overworld ones for good reason:
    /// - the mimic coffer id is never touched;
    /// - a SILVER coffer is left alone below 77% HP — silver coffers can be trap chests, and the
    ///   damage has killed unattended toons;
    /// - per-type interact reach (bronze 3.1y, the rest 4.4y), taken together with the user's own
    ///   range so a tightened slider still wins;
    /// - an EventObj carries no "opened" flag, so an interacted coffer is remembered by entity id
    ///   and only retried after 10s (a coffer that really opened is gone from the table by then).
    /// </summary>
    private bool TryOpenCoffer(IGameObject obj, float distance, IGameObject local, DateTime now)
    {
        var baseId = obj.BaseId;
        float reach;
        var silver = false;

        if (DeepDungeonIds.BronzeChests.Contains(baseId))
        {
            reach = 3.1f;
        }
        else if (baseId == DeepDungeonIds.SilverChest)
        {
            reach = 4.4f;
            silver = true;
        }
        else if (baseId == DeepDungeonIds.GoldChest || baseId == DeepDungeonIds.AccursedHoardCoffer)
        {
            reach = 4.4f;
        }
        else
        {
            return false; // scenery, a mimic coffer, or an undug hoard mound — none are ours
        }

        if (distance > MathF.Min(_openRange(), reach))
            return false;

        if (silver && local is ICharacter character && character.MaxHp > 0
            && character.CurrentHp <= character.MaxHp * 0.77f)
        {
            Status = "holding — silver coffer below 77% HP";
            return false;
        }

        if (_cofferTouched.TryGetValue(obj.EntityId, out var touchedAt) && now - touchedAt < CofferRetry)
            return false;

        _cofferTouched[obj.EntityId] = now;
        _lastOpenUtc = now;
        _interact.TryInteract(obj);
        Status = "opened a deep dungeon coffer";
        _log.Info("Auto-chest: opened coffer {0} (base {1})", obj.EntityId, baseId);
        return true;
    }
}
