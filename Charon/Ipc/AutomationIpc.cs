using System;
using Charon.Features.Automation;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Charon.Ipc;

/// <summary>
/// The suite's "stop after this run" contract — the same two gates in every plugin of the suite. Argus asks each
/// plugin to stop after its run and waits until all are idle before it moves the character (its unattended
/// resend); a plugin that does not answer is skipped.
///
/// | Charon.IsBusy              | Func&lt;bool&gt; | true while any Charon run is running, queued, between steps, or finishing after a stop request |
/// | Charon.RequestStopAfterRun | Func&lt;bool&gt; | finish the step in hand, then stop and stay stopped; true = was busy and will stop, false = idle, nothing changed |
///
/// What counts as a run is the list built in CharonPlugin (the chest, retainer passes, ventures, entrust, gear,
/// job switch, the gil-cap sale, Doman, saddlebag, party invites, a scheduled duty exit or follow teleport, and an
/// active fleet follow). Standing switches that react to events — auto-accept, Heal Watch, Quick Kill, coffers,
/// auto pillion, auto-collect, dialogue and cutscene skipping — are not runs: neither gate sees or changes them.
///
/// Called on the framework thread. The request only sets state; each run stops itself on its own next tick, and
/// clears the request when it does, so a fresh start afterwards (from the UI or another IPC call) runs normally.
/// No execute switch: asking whether Charon is busy, or asking it to stop, can never move anything.
/// Contract rule: EXTEND-ONLY.
/// </summary>
public sealed class AutomationIpc : IDisposable
{
    private readonly ICallGateProvider<bool> _isBusy;
    private readonly ICallGateProvider<bool> _requestStop;
    private readonly AutomationRuns _runs;
    private readonly IPluginLog _log;

    public AutomationIpc(IDalamudPluginInterface pluginInterface, AutomationRuns runs, IPluginLog log)
    {
        _runs = runs;
        _log = log;

        _isBusy = pluginInterface.GetIpcProvider<bool>("Charon.IsBusy");
        _requestStop = pluginInterface.GetIpcProvider<bool>("Charon.RequestStopAfterRun");

        _isBusy.RegisterFunc(() => _runs.IsBusy);
        _requestStop.RegisterFunc(RequestStopAfterRun);
    }

    private string _lastRequest = "no stop requested";

    /// <summary>For the Debug line: what is busy right now, and the last stop request.</summary>
    public string Status
    {
        get
        {
            var busy = _runs.BusyNames();
            return $"{(busy.Count == 0 ? "idle" : "busy: " + string.Join(", ", busy))} · {_lastRequest}";
        }
    }

    private bool RequestStopAfterRun()
    {
        var busy = _runs.BusyNames();
        var stopping = _runs.RequestStopAfterRun();
        _lastRequest = stopping
            ? $"{DateTime.Now:HH:mm:ss} stop after run asked of {string.Join(", ", busy)}"
            : $"{DateTime.Now:HH:mm:ss} stop asked while idle (nothing to do)";
        _log.Debug("[Suite] RequestStopAfterRun → {0}", _lastRequest);
        return stopping;
    }

    public void Dispose()
    {
        _isBusy.UnregisterFunc();
        _requestStop.UnregisterFunc();
    }
}
