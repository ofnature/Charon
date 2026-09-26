using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Dailies;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// The game's own allowance lines, read out of its Timers window.
///
/// The window is TITLED "Timers" and its addon is named **ContentsInfo** — settled by an external node dump of
/// that addon, whose only static text nodes are three placeholders while the eleven rows the player reads live
/// in a TreeList component one level down. Two wrong candidates came before it: the game's addon names do not
/// match its window titles, and `_ToDoList` (an underscore-prefixed HUD element) is the quest list down the
/// right-hand side of the screen, not this. The VALUES are still found by label rather than by node index, so a
/// window restructured by a patch keeps reading correctly — the name only decides which window to open.
///
/// The window has to be OPEN: the client only keeps its contents while it is up, so this holds the last thing
/// it read with the time it read it — the same snapshot honesty as the retainer contents store, because
/// "15h 15m remaining" from an hour ago is a different statement from one read now.
/// </summary>
public sealed class AllowanceReader
{
    /// <summary>The game's Timers window. Titled "Timers" on screen; this is what the client calls it.</summary>
    private const string KnownAddon = "ContentsInfo";

    /// <summary>
    /// Fallback names, tried only if <see cref="KnownAddon"/> is not loaded. The window is still identified by
    /// finding the labels INSIDE it, so a renamed or restructured window reads correctly rather than
    /// confidently reporting nothing — but the known name is tried first because reading one window beats
    /// scanning every loaded one.
    /// </summary>
    private static readonly string[] Candidates = [KnownAddon, "Timers", "AddonTimers", "_ToDoList"];

    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(2);

    /// <summary>How many windows one pass may look inside — bounded so a scan cannot cost a frame.</summary>
    private const int PerPass = 16;

    private readonly WindowTextDump _windows;
    private readonly IPluginLog _log;

    private string? _addon;
    private bool _loggedMiss;
    private int _scans;
    private int _scanOffset;
    private DateTime _lastScanUtc = DateTime.MinValue;
    private int _misses;

    /// <summary>How many labels a window must show at once before it counts as the Timers window.</summary>
    private const int MinLabels = 3;

    public AllowanceReader(WindowTextDump windows, IPluginLog log)
    {
        _windows = windows;
        _log = log;
    }

    /// <summary>The window the last successful read came from, or empty when none has answered yet.</summary>
    public string Addon { get; private set; } = string.Empty;

    /// <summary>How many windows the last scan looked inside — evidence, when nothing was found.</summary>
    public int LastScanCount { get; private set; }

    /// <summary>
    /// What each window offered during the last scan: text nodes found, labels matched, and how many of those
    /// were the Timers window's own signature labels. This is the difference between "the window is not open"
    /// and "it is open and this reader cannot see its rows", and it should not take a screenshot to tell them
    /// apart.
    /// </summary>
    public IReadOnlyList<string> LastProbe { get; private set; } = [];

    /// <summary>When the lines below were read — nothing here is live.</summary>
    public DateTime SeenUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Every allowance line found, in the window's own order.</summary>
    public IReadOnlyList<AllowanceLine> Lines { get; private set; } = [];

    public AllowanceLine? MissionAllowance => Lines.FirstOrDefault(l =>
        l.Label.Contains("Mission Allowance", StringComparison.OrdinalIgnoreCase));

    /// <summary>Are today's Grand Company hand-ins still open, per the game's own timer?</summary>
    /// <remarks>
    /// A countdown here means nothing is available until it expires — which is the game saying the day's
    /// mission hand-ins are done, and is the only honest source for that: the per-row byte in the board's
    /// agent is not documented for supply rows, this is the same number the player reads off their own window.
    /// </remarks>
    public bool? DailiesOpen => MissionAllowance?.State switch
    {
        AllowanceState.Available => true,
        AllowanceState.Countdown => false,
        _ => null,
    };

    public string Status => Addon.Length == 0
        ? LastScanCount == 0
            ? "no Timers window read yet — open it once and Charon reads the game's own answer"
            : $"no allowance labels found in the {LastScanCount} window(s) that are open — open the game's "
              + "Timers window and Charon picks it up on its own"
        : SeenUtc == DateTime.MinValue
            ? $"{Addon}: nothing read yet"
            : $"read from '{Addon}', {Describe(DateTime.UtcNow - SeenUtc)} ago";

