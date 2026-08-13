using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Charon.Features.Leveling;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace Charon.Services.Game;

/// <summary>
/// Switches the active job via the game's own gearset system. Thin unsafe adapter; the legality
/// decision is <see cref="JobSwitchPolicy"/> (pure).
///
/// Gearsets, not armoury shuffling: <c>RaptureGearsetModule.EquipGearset</c> is instant and
/// unambiguous, and a missing gearset is a REFUSAL (the operator makes them in advance — the
/// NoGearset blocker already says so).
///
/// Completion is watched, not assumed: after the equip call the switcher polls the local
/// player's ClassJob each framework tick until it matches (the caller gets ONE completion
/// callback either way). EquipGearset returning success does not mean the job changed yet —
/// and a wrong gearset (wrong job saved in slot) would otherwise "succeed" onto the wrong job,
/// which the read-back catches.
/// </summary>
public sealed unsafe class JobSwitcher
{
    private static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(5);

    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IPluginLog _log;

    /// <summary>One completion per request: (op, ok, detail-or-reason).</summary>
    private readonly Action<string, bool, string> _completed;

    /// <summary>Another leveling operation (the gil seller) running — one at a time, per contract.</summary>
    private readonly Func<bool> _otherOpsBusy;

    // The in-flight switch; null when idle. Watched from the framework tick.
    private uint _pendingRow;
    private string _pendingAbbr = string.Empty;
    private DateTime _pendingSinceUtc;

    public JobSwitcher(IObjectTable objectTable, ICondition condition,
        Func<bool> otherOpsBusy, Action<string, bool, string> completed, IPluginLog log)
    {
        _objectTable = objectTable;
        _condition = condition;
        _otherOpsBusy = otherOpsBusy;
        _completed = completed;
        _log = log;
    }

    public bool Busy => _pendingRow != 0;

    /// <summary>What it is doing, or why the last request ended how it did — for the Debug line.</summary>
    public string Status { get; private set; } = "idle";

    /// <summary>
    /// Ask for a switch to <paramref name="classJobRow"/>. True = accepted (completion arrives
    /// via the callback), false = refused with the reason in <see cref="Status"/>.
    /// </summary>
    public bool Request(uint classJobRow)
    {
        try
        {
            var local = _objectTable.LocalPlayer;
            if (local == null)
            {
                Status = "SwitchJob → refused (no local player)";
                return false;
            }

            var gearsetIndex = FindGearsetFor(classJobRow);
            var decision = JobSwitchPolicy.Evaluate(
                classJobRow, local.ClassJob.RowId, gearsetIndex >= 0, Busy || _otherOpsBusy(),
                _condition[ConditionFlag.InCombat], _condition[ConditionFlag.BoundByDuty]);

            switch (decision.Action)
            {
                case JobSwitchAction.Refuse:
                    Status = $"SwitchJob → refused ({decision.Reason})";
                    return false;

                case JobSwitchAction.AlreadyThere:
                    // Accepted AND completed in one breath — the caller still gets its event, so
                    // its wait loop ends the same way as a real switch.
                    Status = "SwitchJob → already there";
                    _completed("switchJob", true, "already on the requested job");
                    return true;
            }

            var result = RaptureGearsetModule.Instance()->EquipGearset(gearsetIndex);
            if (result < 0)
            {
                Status = $"SwitchJob → EquipGearset refused (code {result})";
                _log.Warning("EquipGearset({0}) returned {1}", gearsetIndex, result);
                return false;
            }

            _pendingRow = classJobRow;
            _pendingAbbr = $"row {classJobRow}";
            _pendingSinceUtc = DateTime.UtcNow;
            Status = $"switching to {_pendingAbbr} (gearset {gearsetIndex + 1})";
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "SwitchJob threw");
            Status = "SwitchJob → threw (see log)";
            return false;
        }
    }

    /// <summary>Watch the in-flight switch; called every framework tick.</summary>
    public void Update(DateTime now)
    {
        if (_pendingRow == 0)
            return;

        try
        {
            var current = _objectTable.LocalPlayer?.ClassJob.RowId ?? 0;
            if (current == _pendingRow)
            {
                Status = $"switched to {_pendingAbbr}";
                Finish(ok: true, "switched");
                return;
            }

            if (now - _pendingSinceUtc > SwitchTimeout)
            {
                // Most likely a stale gearset whose saved job differs from its ClassJob byte, or
                // a state the policy could not see. The caller must not wait forever either way.
                Status = $"switch to {_pendingAbbr} TIMED OUT (still on row {current})";
                _log.Warning("Job switch to {0} timed out; local player is on {1}", _pendingRow, current);
                Finish(ok: false, "timed out — job did not change");
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Job switch watch threw");
            Finish(ok: false, "watch threw");
        }
    }

    private void Finish(bool ok, string detail)
    {
        _pendingRow = 0;
        _pendingAbbr = string.Empty;
        _completed("switchJob", ok, detail);
    }

    /// <summary>
    /// Lowest-numbered valid gearset whose ClassJob is exactly the requested row — gearset 1 is
    /// the player's primary set for a job by convention, so the FIRST match is the predictable
    /// pick, not the highest ilvl.
    /// </summary>
    private static int FindGearsetFor(uint classJobRow)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return -1;

        for (var i = 0; i < module->NumGearsets; i++)
        {
            if (!module->IsValidGearset(i))
                continue;

            var gearset = module->GetGearset(i);
            if (gearset != null && gearset->ClassJob == classJobRow)
                return i;
        }

        return -1;
    }
}
