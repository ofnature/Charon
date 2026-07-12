using System;
using Dalamud.Plugin.Services;
using Charon.Features.HealWatch;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Charon.Services.Game;

/// <summary>
/// Casts the job's basic single-target heal on a fleet toon (in OR out of party — any
/// friendly player is a valid heal target). Thin unsafe adapter, fail-open, framework
/// thread only.
///
/// The roster HP that triggered the intent is 1–2s stale — this executor re-checks LIVE
/// HP from the object table right before casting and refuses when the target has already
/// been topped up, left the zone, or died. UseAction signature verified against the dev
/// assemblies: <c>UseAction(ActionType, uint actionId, ulong targetId, ...)</c>.
/// </summary>
public sealed unsafe class HealExecutor
{
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;

    public HealExecutor(IObjectTable objectTable, IPluginLog log)
    {
        _objectTable = objectTable;
        _log = log;
    }

    /// <summary>Local job/level → heal action id, 0 when we're not a healer (or too low).</summary>
    public uint GetLocalHealAction()
    {
        try
        {
            var player = _objectTable.LocalPlayer;
            if (player == null)
                return 0;

            return HealActionTable.GetHealAction(player.ClassJob.RowId, player.Level);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Cast <paramref name="actionId"/> on the toon with this entity id if they still need it.
    /// Returns true when the cast was submitted.
    /// </summary>
    public bool TryHeal(uint actionId, uint targetEntityId, float healThreshold)
    {
        try
        {
            if (actionId == 0 || targetEntityId == 0)
                return false;

            var target = _objectTable.SearchByEntityId(targetEntityId);
            if (target is not Dalamud.Game.ClientState.Objects.Types.ICharacter character)
                return false; // not in our zone / despawned

            // Live re-check — the roster vitals that triggered us are heartbeat-stale.
            if (character.MaxHp == 0 || character.CurrentHp == 0)
                return false; // dead or unreadable
            if ((float)character.CurrentHp / character.MaxHp > healThreshold)
                return false; // already topped up

            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return false;

            return actionManager->UseAction(ActionType.Action, actionId, character.GameObjectId);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Heal cast failed (action {0}, target {1})", actionId, targetEntityId);
            return false;
        }
    }
}
