using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Charon.Services.Game;

namespace Charon.Ipc;

/// <summary>
/// Charon's gear-equipper provider gates. The contract is FROZEN — SealBreaker v1.1.0.6+ already
/// subscribes to it (leveling mode calls this after duty exit and BEFORE Expert Delivery, so drops
/// get worn instead of turned in):
///
/// | Charon.EquipUpgrades      | Func&lt;bool&gt; | start a pass; true = started or nothing to do, false = refused |
/// | Charon.EquipUpgradesBusy  | Func&lt;bool&gt; | true while a pass runs (SealBreaker polls 250ms, 20s timeout)  |
/// | Charon.PendingUpgradeCount| Func&lt;int&gt;  | upgrades available, WITHOUT equipping — cheap and read-only     |
///
/// Callers fail open: with the gates absent (or refusing) they use the game's Equip Recommended,
/// so a Charon that declines never blocks the fleet.
///
/// EXECUTION IS OPT-IN. Until <c>GearIpcExecuteEnabled</c> is switched on, EquipUpgrades logs the
/// plan and returns false — the count gate stays live, the preview UI stays live, and nothing on
/// the fleet moves an item until the previews have been eyeballed in-game.
/// </summary>
public sealed class GearEquipperIpc : IDisposable
{
    private readonly ICallGateProvider<bool> _equipUpgrades;
    private readonly ICallGateProvider<bool> _busy;
    private readonly ICallGateProvider<int> _pendingCount;

    private readonly GearManager _gear;
    private readonly Func<bool> _ipcEnabled;
    private readonly Func<bool> _executeEnabled;
    private readonly IPluginLog _log;

    public GearEquipperIpc(
        IDalamudPluginInterface pluginInterface,
        GearManager gear,
        Func<bool> ipcEnabled,
        Func<bool> executeEnabled,
        IPluginLog log)
    {
        _gear = gear;
        _ipcEnabled = ipcEnabled;
        _executeEnabled = executeEnabled;
        _log = log;

        _equipUpgrades = pluginInterface.GetIpcProvider<bool>("Charon.EquipUpgrades");
        _busy = pluginInterface.GetIpcProvider<bool>("Charon.EquipUpgradesBusy");
        _pendingCount = pluginInterface.GetIpcProvider<int>("Charon.PendingUpgradeCount");

        _equipUpgrades.RegisterFunc(EquipUpgrades);
        _busy.RegisterFunc(() => _ipcEnabled() && _gear.Busy);
        _pendingCount.RegisterFunc(PendingUpgradeCount);
    }

    /// <summary>Last thing a caller asked of us, for the Debug line.</summary>
    public string Status { get; private set; } = "no calls yet";

    private int PendingUpgradeCount()
    {
        if (!_ipcEnabled())
            return 0;

        var count = _gear.GetUpgrades().Count;
        Status = $"PendingUpgradeCount → {count}";
        return count;
    }

    private bool EquipUpgrades()
    {
        if (!_ipcEnabled())
        {
            Status = "EquipUpgrades → refused (IPC disabled)";
            return false;
        }

        // Preview mode: report the plan, change nothing, and let the caller fall back.
        if (!_executeEnabled())
        {
            var plan = _gear.GetUpgrades();
            Status = $"EquipUpgrades → PREVIEW ONLY ({plan.Count} upgrades, execution off)";
            _log.Info("Gear IPC: preview mode — would equip {0} {1}:",
                plan.Count, plan.Count == 1 ? "piece" : "pieces");
            foreach (var upgrade in plan)
            {
                _log.Info("  {0}: {1} → {2} (+{3} ilvl)",
                    upgrade.Slot, upgrade.Replacing?.Name ?? "(empty)", upgrade.Item.Name, upgrade.IlvlGain);
            }

            return false;
        }

        var started = _gear.StartEquipPass();
        Status = $"EquipUpgrades → {(started ? "started" : "refused")} ({_gear.Status})";
        return started;
    }

    public void Dispose()
    {
        _equipUpgrades.UnregisterFunc();
        _busy.UnregisterFunc();
        _pendingCount.UnregisterFunc();
    }
}
