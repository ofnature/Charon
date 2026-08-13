namespace Charon.Features.Leveling;

/// <summary>What to do with one switch request.</summary>
public enum JobSwitchAction
{
    /// <summary>Refused — <see cref="JobSwitchDecision.Reason"/> says why.</summary>
    Refuse,

    /// <summary>Already on the requested job: complete immediately, equip nothing.</summary>
    AlreadyThere,

    /// <summary>Equip the gearset and watch for the job change to land.</summary>
    Switch,
}

public sealed record JobSwitchDecision(JobSwitchAction Action, string Reason);

/// <summary>
/// Decides whether a job-switch request may run. Pure logic — no Dalamud types. Ordered, first
/// match wins.
///
/// The switcher NEVER improvises: no gearset for the requested row is a refusal, not a cue to
/// assemble something from the armoury. Gearsets are made in advance (the NoGearset blocker says
/// so), because a half-guessed kit on an unattended toon fails dungeons silently.
/// </summary>
public static class JobSwitchPolicy
{
    /// <param name="requestedRow">ClassJob row the caller wants (the "row" field it read from us).</param>
    /// <param name="currentRow">ClassJob row the local player is on right now.</param>
    /// <param name="hasGearsetForRow">A saved gearset targets exactly the requested row.</param>
    /// <param name="busy">Another leveling operation is running (one at a time, per contract).</param>
    /// <param name="inCombat">The game refuses job changes in combat.</param>
    /// <param name="inDuty">The game refuses job changes while bound by duty.</param>
    public static JobSwitchDecision Evaluate(
        uint requestedRow, uint currentRow, bool hasGearsetForRow,
        bool busy, bool inCombat, bool inDuty)
    {
        if (requestedRow == 0)
            return new JobSwitchDecision(JobSwitchAction.Refuse, "no job requested");

        if (busy)
            return new JobSwitchDecision(JobSwitchAction.Refuse, "another leveling operation is running");

        // Before the can-we checks: being on the job already is success, not a refusal — the
        // caller's loop treats it as "switch done", and nothing needs to be legal for a no-op.
        if (requestedRow == currentRow)
            return new JobSwitchDecision(JobSwitchAction.AlreadyThere, "already on the requested job");

        if (inCombat)
            return new JobSwitchDecision(JobSwitchAction.Refuse, "in combat — the game refuses job changes");

        if (inDuty)
            return new JobSwitchDecision(JobSwitchAction.Refuse, "bound by duty — the game refuses job changes");

        if (!hasGearsetForRow)
            return new JobSwitchDecision(JobSwitchAction.Refuse,
                "no gearset for that job; make one and it joins the rotation");

        return new JobSwitchDecision(JobSwitchAction.Switch, string.Empty);
    }
}
