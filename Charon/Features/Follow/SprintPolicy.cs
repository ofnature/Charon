namespace Charon.Features.Follow;

/// <summary>Whether to sprint this tick, and why not if not.</summary>
public sealed record SprintDecision(bool Sprint, string Reason);

/// <summary>
/// Decides whether to hit Sprint. Pure logic — no Dalamud types.
///
/// The rule is deliberately blunt: out of combat, always sprint. An unattended toon trailing its
/// leader has no reason to walk, and the flex leash means a follower that breaks the leash has real
/// ground to make up.
///
/// Only two things hold it back. MOVING, because sprinting on the spot burns a 60s cooldown for
/// nothing; and MOUNTED, because a mount is already faster and the action would be wasted. Combat is
/// excluded outright — sprint is barely useful there and competing with the rotation for the action
/// queue is the one thing Charon never does (same reasoning as Heal Watch standing down).
///
/// Availability is NOT modelled here: the game is asked directly (<c>GetActionStatus</c>), which
/// covers cooldown, zone rules, and the fact that instanced duties have their own sprint behaviour.
/// Five different statuses are named "Sprint" (50 overworld plus instance variants), so checking for
/// the buff by id would have been guesswork.
/// </summary>
public static class SprintPolicy
{
    public static SprintDecision Evaluate(bool enabled, bool inCombat, bool mounted, bool moving, bool actionReady)
    {
        if (!enabled)
            return new SprintDecision(false, "disabled");

        if (inCombat)
            return new SprintDecision(false, "in combat");

        if (mounted)
            return new SprintDecision(false, "mounted");

        if (!moving)
            return new SprintDecision(false, "standing still");

        if (!actionReady)
            return new SprintDecision(false, "sprint not available (cooldown or already up)");

        return new SprintDecision(true, "sprinting");
    }
}
