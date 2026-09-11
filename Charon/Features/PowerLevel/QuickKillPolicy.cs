using System.Collections.Generic;

namespace Charon.Features.PowerLevel;

/// <summary>A mob fighting a friendly (the party or any fleet toon), as Quick Kill sees it — no Dalamud types.</summary>
/// <param name="Distance">Yalms to the mob's hitbox EDGE, the way action range is measured.</param>
/// <param name="Tagged">We are already on its enmity list — the game's own record that we hit it.</param>
/// <param name="RecentlyTried">We fired at it moments ago; the hit needs time to land.</param>
public readonly record struct TagCandidate(uint EntityId, string Name, float Distance, bool Tagged, bool RecentlyTried);

/// <summary>Which mob to act on this tick. <see cref="TargetEntityId"/> 0 means nothing, for <see cref="Reason"/>.</summary>
public readonly record struct QuickKillDecision(uint TargetEntityId, string TargetName, string Reason)
{
    public bool HasTarget => TargetEntityId != 0;
}

/// <summary>
/// Quick Kill's two roles, both pure, both fed ONLY mobs already fighting a friendly — the party
/// or any fleet toon — so neither can ever pull a mob of its own.
///
/// KILL (<see cref="DecideKill"/>) is for the CARRY: it chooses what goes under the crosshair and
/// the toon's own rotation does the killing. TAG (<see cref="DecideTag"/>) is for a toon BEING
/// carried: one ranged hit on each mob so it joins the kill, and then it is left alone.
/// </summary>
public static class QuickKillPolicy
{
    /// <summary>
    /// KILL: keep the current target while it is still fighting the fleet — switching mid-kill
    /// throws away the damage already dealt — otherwise take the NEAREST engaged mob in reach.
    /// Engaged-only is the whole safety story: the carry hitting a mob FIRST would take the claim,
    /// and with it the EXP, away from the toons being carried.
    /// </summary>
    public static QuickKillDecision DecideKill(bool hasCarry, float reach, uint currentTargetId,
        IReadOnlyList<TagCandidate> candidates)
    {
        if (!hasCarry)
            return Idle("no fleet toons to kill for");

        TagCandidate? nearest = null;
        foreach (var candidate in candidates)
        {
            if (candidate.EntityId == currentTargetId)
                return new QuickKillDecision(candidate.EntityId, candidate.Name, $"killing {candidate.Name}");

            if (candidate.Distance <= reach && (nearest == null || candidate.Distance < nearest.Value.Distance))
                nearest = candidate;
        }

        if (nearest is { } next)
            return new QuickKillDecision(next.EntityId, next.Name, $"targeting {next.Name}");

        return Idle(candidates.Count == 0 ? "nothing is fighting the fleet" : "engaged mobs out of reach");
    }

    /// <summary>
    /// TAG: among the untagged mobs in reach, the NEAREST gets this toon's one hit. A cast is held
    /// while the toon is moving (a cast on the move just fails), and a mob already on our enmity
    /// list is never touched again — which is what makes this one hit, not a toon that joins in
    /// and keeps swinging.
    /// </summary>
    public static QuickKillDecision DecideTag(bool rotationActive, bool hasCarry, TagAction? tag,
        bool casting, bool moving, IReadOnlyList<TagCandidate> candidates)
    {
        // The rotation is already attacking — never fight it for the action queue (Heal Watch doctrine).
        if (rotationActive)
            return Idle("standing down (Daedalus rotation enabled)");
        if (!hasCarry)
            return Idle("no party and no fleet toons — nobody is carrying");
        if (tag == null)
            return Idle("no ranged tag for this job at this level");
        if (casting)
            return Idle("casting");

        TagCandidate? best = null;
        var tagged = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Tagged)
            {
                tagged++;
                continue;
            }

            if (candidate.RecentlyTried || candidate.Distance > tag.Range)
                continue;

            if (best == null || candidate.Distance < best.Value.Distance)
                best = candidate;
        }

        if (best == null)
        {
            return Idle(candidates.Count == 0
                ? "nothing engaged"
                : tagged == candidates.Count
                    ? $"all {tagged} engaged mob(s) tagged"
                    : "untagged mobs out of reach, or a hit is landing");
        }

        if (tag.IsCast && moving)
            return Idle($"holding — {tag.Name} is a cast and the toon is moving");

        return new QuickKillDecision(best.Value.EntityId, best.Value.Name, $"tag {best.Value.Name} with {tag.Name}");
    }

    private static QuickKillDecision Idle(string reason) => new(0, string.Empty, reason);
}
