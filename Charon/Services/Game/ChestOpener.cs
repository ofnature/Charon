using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
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
    private const float OpenRange = 2f;

    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IDataManager _dataManager;
    private readonly InteractHelper _interact;
    private readonly Func<bool> _enabled;
    private readonly IPluginLog _log;

    private DateTime _lastScanUtc = DateTime.MinValue;
    private DateTime _lastOpenUtc = DateTime.MinValue;

    public ChestOpener(IObjectTable objectTable, ICondition condition, IDataManager dataManager,
        InteractHelper interact, Func<bool> enabled, IPluginLog log)
    {
        _objectTable = objectTable;
        _condition = condition;
        _dataManager = dataManager;
        _interact = interact;
        _enabled = enabled;
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
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure
                    || !obj.IsTargetable
                    || Vector3.Distance(local.Position, obj.Position) > OpenRange)
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
}
