namespace Charon.Features.Leveling;

/// <summary>
/// Why a track cannot (or should not) be worked right now. These are a TO-DO LIST for the
/// operator, not an error log: every value except <see cref="AtLevelCap"/> names something a
/// human can do to bring the job into the next run, and the text reads that way — a reminder,
/// not a complaint.
/// </summary>
public enum LevelingBlocker
{
    /// <summary>Workable right now — this is the set the round-robin picks from.</summary>
    None,

    /// <summary>Not unlocked on this character YET. Unlock it and it joins the rotation.</summary>
    JobNotUnlocked,

    /// <summary>At the account ceiling (from MaxExpansion). The one blocker with no to-do.</summary>
    AtLevelCap,

    /// <summary>Under 15 — no dungeon exists below Sastasha, so dungeons cannot level it.</summary>
    BelowDungeonMinimum,

    /// <summary>
    /// A class at 30+ whose job quest is not done. SOFT: the game keeps levelling a class past
    /// 30, but base classes learn no further actions there — a level 50 class runs dungeons on a
    /// level 30 kit and cannot tank, heal or dps properly, so pushing on costs more time than
    /// the quest does.
    /// </summary>
    AdvancedJobQuestPending,

    /// <summary>No saved gearset targets this track's row. Gearsets are made in advance — the
    /// switcher never improvises one out of the armoury.</summary>
    NoGearset,

    /// <summary>
    /// At an expansion boundary with gear below the next tier's entry ilvl. NOT EMITTED YET —
    /// the enum member exists so the IPC contract is stable, but computing it needs the bridge
    /// plan (ilvl-per-boundary table), which is a later module. Wired when BridgePlan lands.
    /// </summary>
    BridgeGearNeeded,
}

public sealed record BlockerDecision(LevelingBlocker Blocker, bool Hard, string Text);

/// <summary>
/// Decides the single blocker for one track. Pure logic — no Dalamud types. Ordered, first
/// match wins (house style: one predictable answer beats a pile of independent flags).
///
/// HARD means the game refuses and the job is unworkable (locked, capped, no dungeon, no
/// gearset to switch with). SOFT means it would technically continue but shouldn't — the
/// class-past-30 case. SealBreaker skips hard blockers and surfaces soft ones as "stopped,
/// here's why": the difference between an error and a decision.
/// </summary>
public static class LevelingBlockerPolicy
{
    /// <summary>Sastasha's level — the first dungeon; nothing below this can dungeon-farm.</summary>
    public const short DungeonMinimumLevel = 15;

    /// <summary>Where a base class stops learning actions and the job quest becomes due.</summary>
    public const short AdvancedJobLevel = 30;

    public static BlockerDecision Evaluate(JobTrackDefinition def, JobTrackState state, int maxExpansion)
    {
        var levelCap = JobLevelTable.LevelCapForExpansion(maxExpansion);

        if (state.Level <= 0)
        {
            // "Unlock it and it joins" must not be said to an account that CANNOT: a free trial
            // (ShB) can never unlock RPR/SGE (EW) or VPR/PCT (DT). Same blocker, honest text —
            // and it heals itself the day the trial's MaxExpansion moves up a row.
            return def.RequiredExpansion > maxExpansion
                ? new BlockerDecision(LevelingBlocker.JobNotUnlocked, Hard: true,
                    $"needs {JobLevelTable.ExpansionName(def.RequiredExpansion)} — not available on this account")
                : new BlockerDecision(LevelingBlocker.JobNotUnlocked, Hard: true,
                    "not unlocked; unlock it and it joins the rotation");
        }

        if (state.Level >= levelCap)
            return new BlockerDecision(LevelingBlocker.AtLevelCap, Hard: true,
                $"level {state.Level} — account ceiling");

        if (state.Level < DungeonMinimumLevel)
            return new BlockerDecision(LevelingBlocker.BelowDungeonMinimum, Hard: true,
                $"level {state.Level}; run class hunts to reach {DungeonMinimumLevel}");

        // Before the gearset check on purpose: at 30 the class quest changes which row the track
        // even targets, so a gearset made first would be for a class about to become a job.
        // Starts-advanced tracks (DRK, SAM …) have no class quest and skip this entirely.
        if (!def.StartsAdvanced && def.JobRowId != 0 && !state.JobUnlocked
            && state.Level >= AdvancedJobLevel)
            return new BlockerDecision(LevelingBlocker.AdvancedJobQuestPending, Hard: false,
                $"level {state.Level} {def.ClassAbbr}; run the class quest to unlock {def.JobAbbr}");

        if (!state.HasGearset)
        {
            var abbr = state.JobUnlocked && def.JobAbbr.Length > 0 ? def.JobAbbr : def.ClassAbbr;
            return new BlockerDecision(LevelingBlocker.NoGearset, Hard: true,
                $"no gearset for {abbr}; make one and it joins the rotation");
        }

        return new BlockerDecision(LevelingBlocker.None, Hard: false, string.Empty);
    }
}
