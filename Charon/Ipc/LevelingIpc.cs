using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Charon.Features.Leveling;
using Charon.Services.Game;

namespace Charon.Ipc;

/// <summary>
/// Leveling-support gates for SealBreaker's leveling mode (docs/leveling-mode-plan.md).
///
/// | Charon.Leveling.GetJobLevelsJson | Func&lt;string&gt;      | one entry per combat exp track, each carrying its own blocker |
/// | Charon.Leveling.GetStatusJson    | Func&lt;string&gt;      | account facts for keep-grinding vs pause-for-X decisions      |
/// | Charon.Leveling.SwitchJob        | Func&lt;uint, bool&gt;  | switch via gearset; true = ACCEPTED, completion arrives below |
/// | Charon.Leveling.SellToGilCap     | Func&lt;uint, bool&gt;  | sell that item at the OPEN vendor shop up to the 300k cap     |
/// | Charon.Leveling.Completed        | message (string)       | one JSON frame per finished operation: {op, ok, detail, reason} |
///
/// ONE completion channel for every command, not one per command — a single subscription with an
/// "op" field, exactly like the relay codecs. ONE operation at a time: a command issued while
/// busy returns false (SealBreaker's loop is serial; this removes every interleaving question).
///
/// The per-track blockers ARE the round-robin: SealBreaker picks the lowest-level entry with an
/// empty blocker and every skipped job explains itself in blockerText — a to-do list, not an
/// error log. Payloads are extend-only; consumers must tolerate new fields.
/// </summary>
public sealed class LevelingIpc : IDisposable
{
    private readonly ICallGateProvider<string> _jobLevels;
    private readonly ICallGateProvider<string> _status;
    private readonly ICallGateProvider<uint, bool> _switchJob;
    private readonly ICallGateProvider<uint, bool> _sellToGilCap;
    private readonly ICallGateProvider<string, object?> _completed;

    private readonly JobLevelReader _reader;
    private readonly Func<bool> _enabled;
    private readonly Func<uint, bool> _requestSwitch;
    private readonly Func<uint, bool> _requestSell;

    /// <summary>The running operation's name, "" when idle — drives busy/op in the status.</summary>
    private readonly Func<string> _currentOp;

    /// <summary>Whether this character can still donate at the Doman Enclave this week.</summary>
    private readonly Func<bool> _donationAvailable;

    private readonly IPluginLog _log;

    public LevelingIpc(IDalamudPluginInterface pluginInterface, JobLevelReader reader,
        Func<bool> enabled, Func<uint, bool> requestSwitch, Func<uint, bool> requestSell,
        Func<string> currentOp, Func<bool> donationAvailable, IPluginLog log)
    {
        _reader = reader;
        _enabled = enabled;
        _requestSwitch = requestSwitch;
        _requestSell = requestSell;
        _currentOp = currentOp;
        _donationAvailable = donationAvailable;
        _log = log;

        _jobLevels = pluginInterface.GetIpcProvider<string>("Charon.Leveling.GetJobLevelsJson");
        _status = pluginInterface.GetIpcProvider<string>("Charon.Leveling.GetStatusJson");
        _switchJob = pluginInterface.GetIpcProvider<uint, bool>("Charon.Leveling.SwitchJob");
        _sellToGilCap = pluginInterface.GetIpcProvider<uint, bool>("Charon.Leveling.SellToGilCap");
        _completed = pluginInterface.GetIpcProvider<string, object?>("Charon.Leveling.Completed");

        _jobLevels.RegisterFunc(GetJobLevelsJson);
        _status.RegisterFunc(GetStatusJson);
        _switchJob.RegisterFunc(SwitchJob);
        _sellToGilCap.RegisterFunc(SellToGilCap);
    }

    /// <summary>
    /// Tell subscribers an operation finished. Called by the operation adapters (job switcher,
    /// later the sellers); exactly one frame per accepted command, success or not, so a caller's
    /// wait loop always ends.
    /// </summary>
    public void PublishCompleted(string op, bool ok, string detail)
    {
        var frame = JsonSerializer.Serialize(new CompletedDto
        {
            Op = op,
            Ok = ok,
            Detail = ok ? detail : string.Empty,
            Reason = ok ? string.Empty : detail,
        });

        try
        {
            _completed.SendMessage(frame);
        }
        catch (Exception ex)
        {
            // A subscriber threw in its handler — their bug must not kill our operation's epilogue.
            _log.Warning(ex, "Leveling Completed subscriber threw on {0}", frame);
        }

        Status = $"completed → {op} {(ok ? "ok" : "FAILED")} ({detail})";
    }

