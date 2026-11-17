using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Charon.Features.Tweaks;

namespace Charon.Features.Perf;

/// <summary>
/// One second of one game client: how fast it ran and what it cost. <paramref name="Step"/> names what
/// the second belongs to (a sweep step, or the client's state in a plain recording) and
/// <paramref name="Counted"/> is false for seconds that must not enter a summary — a loading screen, or
/// a sweep second spent settling, focused or in combat.
/// </summary>
public sealed record PerfSample(
    DateTime Utc,
    string Step,
    bool Counted,
    bool Focused,
    bool InCombat,
    uint Territory,
    int Frames,
    double TotalMs,
    double WorstMs,
    double CpuCores,
    double WorkingSetMb,
    double PrivateMb,
    double? Gpu3dPercent,
    double? VideoMemoryMb,
    string Applied);

public static class PerfCsv
{
    public const string Header =
        "utc,step,counted,focused,inCombat,territory,frames,fps,worstMs,cpuCores,workingSetMb,privateMb,gpu3dPercent,videoMemoryMb,applied";

    public static string Line(PerfSample s)
    {
        var c = CultureInfo.InvariantCulture;
        var fps = s.TotalMs > 0 ? s.Frames * 1000.0 / s.TotalMs : 0;
        return string.Join(",",
            s.Utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", c),
            Quote(s.Step),
            s.Counted ? "1" : "0",
            s.Focused ? "1" : "0",
            s.InCombat ? "1" : "0",
            s.Territory.ToString(c),
            s.Frames.ToString(c),
            fps.ToString("0.0", c),
            s.WorstMs.ToString("0.0", c),
            s.CpuCores.ToString("0.000", c),
            s.WorkingSetMb.ToString("0", c),
            s.PrivateMb.ToString("0", c),
            s.Gpu3dPercent?.ToString("0.00", c) ?? "",
            s.VideoMemoryMb?.ToString("0", c) ?? "",
            Quote(s.Applied));
    }

    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
}

/// <summary>One step's averages over its counted seconds.</summary>
public sealed record PerfStepSummary(
    string Step, int Seconds, double Fps, double WorstMs, double CpuCores, double? Gpu3dPercent,
    double? VideoMemoryMb, double PrivateMb);

public static class PerfSummary
{
    /// <summary>Counted seconds grouped by step, in the order the steps first appeared. Frame rate is
    /// frames over time (not a mean of per-second rates), so a slow second weighs what it should.</summary>
    public static IReadOnlyList<PerfStepSummary> ByStep(IEnumerable<PerfSample> samples)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<PerfSample>>();
        foreach (var sample in samples)
        {
            if (!sample.Counted)
                continue;
            if (!groups.TryGetValue(sample.Step, out var list))
            {
                groups[sample.Step] = list = new List<PerfSample>();
                order.Add(sample.Step);
            }

            list.Add(sample);
        }

