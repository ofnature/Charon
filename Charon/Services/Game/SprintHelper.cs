using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Charon.Services.Game;

/// <summary>
/// Casts Sprint. Thin unsafe adapter — the decision is <see cref="Features.Follow.SprintPolicy"/>.
///
/// Sprint is a GENERAL action, not a job action: <c>GeneralAction</c> row 4 (verified against the
/// sheet — the same family BMR uses for jump at row 2).
///
/// Availability is asked of the game via <c>GetActionStatus</c> rather than modelled here. That
/// covers the 60s cooldown, zone restrictions, and instanced duties having their own sprint rules —
/// none of which Charon should be second-guessing. It also sidesteps the fact that FIVE different
/// statuses are called "Sprint" (50 overworld plus instance variants), so detecting "already
/// sprinting" by status id would have been guesswork.
/// </summary>
public static unsafe class SprintHelper
{
    /// <summary>GeneralAction row 4. VERIFIED against the sheet.</summary>
    private const uint SprintGeneralAction = 4;

    /// <summary>True when the game says Sprint can be used right now (status 0 = usable).</summary>
    public static bool IsReady()
    {
        try
        {
            var manager = ActionManager.Instance();
            return manager != null
                   && manager->GetActionStatus(ActionType.GeneralAction, SprintGeneralAction) == 0;
        }
        catch
        {
            return false; // unreadable — never cast on a guess
        }
    }

    /// <summary>Cast Sprint. False when it was refused or unavailable.</summary>
    public static bool TrySprint(IPluginLog log)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager == null)
                return false;

            return manager->UseAction(ActionType.GeneralAction, SprintGeneralAction);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Sprint failed");
            return false;
        }
    }
}
