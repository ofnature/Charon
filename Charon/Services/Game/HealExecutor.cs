using System;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Charon.Features.HealWatch;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Charon.Services.Game;

/// <summary>
/// Executes Heal Watch casts on fleet toons (in OR out of party — any friendly player is a
/// valid target). Thin unsafe adapter, fail-open, framework thread only.
///
/// The roster vitals that trigger intents are 1–2s stale, so every method re-checks LIVE
/// object-table state right before casting and refuses when it no longer applies:
/// - heals: target already topped up, gone, or dead
/// - HoTs: the status is still running on the target (Daedalus HoT-upkeep doctrine —
///   never clip a live Regen/Galvanize; recast only inside the expiry window)
/// - raises: target alive again, or already has a raise pending (never double-raise)
/// UseAction signature verified against the dev assemblies:
/// <c>UseAction(ActionType, uint actionId, ulong targetId, ...)</c>. Hardcast raises are
/// fine — a mid-cast UseAction attempt is rejected by the game without cancelling the cast.
/// </summary>
public sealed unsafe class HealExecutor
{
    /// <summary>Recast the HoT only when it has less than this long left on the target.</summary>
    private const float HotRefreshWindowSeconds = 3f;

    /// <summary>Status 148 "Raise" — the accept-resurrection window; a target carrying it needs no second raise.</summary>
    private const uint RaisePendingStatusId = 148;

    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;

    public HealExecutor(IObjectTable objectTable, IPluginLog log)
    {
        _objectTable = objectTable;
        _log = log;
    }

    /// <summary>Local job/level → Heal Watch kit, null when we're not a healer.</summary>
    public HealerKit? GetLocalKit()
    {
        try
        {
            var player = _objectTable.LocalPlayer;
            if (player == null)
                return null;

            return HealActionTable.GetKit(player.ClassJob.RowId, player.Level);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Heal the toon if they still need it (live HP at or below the threshold).</summary>
    public bool TryHeal(uint actionId, uint targetEntityId, float healThreshold)
    {
        var target = ResolveBattleChara(targetEntityId);
        if (target == null || actionId == 0)
            return false;

        if (target.MaxHp == 0 || target.CurrentHp == 0)
            return false; // dead or unreadable — the raise path owns dead toons
        if ((float)target.CurrentHp / target.MaxHp > healThreshold)
            return false; // already topped up

        return Cast(actionId, target);
    }

    /// <summary>
    /// Apply the HoT/shield unless the target still carries it. Same recast doctrine as
    /// Daedalus DoT/HoT upkeep: only recast inside the expiry window, never clip.
    /// </summary>
    public bool TryApplyHot(uint actionId, uint statusId, uint targetEntityId)
    {
        var target = ResolveBattleChara(targetEntityId);
        if (target == null || actionId == 0 || statusId == 0)
            return false;

        if (target.MaxHp == 0 || target.CurrentHp == 0)
            return false; // dead — nothing to maintain

        foreach (var status in target.StatusList)
        {
            if (status.StatusId == statusId && status.RemainingTime > HotRefreshWindowSeconds)
                return false; // still running — do not clip
        }

        return Cast(actionId, target);
    }

    /// <summary>Hardcast raise a dead toon, unless they already have a raise pending.</summary>
    public bool TryRaise(uint actionId, uint targetEntityId)
    {
        var target = ResolveBattleChara(targetEntityId);
        if (target == null || actionId == 0)
            return false;

        if (target.MaxHp == 0 || target.CurrentHp != 0)
            return false; // alive (or unreadable) — vitals were stale

        foreach (var status in target.StatusList)
        {
            if (status.StatusId == RaisePendingStatusId)
                return false; // someone already raised them — never double-raise
        }

        return Cast(actionId, target);
    }

    private IBattleChara? ResolveBattleChara(uint entityId)
    {
        try
        {
            if (entityId == 0)
                return null;

            return _objectTable.SearchByEntityId(entityId) as IBattleChara;
        }
        catch
        {
            return null;
        }
    }

    private bool Cast(uint actionId, IBattleChara target)
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return false;

            return actionManager->UseAction(ActionType.Action, actionId, target.GameObjectId);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Heal Watch cast failed (action {0}, target {1})", actionId, target.GameObjectId);
            return false;
        }
    }
}
