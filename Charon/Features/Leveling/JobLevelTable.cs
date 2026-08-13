using System;

namespace Charon.Features.Leveling;

/// <summary>
/// Sheet-side facts about one EXP TRACK — resolved by the adapter, never looked up here.
///
/// A track is the unit of leveling, NOT a ClassJob row: a class and its advanced job share one
/// exp array slot (VERIFIED: GLA row 1 and PLD row 19 both carry ExpArrayIndex 1), so indexing
/// anything by ClassJob row id reads a different job's level and silently corrupts every
/// decision built on it. ~22 tracks exist, not ~40 jobs.
/// </summary>
/// <param name="JobRowId">The advanced job's row; equals <paramref name="ClassRowId"/> for jobs
/// that start advanced, 0 if the track has no advanced job.</param>
/// <param name="StartsAdvanced">ClassJobParent == self AND JobIndex != 0 (VERIFIED: DRK row 32
/// and SAM row 34 self-parent; GLA self-parents with JobIndex 0; PLD parents to GLA). These
/// tracks have no class quest and are exempt from the level-30 stop.</param>
/// <param name="RequiredExpansion">ExVersion row of the expansion the job's unlock quest belongs
/// to (0 = ARR). When it exceeds the account's MaxExpansion the job CANNOT be unlocked — a free
/// trial (ShB, row 3) can never have RPR/SGE (EW) or VPR/PCT (DT), and "unlock it and it joins
/// the rotation" would be a lie there.</param>
public sealed record JobTrackDefinition(
    int ExpArrayIndex,
    uint ClassRowId,
    string ClassAbbr,
    uint JobRowId,
    string JobAbbr,
    bool StartsAdvanced,
    int RequiredExpansion = 0);

/// <summary>Live per-track state, read from PlayerState / gearsets / quest flags by the adapter.</summary>
/// <param name="ExpInto">Exp earned into the current level.</param>
/// <param name="ExpToNextTotal">ParamGrow.ExpToNext for the current level; 0 at the absolute cap.</param>
/// <param name="JobUnlocked">Advanced-job unlock quest complete (for starts-advanced tracks,
/// simply "the job exists on this character").</param>
/// <param name="HasGearset">A saved gearset targets the track's preferred row.</param>
public sealed record JobTrackState(
    short Level,
    long ExpInto,
    int ExpToNextTotal,
    bool JobUnlocked,
    bool HasGearset);

/// <summary>
/// One track as reported over IPC and shown in the UI. <paramref name="RowId"/> is the row a
/// job switch should target — the job when it is unlocked, the class until then.
/// <paramref name="ExpToNext"/> is the REMAINING exp and is null exactly where the game's EXP
/// bar reads "-/-": at the account ceiling, at the absolute cap, or on a locked track.
/// </summary>
public sealed record JobTrackReport(
    uint RowId,
    string Abbr,
    int ExpArrayIndex,
    short Level,
    bool Unlocked,
    bool IsJob,
    string ParentAbbr,
    long? ExpToNext,
    bool Capped,
    LevelingBlocker Blocker,
    bool Hard,
    string BlockerText);

/// <summary>
/// Pure composition of sheet facts + live state into track reports. No Dalamud types.
/// </summary>
public static class JobLevelTable
{
    /// <summary>
    /// Level cap from the account's expansion ceiling — <c>PlayerState.MaxExpansion</c>, which is
    /// an ExVersion row id (documented in ClientStructs). Derived, never keyed on the free-trial
    /// flag: when the trial gains an expansion the row moves and the cap follows with no code
    /// change. The formula fallback keeps a FUTURE expansion row degrading gracefully (row 6 →
    /// 110) instead of breaking.
    /// </summary>
    public static int LevelCapForExpansion(int exVersionRow) => exVersionRow switch
    {
        0 => 50,  // A Realm Reborn
        1 => 60,  // Heavensward
        2 => 70,  // Stormblood
        3 => 80,  // Shadowbringers — the free trial today
        4 => 90,  // Endwalker
        5 => 100, // Dawntrail
        _ => exVersionRow < 0 ? 50 : 50 + 10 * exVersionRow,
    };

    /// <summary>Expansion display names, for blocker text. Formula fallback for future rows.</summary>
    public static string ExpansionName(int exVersionRow) => exVersionRow switch
    {
        0 => "A Realm Reborn",
        1 => "Heavensward",
        2 => "Stormblood",
        3 => "Shadowbringers",
        4 => "Endwalker",
        5 => "Dawntrail",
        _ => $"expansion {exVersionRow}",
    };

    /// <summary>
    /// <paramref name="maxExpansion"/> is the account's ExVersion ceiling — the cap is derived
    /// from it HERE so the two facts can never disagree in a report.
    /// </summary>
    public static JobTrackReport Compose(JobTrackDefinition def, JobTrackState state, int maxExpansion)
    {
        var levelCap = LevelCapForExpansion(maxExpansion);
        var unlocked = state.Level > 0;
        var isJob = def.StartsAdvanced ? unlocked : unlocked && state.JobUnlocked;
        var useJobRow = isJob && def.JobRowId != 0;
        var rowId = useJobRow ? def.JobRowId : def.ClassRowId;
        var abbr = useJobRow && def.JobAbbr.Length > 0 ? def.JobAbbr : def.ClassAbbr;
        var capped = unlocked && state.Level >= levelCap;

        // Null exactly where the game shows -/-. The account ceiling matters here: at 80 on a
        // Shadowbringers-capped account the SHEET still has a next level, but the game shows -/-
        // and no exp accrues — so the account cap nulls it, not just ExpToNextTotal running out.
        long? expToNext = !unlocked || capped || state.ExpToNextTotal <= 0
            ? null
            : Math.Max(0L, state.ExpToNextTotal - state.ExpInto);

        var decision = LevelingBlockerPolicy.Evaluate(def, state, maxExpansion);

        return new JobTrackReport(
            rowId, abbr, def.ExpArrayIndex, state.Level, unlocked, isJob,
            useJobRow && !def.StartsAdvanced ? def.ClassAbbr : string.Empty,
            expToNext, capped, decision.Blocker, decision.Hard, decision.Text);
    }
}