        return order.Select(step =>
        {
            var rows = groups[step];
            var ms = rows.Sum(r => r.TotalMs);
            var gpu = rows.Where(r => r.Gpu3dPercent != null).Select(r => r.Gpu3dPercent!.Value).ToList();
            var memory = rows.Where(r => r.VideoMemoryMb != null).Select(r => r.VideoMemoryMb!.Value).ToList();
            return new PerfStepSummary(
                step,
                rows.Count,
                ms > 0 ? rows.Sum(r => (long)r.Frames) * 1000.0 / ms : 0,
                rows.Max(r => r.WorstMs),
                rows.Average(r => r.CpuCores),
                gpu.Count > 0 ? gpu.Average() : null,
                memory.Count > 0 ? memory.Average() : null,
                rows.Average(r => r.PrivateMb));
        }).ToList();
    }

    /// <summary>"-48%" against the first row's value; empty for the first row or when there is nothing
    /// to compare with.</summary>
    public static string Change(double? value, double? baseline, bool isBaseline)
    {
        if (isBaseline || value is not { } v || baseline is not { } b || b <= 0)
            return string.Empty;
        var percent = (v - b) / b * 100;
        return $"{(percent >= 0 ? "+" : "")}{percent.ToString("0", CultureInfo.InvariantCulture)}%";
    }

    /// <summary>The summary as plain text, for the clipboard and the summary file.</summary>
    public static string Table(IReadOnlyList<PerfStepSummary> rows)
    {
        if (rows.Count == 0)
            return "nothing counted yet";

        var c = CultureInfo.InvariantCulture;
        var first = rows[0];
        var text = new StringBuilder();
        var width = Math.Max(34, rows.Max(r => r.Step.Length));
        text.AppendLine($"{"step".PadRight(width)} {"secs",4} {"fps",6} {"worst ms",8} {"CPU cores",16} {"GPU %",14} {"video MB",15}");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var baseline = i == 0;
            text.AppendLine(string.Join(" ",
                row.Step.PadRight(width),
                row.Seconds.ToString(c).PadLeft(4),
                row.Fps.ToString("0.0", c).PadLeft(6),
                row.WorstMs.ToString("0", c).PadLeft(8),
                With(row.CpuCores.ToString("0.00", c), Change(row.CpuCores, first.CpuCores, baseline)).PadLeft(16),
                With(row.Gpu3dPercent?.ToString("0.0", c) ?? "?", Change(row.Gpu3dPercent, first.Gpu3dPercent, baseline)).PadLeft(14),
                With(row.VideoMemoryMb?.ToString("0", c) ?? "?", Change(row.VideoMemoryMb, first.VideoMemoryMb, baseline)).PadLeft(15)));
        }

        return text.ToString().TrimEnd();
    }

    private static string With(string value, string change) => change.Length == 0 ? value : $"{value} ({change})";
}

/// <summary>One setting to hold and measure. <paramref name="Choice"/> is what the render profile
/// applies for the step (null = nothing, the player's own settings); <paramref name="TargetFps"/> above
/// 0 paces the client's frames to that rate instead. A memory-probe step instead sets one game System
/// option (<paramref name="Option"/> to <paramref name="Value"/>) or resizes the window's picture to
/// <paramref name="Width"/> x <paramref name="Height"/>; either is put back when the step ends.</summary>
public sealed record SweepStep(string Name, BotRenderChoice? Choice, int TargetFps = 0,
    string? Option = null, uint Value = 0, int Width = 0, int Height = 0);

/// <summary>One game option as the client reports it: its allowed range and what it holds now.</summary>
public readonly record struct OptionRange(uint Minimum, uint Maximum, uint Current);

public static class PerfSweepPlan
{
    /// <summary>Seconds thrown away at the start of a step and after any interruption: the render
    /// profile waits 3s in the background before it applies anything, then the change has to land.</summary>
    public const int SettleSeconds = 6;

    /// <summary>Counted seconds per step.</summary>
    public const int HoldSeconds = 15;

    private static BotRenderChoice Only(bool cap = false, bool fewer = false, int resolution = 0) =>
        new(true, cap, true, fewer, resolution > 0, resolution > 0 ? resolution : BotRenderPolicy.DefaultResolutionPercent);

    /// <summary>The first step is the baseline every other is compared with. The steps are only
    /// comparable in one scene: the first live sweep (2026-11-15) crossed a zone change, and the
    /// uncapped rate went from 34 to 93 fps for reasons no setting caused.</summary>
    public static readonly IReadOnlyList<SweepStep> Steps =
    [
        new("your settings", null),
        new("game frame cap", Only(cap: true)),
        new("30 fps", null, 30),
        new("15 fps", null, 15),
        new("10 fps", null, 10),
        new("resolution 75%", Only(resolution: 75)),
        new("resolution 50%", Only(resolution: 50)),
        new("resolution 25%", Only(resolution: 25)),
        new("fewest characters", Only(fewer: true)),
        new("cap + 50% + fewest characters", Only(cap: true, fewer: true, resolution: 50)),

        // The baseline again: its change against the first row is how far the scene itself drifted
        // during the run, which is the error bar on every other row.
        new("your settings (end)", null),
    ];

    /// <summary>Roughly how long an uninterrupted sweep takes.</summary>
    public static TimeSpan Length => TimeSpan.FromSeconds(Steps.Count * (SettleSeconds + HoldSeconds));

