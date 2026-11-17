using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Charon.Features.Perf;
using Charon.Features.Tweaks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Performance capture for one game client: a row per second (frame rate, worst frame, this process's
/// CPU, memory, GPU load and video memory, and what was turned down at the time) written to a CSV under
/// the plugin's config folder, summarised per step.
///
/// Two ways to run it. RECORD just writes what happens, labelled by the client's state. SWEEP steps
/// the client through <see cref="PerfSweepPlan.Steps"/> — the render profile's settings one at a time
/// (through <see cref="OverrideChoice"/>) and a few paced frame rates — holding each until it has
/// enough clean seconds, so the summary says what each change saves against the player's own settings.
/// It measures; nothing here can predict a GPU's cost.
///
/// MEMORY PROBE is a second sweep for the one thing the first showed no setting moved: video memory.
/// Each step sets ONE game System option to another value it allows, or shrinks the window, in this
/// client only, holds it, and puts it back — on the next step, on stop, on logout and on unload. Only
/// the in-memory setting is touched; the shared settings file is not written by Charon.
///
/// The frame pacing is for the sweep only: it sleeps out the rest of each frame on the game's own
/// thread, in the background, to see what a chosen frame rate costs. It is not a limiter feature.
/// </summary>
public sealed class PerfRecorder : IDisposable
{
    private const int MaxSamples = 20_000;


    private static readonly BotRenderChoice Nothing = new(false, false, true, false, false);

    private readonly IGameConfig _gameConfig;
    private readonly ICondition _condition;
    private readonly IClientState _clientState;
    private readonly FrameWatch _frames;
    private readonly Func<bool> _inCombat;
    private readonly Func<string> _applied;
    private readonly string _folder;
    private readonly IPluginLog _log;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly List<PerfSample> _samples = new();

    private StreamWriter? _writer;
    private GpuCounters? _gpu;
    private PerfSweepState? _sweep;
    private string _file = string.Empty;
    private DateTime _startedUtc;
    private DateTime _secondStartUtc;
    private TimeSpan _cpuAtSecondStart;
    private int _secondFrames;
    private double _secondMs;
    private double _secondWorstMs;
    private long _paceStamp;

    /// <summary>The zone the sweep's counted seconds were measured in (0 = none counted yet).</summary>
    private uint _sweepTerritory;
    private string _pause = string.Empty;

    /// <summary>The probe step whose change is in force, with what to put back when it ends.</summary>
    private SweepStep? _activeStep;
    private (SystemConfigOption Option, uint Original)? _stepOption;
    private GameWindow.Placement? _savedPlacement;
    private string? _stepNote;

    public PerfRecorder(IGameConfig gameConfig, ICondition condition, IClientState clientState, FrameWatch frames,
        Func<bool> inCombat, Func<string> applied, string folder, IPluginLog log)
    {
        _gameConfig = gameConfig;
        _condition = condition;
        _clientState = clientState;
        _frames = frames;
        _inCombat = inCombat;
        _applied = applied;
        _folder = folder;
        _log = log;
    }

    public bool Recording => _writer != null;
    public bool SweepRunning => _sweep is { Done: false };
    public string Folder => _folder;

    /// <summary>The summary of the current or last capture, one row per step.</summary>
    public IReadOnlyList<PerfStepSummary> Summary { get; private set; } = [];

    /// <summary>What ended the last capture, or why one could not start.</summary>
    public string LastResult { get; private set; } = string.Empty;

    /// <summary>What the sweep wants the render profile to apply right now; null when no sweep is
    /// running, which leaves the character's own profile in charge.</summary>
    public BotRenderChoice? OverrideChoice => SweepRunning ? _sweep!.Current!.Choice ?? Nothing : null;

    public string Status
    {
        get
        {
            if (!Recording)
                return LastResult.Length > 0 ? $"off · {LastResult}" : "off";

            var seconds = (int)(DateTime.UtcNow - _startedUtc).TotalSeconds;
            var file = Path.GetFileName(_file);
            if (_sweep is not { Done: false } sweep)
                return $"recording {seconds}s → {file}";

            var step = $"sweep {sweep.Index + 1}/{sweep.StepCount} \"{sweep.Current!.Name}\"";
            if (_pause.Length > 0)
                return $"{step} — paused: {_pause} → {file}";
            return sweep.Settling
                ? $"{step} — settling → {file}"
                : $"{step} — {sweep.Counted}/{sweep.Hold}s → {file}";
        }
    }

    public void StartRecording() => Start(null, "recording");

    public void StartSweep() => Start(new PerfSweepState(), "sweep");

    public void StartMemoryProbe() => Start(
        new PerfSweepState(BuildMemorySteps(), PerfSweepPlan.MemorySettleSeconds, PerfSweepPlan.MemoryHoldSeconds),
        "memory probe");

    private IReadOnlyList<SweepStep> BuildMemorySteps()
    {
        var (width, height) = GameWindow.PictureSize(_process.MainWindowHandle);
        return PerfSweepPlan.MemorySteps(ReadOption, width, height);
    }

    private OptionRange? ReadOption(string name) =>
        Enum.TryParse<SystemConfigOption>(name, out var option)
        && _gameConfig.TryGet(option, out UIntConfigProperties? properties) && properties != null
        && _gameConfig.TryGet(option, out uint current)
            ? new OptionRange(properties.Minimum, properties.Maximum, current)
            : null;

    public void Stop() => Finish(SweepRunning ? "sweep stopped early" : "stopped");

    public void Dispose() => Finish("the plugin unloaded");

    /// <summary>Call once per frame, after the frame watch and the render profile.</summary>
    public void Update(IFramework framework, DateTime nowUtc)
    {
        if (Recording)
            Record(framework.UpdateDelta.TotalMilliseconds, nowUtc);
        Pace();
    }

    private void Start(PerfSweepState? sweep, string kind)
    {
        if (Recording)
            return;

        try
        {
            Directory.CreateDirectory(_folder);
            _file = Path.Combine(_folder, $"perf-{DateTime.Now:yyyyMMdd-HHmmss}-pid{Environment.ProcessId}.csv");
            _writer = new StreamWriter(_file) { AutoFlush = true };
            _writer.WriteLine(PerfCsv.Header);
        }
        catch (Exception ex)
        {
            _writer = null;
            LastResult = $"could not create the capture file ({ex.Message})";
            _log.Warning(ex, "Perf capture: could not create the file");
            return;
        }

        _gpu = new GpuCounters(Environment.ProcessId);
        _sweep = sweep;
        _sweepTerritory = 0;
        _samples.Clear();
        Summary = [];
        _pause = string.Empty;
        _startedUtc = DateTime.UtcNow;
        _secondStartUtc = default; // the first frame recorded starts the first second
        _log.Info($"Perf capture: {kind} started → {_file}");
    }

    private void Finish(string why)
    {
        if (_writer == null)
            return;

        ApplyStep(null); // puts back whatever the last probe step changed
        _writer.Dispose();
        _writer = null;
        _gpu?.Dispose();
        _gpu = null;
        _sweep = null;
        _paceStamp = 0;

        var summaryFile = Path.ChangeExtension(_file, null) + "-summary.txt";
        try
        {
            File.WriteAllText(summaryFile, PerfSummary.Table(Summary));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Perf capture: could not write the summary");
        }

        LastResult = $"{why}, {_samples.Count}s captured → {Path.GetFileName(summaryFile)}";
        _log.Info($"Perf capture: {LastResult}");
    }

    private void Record(double frameMs, DateTime nowUtc)
    {
        if (_secondStartUtc == default)
        {
            _secondStartUtc = nowUtc;
            _process.Refresh();
            _cpuAtSecondStart = _process.TotalProcessorTime;
            _secondFrames = 0;
            _secondMs = _secondWorstMs = 0;
            return;
        }

        _secondFrames++;
        _secondMs += frameMs;
        _secondWorstMs = Math.Max(_secondWorstMs, frameMs);

        var wall = (nowUtc - _secondStartUtc).TotalSeconds;
        if (wall < 1)
            return;

        if (_sweep != null && !_clientState.IsLoggedIn)
        {
            Finish("sweep stopped: logged out");
            return;
        }

        var loading = _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];
        var focused = !_frames.WindowKnown || _frames.IsFocused;
        var inCombat = _inCombat();
        var applied = _stepNote ?? _applied();

        string step;
        bool counted;
        if (_sweep is { Done: false } sweep)
        {
            _pause = loading ? "loading" : focused ? "click away from this window" : inCombat ? "in combat" : string.Empty;
            // Steps are only comparable in one scene; a different zone is a different baseline.
            if (!loading && _sweepTerritory != 0 && _clientState.TerritoryType != _sweepTerritory)
            {
                Finish("sweep stopped: the zone changed, so the steps would not be comparable — run it again and stay in one place");
                return;
            }

            step = sweep.Current!.Name;
            counted = sweep.Tick(_pause.Length == 0);
            if (counted && _sweepTerritory == 0)
                _sweepTerritory = _clientState.TerritoryType;
            ApplyStep(sweep.Current); // the step this second ended in; null once the sweep is done
        }
        else
        {
            step = (focused ? "focused" : applied.Length > 0 ? "background, turned down" : "background")
                   + (inCombat ? ", in combat" : string.Empty);
            counted = !loading;
        }

        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        var gpu = _gpu?.Latest ?? default;
        var sample = new PerfSample(nowUtc, step, counted, focused, inCombat, _clientState.TerritoryType,
            _secondFrames, _secondMs, _secondWorstMs, (cpu - _cpuAtSecondStart).TotalSeconds / wall,
            _process.WorkingSet64 / (1024.0 * 1024.0), _process.PrivateMemorySize64 / (1024.0 * 1024.0),
            gpu.Gpu3dPercent, gpu.VideoMemoryMb, applied);

        _secondStartUtc = nowUtc;
        _cpuAtSecondStart = cpu;
        _secondFrames = 0;
        _secondMs = _secondWorstMs = 0;

        try
        {
            _writer!.WriteLine(PerfCsv.Line(sample));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Perf capture: write failed");
            Finish("the capture file could not be written");
            return;
        }

        _samples.Add(sample);
        Summary = PerfSummary.ByStep(_samples);

        if (_sweep is { Done: true })
            Finish("sweep finished");
        else if (_samples.Count >= MaxSamples)
            Finish("stopped at the capture's length limit");
    }

    /// <summary>
    /// Makes one probe step's change and puts the previous step's back. Called with the same step
    /// every second, so it only acts when the step changes; null puts everything back.
    /// </summary>
    private void ApplyStep(SweepStep? step)
    {
        if (ReferenceEquals(step, _activeStep))
            return;
        _activeStep = step;
        _stepNote = null;

        if (_stepOption is { } old)
        {
            _gameConfig.Set(old.Option, old.Original);
            _log.Info($"Perf capture: put {old.Option} back to {old.Original}");
            _stepOption = null;
        }

        var window = _process.MainWindowHandle;
        if (step is { Width: > 0 } && window != 0)
        {
            // Saved once, so two window steps in a row go back to the original, not to each other.
            _savedPlacement ??= GameWindow.Save(window);
            if (_savedPlacement != null && GameWindow.SetPictureSize(window, step.Width, step.Height))
            {
                _stepNote = step.Name;
                _log.Info($"Perf capture: window picture set to {step.Width}x{step.Height}");
            }
        }
        else if (_savedPlacement is { } placement)
        {
            GameWindow.Restore(window, placement);
            _savedPlacement = null;
            _log.Info("Perf capture: window put back");
        }

        if (step?.Option != null && Enum.TryParse<SystemConfigOption>(step.Option, out var option)
            && _gameConfig.TryGet(option, out uint current))
        {
            _gameConfig.Set(option, step.Value);
            _stepOption = (option, current);
            _stepNote = step.Name;
            _log.Info($"Perf capture: set {option} to {step.Value} (was {current})");
        }
    }

    /// <summary>Sleeps out the rest of the frame for a paced sweep step. Only in the background: a
    /// person who clicks into the window gets their frame rate back at once.</summary>
    private void Pace()
    {
        var target = _sweep is { Done: false } sweep && _frames.WindowKnown && !_frames.IsFocused
            ? sweep.Current!.TargetFps
            : 0;
        if (target <= 0)
        {
            _paceStamp = 0;
            return;
        }

        if (_paceStamp != 0)
        {
            var due = _paceStamp + Stopwatch.Frequency / target;
            var wait = due - Stopwatch.GetTimestamp();
            if (wait > 0)
            {
                // No spinning to hit the target exactly: spun time would be counted as this client's
                // CPU. The rate actually reached is what the row records.
                var ms = (int)(wait * 1000 / Stopwatch.Frequency);
                if (ms > 0)
                    Thread.Sleep(ms);
            }
        }

        _paceStamp = Stopwatch.GetTimestamp();
    }
}