    /// <summary>
    /// Called every tick. Once the window has been found it is re-read directly; until then the open windows
    /// are scanned (twice a second) for the label, so the window identifies itself and no name is assumed.
    /// </summary>
    public void Update(DateTime nowUtc)
    {
        if (_addon != null)
        {
            if (IsOpen(_addon) && ReadFrom(_addon, nowUtc))
            {
                _misses = 0;
                return;
            }

            // A latched window that stops answering is not the Timers window any more — it was once a wrong
            // guess, or it has been restructured. Look again rather than reporting that read forever.
            if (++_misses >= 3)
            {
                _log.Information("[Allowances] '{0}' stopped answering — searching again.", _addon);
                _addon = null;
                Addon = string.Empty;
                _misses = 0;
                _scans = 0;
            }

            return;
        }

        // Walking every loaded window is not free, so a search that keeps failing slows down rather than
        // paying that cost twice a second forever.
        var every = _scans > 30 ? TimeSpan.FromSeconds(15) : _scans > 10 ? TimeSpan.FromSeconds(6) : ScanEvery;
        if (nowUtc - _lastScanUtc < every)
            return;

        _lastScanUtc = nowUtc;
        _scans++;

        var probes = new List<string>();

        // The known name first: one window read against a hundred and nineteen, and the answer is the same.
        if (Probe(KnownAddon, nowUtc, probes))
        {
            Latch(KnownAddon);
            return;
        }

        var open = _windows.LoadedAddons(visibleOnly: false);
        LastScanCount = open.Count;

        // Walk a BOUNDED slice per pass and rotate: reading every loaded window is both expensive and the
        // thing that crashed the tick (walking a window mid-teardown is an access violation, which no
        // try/catch can catch). Every pass still moves through the list, so nothing is missed for long.
        var slice = open.Count <= PerPass
            ? open
            : Enumerable.Range(0, PerPass)
                .Select(i => open[(_scanOffset + i) % open.Count])
                .ToList();
        _scanOffset = (_scanOffset + PerPass) % Math.Max(1, open.Count);

        // Then the hint list (cheap), then the slice. A window that holds the labels is the Timers window
        // whatever it is called — which is the property worth keeping, since the name is what fooled this.
        foreach (var name in Candidates.Concat(slice).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Probe(name, nowUtc, probes))
            {
                Latch(name);
                return;
            }
        }

        LastProbe = probes;

        if (!_loggedMiss && LastScanCount > 0)
        {
            _loggedMiss = true;
            _log.Information("[Allowances] none of the {0} open window(s) held an allowance label — open the "
                             + "game's Timers window and it will be found automatically.", LastScanCount);
        }
    }

    private void Latch(string addon)
    {
        _addon = addon;
        Addon = addon;
        _misses = 0;
        _log.Information("[Allowances] the allowance lines are in '{0}' — reading that window from now on.", addon);
    }

    /// <summary>Read a window and record what it offered, whether or not it was accepted.</summary>
    private bool Probe(string addon, DateTime nowUtc, List<string> probes)
    {
        var nodes = _windows.Read(addon).Select(n => (n.X, n.Y, n.Text)).ToList();

        var lines = new List<AllowanceLine>();
        foreach (var label in Allowances.KnownLabels)
        {
            var line = Allowances.Find(nodes, label);
            if (line != null)
                lines.Add(line);
        }

        var signatures = lines.Count(l =>
            Allowances.Signatures.Any(s => Allowances.IsLabel(l.Label, s)));

        // Only windows with text are worth reporting: the client keeps a hundred and nineteen and most are HUD
        // fragments, so a probe line for each would bury the one that matters.
        if (nodes.Count > 0 && probes.Count < 8)
            probes.Add($"{addon}: {nodes.Count} text node(s), {lines.Count} label(s), {signatures} signature(s)");

        // A couple of labels can be a coincidence — an unrelated window mentions ventures and squadrons too,
        // which is exactly how a scan latched onto one. The Timers window shows several AT ONCE, including at
        // least one label that is only ever its own.
        if (lines.Count < MinLabels || signatures < 1)
            return false;

        Lines = lines;
        SeenUtc = nowUtc;
        return true;
    }

    /// <summary>Read the latched window: fills the lines when it still holds them, and says whether it did.</summary>
    private bool ReadFrom(string addon, DateTime nowUtc) => Probe(addon, nowUtc, []);

    private bool IsOpen(string addon) => _windows.Read(addon).Count > 0;

    private static string Describe(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "less than a minute",
        < 90 => $"{age.TotalMinutes:0} min",
        _ => $"{age.TotalHours:0} h",
    };
}
