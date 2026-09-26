using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Dailies;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// The game's own allowance lines, read out of its Timers window.
///
/// Which window that is was the first question: the game calls the window "Timers" but its internal addon is
/// named something else — `/charon text` (the recorder in <see cref="WindowTextDump"/>) lists what the client
/// has loaded, so this tries the plausible names in order and remembers which one answered, instead of
/// asserting one. The VALUES are found by label inside whatever window it is, so the read survives the window
/// being restructured.
///
/// The window has to be OPEN: the client only keeps its contents while it is up, so this holds the last thing
/// it read with the time it read it — the same snapshot honesty as the retainer contents store, because
/// "15h 15m remaining" from an hour ago is a different statement from one read now.
/// </summary>
public sealed class AllowanceReader
{
    /// <summary>Candidate addon names, most likely first. The first one that is open wins and is remembered.</summary>
    private static readonly string[] Candidates = ["_ToDoList", "ToDoList", "Timers", "Timer", "AddonTimers"];

    private readonly WindowTextDump _windows;
    private readonly IPluginLog _log;

    private string? _addon;
    private bool _loggedMiss;

    public AllowanceReader(WindowTextDump windows, IPluginLog log)
    {
        _windows = windows;
        _log = log;
    }

    /// <summary>The window the last successful read came from, or empty when none has answered yet.</summary>
    public string Addon { get; private set; } = string.Empty;

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
        ? "no Timers window read yet — open it once and Charon reads the game's own answer"
        : SeenUtc == DateTime.MinValue
            ? $"{Addon}: nothing read yet"
            : $"read from '{Addon}', {Describe(DateTime.UtcNow - SeenUtc)} ago";

    /// <summary>Called every tick; a read happens only while one of the candidate windows is actually open.</summary>
    public void Update(DateTime nowUtc)
    {
        var addon = _addon ?? Candidates.FirstOrDefault(IsOpen);
        if (addon == null || !IsOpen(addon))
            return;

        var lines = new List<AllowanceLine>();
        var nodes = _windows.Read(addon).Select(n => (n.X, n.Y, n.Text)).ToList();

        foreach (var label in Allowances.KnownLabels)
        {
            var line = Allowances.Find(nodes, label);
            if (line != null)
                lines.Add(line);
        }

        if (lines.Count == 0)
        {
            if (!_loggedMiss)
            {
                _loggedMiss = true;
                _log.Debug("[Allowances] '{0}' is open but none of the known allowance labels were in it "
                           + "({1} text node(s)) — /charon text {0} records what is.", addon, nodes.Count);
            }

            return;
        }

        _addon = addon;
        Addon = addon;
        Lines = lines;
        SeenUtc = nowUtc;
    }

    private bool IsOpen(string addon) => _windows.Read(addon).Count > 0;

    private static string Describe(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "less than a minute",
        < 90 => $"{age.TotalMinutes:0} min",
        _ => $"{age.TotalHours:0} h",
    };
}
