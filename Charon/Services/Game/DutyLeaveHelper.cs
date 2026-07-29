using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace Charon.Services.Game;

/// <summary>
/// Leaves the current duty. Thin unsafe adapter — the decision is <see cref="Features.Duty.DutyExitPolicy"/>.
///
/// Uses the typed <c>EventFramework.LeaveCurrentContent</c>, which is what BossMod Reborn calls in
/// its live execution path. Deliberately NOT the signature-scanned <c>AbandonDuty</c> that BMR also
/// carries (behind a debug button, inherited from older code): a scanned signature is one game patch
/// away from breaking silently, and there is no reason to own that risk when a typed API exists.
///
/// There is no /leaveduty text command — verified against the TextCommand sheet, which ships only
/// /dutyfinder and /generaldutykey.
/// </summary>
public static class DutyLeaveHelper
{
    /// <summary>Leave the current duty. False when the call was unavailable or threw (fail-open).</summary>
    public static bool TryLeaveDuty(IPluginLog log)
    {
        try
        {
            EventFramework.LeaveCurrentContent(false);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "LeaveCurrentContent threw");
            return false;
        }
    }
}
