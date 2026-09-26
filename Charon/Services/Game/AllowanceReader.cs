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

    public AllowanceReader(WindowTextDump windows, IPluginLog log)
    {
        _windows = windows;
        _log = log;
    }

    /// <summary>The window the last successful read came from, or empty when none has answered yet.</summary>
    public string Addon { get; private set; } = string.Empty;

    /// <summary>How many windows the last scan looked inside — evidence, when nothing was found.</summary>
    public int LastScanCount { get; private set; }

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
            if (IsOpen(_addon))
                ReadFrom(_addon, nowUtc);

            return;
        }

        // Walking every loaded window is not free, so a search that keeps failing slows down rather than
        // paying that cost twice a second forever.
        var every = _scans > 30 ? TimeSpan.FromSeconds(15) : _scans > 10 ? TimeSpan.FromSeconds(6) : ScanEvery;
        if (nowUtc - _lastScanUtc < every)
            return;

        _lastScanUtc = nowUtc;
        _scans++;

        // The known name first: one window read against a hundred and eighteen, and the answer is the same.
        if (ReadFrom(KnownAddon, nowUtc))
        {
            _addon = KnownAddon;
            Addon = KnownAddon;
            _log.Information("[Allowances] reading the allowance lines from '{0}' (the Timers window).",
                KnownAddon);
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
            if (ReadFrom(name, nowUtc))
            {
                _addon = name;
                Addon = name;
                _log.Information("[Allowances] the allowance lines are in '{0}' — reading that window from now on.",
                    name);
                return;
            }
        }

        if (!_loggedMiss && LastScanCount > 0)
        {
            _loggedMiss = true;
            _log.Information("[Allowances] none of the {0} open window(s) held an allowance label — open the "
                             + "game's Timers window and it will be found automatically.", LastScanCount);
        }
    }

    /// <summary>Read one window: fills the lines when it holds the labels, and says whether it did.</summary>
    private bool ReadFrom(string addon, DateTime nowUtc)
    {
        var nodes = _windows.Read(addon).Select(n => (n.X, n.Y, n.Text)).ToList();
        if (nodes.Count == 0)
            return false;

        var lines = new List<AllowanceLine>();
        foreach (var label in Allowances.KnownLabels)
        {
            var line = Allowances.Find(nodes, label);
            if (line != null)
                lines.Add(line);
        }

        // One label could be a coincidence; two means this is the window.
        if (lines.Count < 2)
            return false;

        Lines = lines;
        SeenUtc = nowUtc;
        return true;
    }

    private bool IsOpen(string addon) => _windows.Read(addon).Count > 0;

    private static string Describe(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "less than a minute",
        < 90 => $"{age.TotalMinutes:0} min",
        _ => $"{age.TotalHours:0} h",
    };
}
