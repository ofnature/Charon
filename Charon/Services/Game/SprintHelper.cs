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

    /// <summary>
    /// Status 4398 "Stellar Sprint" — Cosmic Exploration's sprint on all four moons (Sinus Ardorum
    /// 1237, Phaenna 1291, Oizys 1310, Auxesia 1319 — every TerritoryType with intended use 60).
    /// Marked permanent in the Status sheet: it lasts until cancelled. VERIFIED with Lumina against
    /// the game files and by the in-game tooltip. The check reads the buff, not the zone, so a new
    /// moon needs no change here.
    /// </summary>
    public const uint StellarSprintStatus = 4398;

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
