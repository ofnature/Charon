using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Charon.Features.Leveling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>
/// Reads every combat exp track's level, exp and blockers. Thin unsafe adapter; the pairing and
/// blocker decisions are pure (<see cref="JobLevelTable"/>, <see cref="LevelingBlockerPolicy"/>).
///
/// Everything is indexed by <c>ExpArrayIndex</c>, never by ClassJob row id — a class and its job
/// share one exp slot (GLA/PLD both index 1, verified), so row-id indexing reads the wrong level.
///
/// Deliberately EXCLUDED from the table:
/// - DoH/DoL (DohDolJobIndex >= 0) — no dungeon levels a crafter; leveling mode is dungeon farming.
/// - Limited jobs (IsLimitedJob — BLU) — cannot queue normal duties the way AutoDuty runs them.
/// </summary>
public sealed unsafe class JobLevelReader
{
    /// <summary>Polled by IPC and drawn every frame — same throttle as the other preview caches.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMilliseconds(500);

    /// <summary>Gil is inventory item 1 (the pattern SealBreaker's shop loop already uses).</summary>
    private const uint GilItemId = 1;

    private readonly IDataManager _dataManager;
    private readonly ICondition _condition;
    private readonly IPluginLog _log;

    private List<TrackSheet>? _sheetTracks;
    private List<JobTrackReport>? _cache;
    private DateTime _cacheUtc = DateTime.MinValue;

    /// <summary>One advanced job reachable from a class (ACN has two: SMN and SCH).</summary>
    private sealed record AdvancedRow(uint RowId, string Abbr, uint UnlockQuestId);

    /// <summary>Sheet-side shape of one track, cached for the session — sheets don't change.</summary>
    /// <param name="RequiredExpansion">For starts-advanced jobs, the ExVersion row of their unlock
    /// quest — how we know VPR needs Dawntrail without a hardcoded job list. 0 for class tracks.</param>
    private sealed record TrackSheet(
        int ExpIndex, uint ClassRow, string ClassAbbr, bool StartsAdvanced,
        IReadOnlyList<AdvancedRow> Advanced, int RequiredExpansion);

    public JobLevelReader(IDataManager dataManager, ICondition condition, IPluginLog log)
    {
        _dataManager = dataManager;
        _condition = condition;
        _log = log;
    }

    /// <summary>What it read last, for the Debug section.</summary>
    public string Status { get; private set; } = "not read yet";

    /// <summary>ExVersion row from PlayerState.MaxExpansion, as of the last read.</summary>
    public int MaxExpansion { get; private set; }

    public int LevelCap => JobLevelTable.LevelCapForExpansion(MaxExpansion);

    public bool IsFreeTrial => _condition[ConditionFlag.OnFreeTrial];

    /// <summary>The logged-in character's content id (0 when not logged in) — the stable
    /// per-character key for things like the Doman weekly-donation record.</summary>
    public ulong LocalContentId
    {
        get
        {
            try
            {
                var playerState = PlayerState.Instance();
                return playerState == null ? 0 : playerState->ContentId;
            }
            catch
            {
                return 0;
            }
        }
    }