    private bool SwitchJob(uint classJobRow)
    {
        if (!_enabled())
        {
            Status = "SwitchJob → refused (IPC disabled)";
            return false;
        }

        var accepted = _requestSwitch(classJobRow);
        Status = $"SwitchJob({classJobRow}) → {(accepted ? "accepted" : "refused")}";
        return accepted;
    }

    private bool SellToGilCap(uint itemId)
    {
        if (!_enabled())
        {
            Status = "SellToGilCap → refused (IPC disabled)";
            return false;
        }

        var accepted = _requestSell(itemId);
        Status = $"SellToGilCap({itemId}) → {(accepted ? "accepted" : "refused")}";
        return accepted;
    }

    /// <summary>Last thing a caller asked of us, for the Debug line.</summary>
    public string Status { get; private set; } = "no calls yet";

    private string GetJobLevelsJson()
    {
        if (!_enabled())
        {
            Status = "GetJobLevels → refused (IPC disabled)";
            return "[]";
        }

        var tracks = _reader.GetTracks();
        Status = $"GetJobLevels → {tracks.Count} tracks";
        return JsonSerializer.Serialize(tracks.Select(TrackDto.From));
    }

    private string GetStatusJson()
    {
        if (!_enabled())
        {
            Status = "GetStatus → refused (IPC disabled)";
            return "{}";
        }

        _reader.GetTracks(); // refresh MaxExpansion alongside the levels
        var freeTrial = _reader.IsFreeTrial;
        var op = _currentOp();
        Status = "GetStatus → ok";
        return JsonSerializer.Serialize(new StatusDto
        {
            Busy = op.Length == 0 ? "idle" : op,
            Op = op,
            Gil = _reader.GetGil(),
            GilCap = freeTrial ? Services.Game.GilCapSeller.FreeTrialGilCap : null,
            FreeTrial = freeTrial,
            MaxExpansion = _reader.MaxExpansion,
            LevelCap = _reader.LevelCap,
            DonationAvailable = _donationAvailable(),
        });
    }

    private sealed class TrackDto
    {
        [JsonPropertyName("row")] public uint Row { get; set; }
        [JsonPropertyName("abbr")] public string Abbr { get; set; } = string.Empty;
        [JsonPropertyName("exp")] public int Exp { get; set; }
        [JsonPropertyName("level")] public short Level { get; set; }
        [JsonPropertyName("unlocked")] public bool Unlocked { get; set; }
        [JsonPropertyName("isJob")] public bool IsJob { get; set; }
        [JsonPropertyName("parent")] public string Parent { get; set; } = string.Empty;
        [JsonPropertyName("expToNext")] public long? ExpToNext { get; set; }
        [JsonPropertyName("capped")] public bool Capped { get; set; }
        [JsonPropertyName("blocker")] public string Blocker { get; set; } = string.Empty;
        [JsonPropertyName("hard")] public bool Hard { get; set; }
        [JsonPropertyName("blockerText")] public string BlockerText { get; set; } = string.Empty;

        public static TrackDto From(JobTrackReport report) => new()
        {
            Row = report.RowId,
            Abbr = report.Abbr,
            Exp = report.ExpArrayIndex,
            Level = report.Level,
            Unlocked = report.Unlocked,
            IsJob = report.IsJob,
            Parent = report.ParentAbbr,
            ExpToNext = report.ExpToNext,
            Capped = report.Capped,
            Blocker = report.Blocker == LevelingBlocker.None ? string.Empty : report.Blocker.ToString(),
            Hard = report.Hard,
            BlockerText = report.BlockerText,
        };
    }

    private sealed class StatusDto
    {
        [JsonPropertyName("busy")] public string Busy { get; set; } = "idle";
        [JsonPropertyName("op")] public string Op { get; set; } = string.Empty;
        [JsonPropertyName("gil")] public long Gil { get; set; }
        [JsonPropertyName("gilCap")] public long? GilCap { get; set; }
        [JsonPropertyName("freeTrial")] public bool FreeTrial { get; set; }
        [JsonPropertyName("maxExpansion")] public int MaxExpansion { get; set; }
        [JsonPropertyName("levelCap")] public int LevelCap { get; set; }
        [JsonPropertyName("donationAvailable")] public bool DonationAvailable { get; set; }
    }

    private sealed class CompletedDto
    {
        [JsonPropertyName("op")] public string Op { get; set; } = string.Empty;
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("detail")] public string Detail { get; set; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }

    public void Dispose()
    {
        _jobLevels.UnregisterFunc();
        _status.UnregisterFunc();
        _switchJob.UnregisterFunc();
        _sellToGilCap.UnregisterFunc();
    }
}