    /// <summary>Textures and render targets take longer to reload than a frame cap takes to bite.</summary>
    public const int MemorySettleSeconds = 10;

    public const int MemoryHoldSeconds = 8;

    /// <summary>
    /// Game System options that could plausibly change how much video memory a client holds (the
    /// first sweep showed frame rate, 3D resolution scale and the character limit do not). Names are
    /// Dalamud's SystemConfigOption members. What each VALUE means is not documented, so the probe
    /// tries every other value an option allows and lets the measurement say.
    /// </summary>
    public static readonly IReadOnlyList<string> MemoryOptions =
    [
        "TextureRezoType",
        "UiAssetType",
        "ShadowTextureSizeType_DX11",
        "ShadowCascadeCountType_DX11",
        "MapResolution_DX11",
        "LodType_DX11",
        "ShadowLOD_DX11",
        "ShadowBgLOD",
        "GrassQuality_DX11",
    ];

    /// <summary>Picture sizes the probe shrinks the window to, when the window is larger.</summary>
    public static readonly IReadOnlyList<(int Width, int Height)> WindowSizes = [(1280, 720), (960, 540)];

    /// <summary>
    /// The memory probe for one client: the baseline, every other value of every option it can read
    /// (an option with a huge range is not a quality level and is skipped), the smaller window sizes
    /// last, and the baseline again. Window steps are last because putting the window back may bring
    /// it to the front.
    /// </summary>
    public static IReadOnlyList<SweepStep> MemorySteps(Func<string, OptionRange?> read, int width, int height)
    {
        // The character limit goes through the render profile like the first sweep's steps. That
        // sweep measured it in a quiet zone, where there was nobody to stop drawing.
        var steps = new List<SweepStep> { new("your settings", null), new("fewest characters", Only(fewer: true)) };
        foreach (var option in MemoryOptions)
        {
            if (read(option) is not { } range || range.Maximum < range.Minimum || range.Maximum - range.Minimum > 4)
                continue;
            for (var value = range.Minimum; value <= range.Maximum; value++)
                if (value != range.Current)
                    steps.Add(new SweepStep($"{option} = {value} (yours {range.Current})", null, Option: option, Value: value));
        }

        foreach (var (w, h) in WindowSizes)
            if (w < width && h < height)
                steps.Add(new SweepStep($"window {w}x{h} (yours {width}x{height})", null, Width: w, Height: h));

        steps.Add(new SweepStep("your settings (end)", null));
        return steps;
    }
}

/// <summary>Walks the sweep one second at a time. A second counts only when the client is usable for
/// measuring (in the background, out of combat, not loading) and the step has settled.</summary>
public sealed class PerfSweepState
{
    private readonly IReadOnlyList<SweepStep> _steps;
    private readonly int _settle;
    private int _settleLeft;

    public PerfSweepState(IReadOnlyList<SweepStep>? steps = null, int settleSeconds = PerfSweepPlan.SettleSeconds,
        int holdSeconds = PerfSweepPlan.HoldSeconds)
    {
        _steps = steps ?? PerfSweepPlan.Steps;
        _settle = _settleLeft = settleSeconds;
        Hold = holdSeconds;
    }

    /// <summary>Counted seconds each step needs.</summary>
    public int Hold { get; }

    public int Index { get; private set; }
    public int Counted { get; private set; }
    public int StepCount => _steps.Count;
    public bool Done => Index >= _steps.Count;
    public bool Settling => !Done && _settleLeft > 0;
    public SweepStep? Current => Done ? null : _steps[Index];

    /// <summary>One second has passed. Returns true when it counts toward the current step — read
    /// <see cref="Current"/> BEFORE calling, since the last counted second moves on to the next step.</summary>
    public bool Tick(bool usable)
    {
        if (Done)
            return false;

        if (!usable)
        {
            _settleLeft = _settle;
            return false;
        }

        if (_settleLeft > 0)
        {
            _settleLeft--;
            return false;
        }

        if (++Counted >= Hold)
        {
            Index++;
            Counted = 0;
            _settleLeft = _settle;
        }

        return true;
    }
}
