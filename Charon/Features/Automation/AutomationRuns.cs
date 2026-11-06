using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Automation;

/// <summary>One of Charon's runs, as the suite's "stop after this run" contract sees it.</summary>
/// <param name="Name">Short name for the Debug line.</param>
/// <param name="Busy">True while it runs, is queued, is between steps, or is finishing after a stop request.</param>
/// <param name="StopAfterStep">
/// Finish the step in hand, then stop and stay stopped. Only ever called while <paramref name="Busy"/> is true,
/// and must be safe to call again while the stop is landing (a second request is not a toggle).
/// </param>
public sealed record AutomationRun(string Name, Func<bool> Busy, Action StopAfterStep)
{
    /// <summary>For a run that is a single short step: it already stops when done, so a request lets it finish.</summary>
    public static readonly Action LetItFinish = () => { };
}

/// <summary>
/// The suite contract every plugin answers the same way (Argus asks before it moves the character): busy is
/// any run being busy, and a stop request goes to every BUSY run and says whether there was one. An idle run is
/// never touched, so no stale request is left behind to stop the next start — the runs hold their own requests
/// and clear them when the stop lands or a new run begins. This class holds no state of its own.
/// </summary>
public sealed class AutomationRuns
{
    private readonly IReadOnlyList<AutomationRun> _runs;

    public AutomationRuns(IReadOnlyList<AutomationRun> runs) => _runs = runs;

    public bool IsBusy => _runs.Any(r => r.Busy());

    public IReadOnlyList<string> BusyNames() => _runs.Where(r => r.Busy()).Select(r => r.Name).ToList();

    /// <summary>True when anything was busy and has been asked to stop after its step; false, touching nothing, when idle.</summary>
    public bool RequestStopAfterRun()
    {
        var any = false;
        foreach (var run in _runs)
        {
            if (!run.Busy())
                continue;

            run.StopAfterStep();
            any = true;
        }

        return any;
    }
}