    public long GetGil()
    {
        try
        {
            var inventory = InventoryManager.Instance();
            return inventory == null ? 0 : inventory->GetInventoryItemCount(GilItemId);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>All combat tracks with levels and blockers. Safe to call from draw code.</summary>
    public IReadOnlyList<JobTrackReport> GetTracks()
    {
        if (_cache != null && DateTime.UtcNow - _cacheUtc < CacheLifetime)
            return _cache;

        _cache = ReadTracks();
        _cacheUtc = DateTime.UtcNow;
        return _cache;
    }

    private List<JobTrackReport> ReadTracks()
    {
        var reports = new List<JobTrackReport>();
        try
        {
            var playerState = PlayerState.Instance();
            if (playerState == null)
            {
                Status = "no player state (not logged in?)";
                return reports;
            }

            MaxExpansion = playerState->MaxExpansion;
            var levelCap = LevelCap;
            var gearsetJobs = ReadGearsetJobs();
            _sheetTracks ??= BuildSheetTracks();

            foreach (var track in _sheetTracks)
            {
                var level = ReadLevel(playerState, track.ExpIndex);
                var expInto = ReadExp(playerState, track.ExpIndex);

                // For class-derived tracks the job exists once its unlock quest is complete; a
                // starts-advanced track's job simply exists whenever the track does.
                var jobUnlocked = track.StartsAdvanced
                    ? level > 0
                    : track.Advanced.Any(a => a.UnlockQuestId != 0 && QuestManager.IsQuestComplete(a.UnlockQuestId));

                // ACN's two jobs (SMN/SCH) share the track: prefer the unlocked row that has a
                // gearset, then any unlocked row, then the first — the SwitchJob target must be
                // something the switcher can actually equip.
                var advanced = PickAdvancedRow(track, gearsetJobs);
                var definition = new JobTrackDefinition(
                    track.ExpIndex, track.ClassRow, track.ClassAbbr,
                    advanced?.RowId ?? 0, advanced?.Abbr ?? string.Empty, track.StartsAdvanced,
                    track.RequiredExpansion);

                var preferredRow = jobUnlocked && definition.JobRowId != 0 ? definition.JobRowId : definition.ClassRowId;
                var state = new JobTrackState(
                    level, expInto, ExpToNextTotal(level), jobUnlocked,
                    gearsetJobs.Contains(preferredRow));

                reports.Add(JobLevelTable.Compose(definition, state, MaxExpansion));
            }

            var workable = reports.Count(r => r.Blocker == LevelingBlocker.None);
            var capped = reports.Count(r => r.Capped);
            Status = $"{reports.Count} tracks · {workable} levelable · {capped} capped · cap {levelCap} (ex {MaxExpansion})";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Job level read threw");
            Status = "read threw (see log)";
        }

        return reports;
    }

    private static short ReadLevel(PlayerState* playerState, int expIndex)
    {
        var levels = playerState->ClassJobLevels;
        return expIndex >= 0 && expIndex < levels.Length ? levels[expIndex] : (short)0;
    }

    private static long ReadExp(PlayerState* playerState, int expIndex)
    {
        var exp = playerState->ClassJobExperience;
        return expIndex >= 0 && expIndex < exp.Length ? exp[expIndex] : 0;
    }

    private int ExpToNextTotal(short level)
    {
        if (level <= 0)
            return 0;

        var sheet = _dataManager.GetExcelSheet<ParamGrow>();
        return sheet != null && sheet.TryGetRow((uint)level, out var row) ? row.ExpToNext : 0;
    }

    private static AdvancedRow? PickAdvancedRow(TrackSheet track, HashSet<uint> gearsetJobs)
    {
        if (track.Advanced.Count == 0)
            return null;

        AdvancedRow? firstUnlocked = null;
        foreach (var row in track.Advanced)
        {
            if (row.UnlockQuestId == 0 || !QuestManager.IsQuestComplete(row.UnlockQuestId))
                continue;
            if (gearsetJobs.Contains(row.RowId))
                return row;
            firstUnlocked ??= row;
        }

        return firstUnlocked ?? track.Advanced[0];
    }

    /// <summary>ClassJob row of every valid saved gearset — the "can we even switch" evidence.</summary>
    private HashSet<uint> ReadGearsetJobs()
    {
        var jobs = new HashSet<uint>();
        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return jobs;

            for (var i = 0; i < module->NumGearsets; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var gearset = module->GetGearset(i);
                if (gearset != null)
                    jobs.Add(gearset->ClassJob);
            }
        }
        catch
        {
            // fail-open: no gearset info just means NoGearset blockers — visible, not wrong
        }

        return jobs;
    }

    private List<TrackSheet> BuildSheetTracks()
    {
        var tracks = new List<TrackSheet>();
        var sheet = _dataManager.GetExcelSheet<ClassJob>();
        if (sheet == null)
            return tracks;

        var byParent = new Dictionary<uint, List<AdvancedRow>>();
        var bases = new List<(uint Row, string Abbr, int ExpIndex, bool StartsAdvanced, int RequiredExpansion)>();

        foreach (var row in sheet)
        {
            if (row.RowId == 0)
                continue;

            var abbr = row.Abbreviation.ExtractText();
            if (abbr.Length == 0 || row.ExpArrayIndex < 0)
                continue;
            if (row.IsLimitedJob || row.DohDolJobIndex >= 0)
                continue;

            var parent = row.ClassJobParent.RowId;
            if (parent == row.RowId)
            {
                // Self-parenting: a base class (JobIndex 0) or a job that starts advanced. For
                // the latter, the unlock quest's expansion says which account tier can have it
                // at all (VPR's quest is Dawntrail content) — read it, never hardcode the list.
                var startsAdvanced = row.JobIndex != 0;
                var requiredExpansion = startsAdvanced
                    ? (int)(row.UnlockQuest.RowId != 0
                        ? _dataManager.GetExcelSheet<Quest>()?.GetRowOrDefault(row.UnlockQuest.RowId)
                              ?.Expansion.RowId ?? 0
                        : 0)
                    : 0;
                bases.Add((row.RowId, abbr, row.ExpArrayIndex, startsAdvanced, requiredExpansion));
            }
            else
            {
                if (!byParent.TryGetValue(parent, out var list))
                    byParent[parent] = list = [];
                list.Add(new AdvancedRow(row.RowId, abbr, row.UnlockQuest.RowId));
            }
        }

        foreach (var (rowId, abbr, expIndex, startsAdvanced, requiredExpansion) in bases.OrderBy(b => b.Row))
        {
            var advanced = byParent.TryGetValue(rowId, out var list)
                ? (IReadOnlyList<AdvancedRow>)list.OrderBy(a => a.RowId).ToList()
                : [];
            tracks.Add(new TrackSheet(expIndex, rowId, abbr, startsAdvanced, advanced, requiredExpansion));
        }

        _log.Info("Leveling: {0} combat tracks from the ClassJob sheet", tracks.Count);
        return tracks;
    }
}
