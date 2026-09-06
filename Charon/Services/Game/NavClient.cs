using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Navigation IPC client — every Charon movement (fleet follow, boarding walks, vendor trips)
/// goes through here, so the provider switch covers every consumer at once.
///
/// Two providers, selected by <c>CharonConfig.NavProvider</c>:
/// - <b>vnavmesh</b> (default): the original integration; same endpoints Daedalus's VNavService
///   uses, all synchronous.
/// - <b>Ariadne</b>: same movement gate shapes under the <c>Ariadne.</c> prefix (documented in
///   Ariadne's docs/consumer-ipc.md). Readiness is <c>IsConnected</c> + <c>ZoneStatus</c> 2/3 —
///   there is deliberately no Nav.IsReady twin. The reachability query is ASYNC under this
///   prefix (it crosses a pipe), so <see cref="IsReachable"/> answers from the last completed
///   query and fails open while one is in flight.
///
/// Fail-open throughout: when the selected provider is not installed or not ready, every call is
/// a safe no-op and <see cref="IsAvailable"/> is false — features then degrade exactly as they
/// always have (boarding needs the toons to already stand near the mount, follow holds, etc.).
/// </summary>
public sealed class NavClient
{
    private const int ProviderVnav = 0;
    private const int ProviderAriadne = 1;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Func<int> _provider;
    private readonly IPluginLog _log;

    private int _builtFor = -1;

    // Shared shapes (identical under both prefixes).
    private ICallGateSubscriber<bool>? _pathIsRunning;
    private ICallGateSubscriber<Vector3, bool, float, bool>? _pathfindAndMoveCloseTo;
    private ICallGateSubscriber<object>? _pathStop;

    // vnavmesh-only.
    private ICallGateSubscriber<bool>? _vnavIsReady;
    private ICallGateSubscriber<Vector3, float, float, Vector3?>? _vnavNearestReachable;

    // Ariadne-only.
    private ICallGateSubscriber<bool>? _ariadneIsConnected;
    private ICallGateSubscriber<int>? _ariadneZoneStatus;
    private ICallGateSubscriber<Vector3, float, float, Task<Vector3?>>? _ariadneNearestReachable;

    // The in-flight/last async reachability answer (Ariadne mode). Keyed loosely by destination:
    // a new destination retires the old query.
    private Task<Vector3?>? _reachTask;
    private Vector3 _reachDest;

    public NavClient(IDalamudPluginInterface pluginInterface, Func<int> provider, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _provider = provider;
        _log = log;
    }

    public string ProviderName => _provider() == ProviderAriadne ? "Ariadne" : "vnavmesh";

    public bool IsAvailable
    {
        get
        {
            EnsureSubscribers();
            if (_builtFor == ProviderAriadne)
            {
                if (!IsPluginLoaded("Ariadne"))
                    return false;

                // Ariadne's documented readiness: pipe up AND this zone's mesh answered
                // (2 LocalCurrent / 3 MnemosyneCached). No Nav.IsReady twin exists on purpose.
                return TryInvoke(() => _ariadneIsConnected!.InvokeFunc())
                       && TryInvoke(() => _ariadneZoneStatus!.InvokeFunc() is 2 or 3);
            }

            return IsPluginLoaded("vnavmesh") && TryInvoke(() => _vnavIsReady!.InvokeFunc());
        }
    }

    public bool IsPathRunning
    {
        get
        {
            EnsureSubscribers();
            return TryInvoke(() => _pathIsRunning!.InvokeFunc());
        }
    }

    /// <summary>Pathfind and walk to within <paramref name="range"/> yalms of the destination.</summary>
    public bool MoveCloseTo(Vector3 destination, float range)
    {
        EnsureSubscribers();
        return TryInvoke(() => _pathfindAndMoveCloseTo!.InvokeFunc(destination, false, range));
    }

    public void Stop()
    {
        EnsureSubscribers();
        try
        {
            _pathStop!.InvokeAction();
        }
        catch
        {
            // fail-open
        }
    }

    /// <summary>
    /// True when <paramref name="destination"/> can actually be walked to from where we stand.
    /// Catches the portal case: a leader who took a teleport stone can be visible and close on
    /// the map yet sit on a disconnected navmesh island — the query then returns the nearest
    /// point reachable from US, which is far from them (or null). FAIL-OPEN: any error/absence
    /// returns true so following never breaks on a query hiccup.
    ///
    /// vnavmesh answers synchronously; Ariadne's query crosses a pipe and is async, so in that
    /// mode the answer comes from the last completed query for this destination (callers already
    /// throttle to ~1.5s, so the next check reads the settled result).
    /// </summary>
    public bool IsReachable(Vector3 destination, float tolerance = 5f)
    {
        EnsureSubscribers();
        try
        {
            if (_builtFor == ProviderAriadne)
                return IsReachableAriadne(destination, tolerance);

            var reachable = _vnavNearestReachable!.InvokeFunc(destination, tolerance, tolerance);
            if (reachable == null)
                return false;

            return Vector3.Distance(reachable.Value, destination) <= tolerance;
        }
        catch
        {
            return true; // endpoint unavailable — don't block following
        }
    }

    private bool IsReachableAriadne(Vector3 destination, float tolerance)
    {
        // A new destination (or no query yet) kicks a fresh one; the old answer is irrelevant.
        if (_reachTask == null || Vector3.Distance(_reachDest, destination) > tolerance)
        {
            _reachDest = destination;
            _reachTask = _ariadneNearestReachable!.InvokeFunc(destination, tolerance, tolerance);
            return true; // fail-open while in flight
        }

        if (!_reachTask.IsCompleted)
            return true;

        if (_reachTask.IsFaulted || _reachTask.IsCanceled)
        {
            _reachTask = null;
            return true;
        }

        var reachable = _reachTask.Result;
        _reachTask = null; // consumed — the caller's next throttled check re-queries
        if (reachable == null)
            return false;

        return Vector3.Distance(reachable.Value, destination) <= tolerance;
    }

    private void EnsureSubscribers()
    {
        var provider = _provider() == ProviderAriadne ? ProviderAriadne : ProviderVnav;
        if (provider == _builtFor)
            return;

        // Provider changed (or first use): rebuild everything for the new prefix.
        _builtFor = provider;
        _reachTask = null;
        var prefix = provider == ProviderAriadne ? "Ariadne." : "vnavmesh.";

        _pathIsRunning = _pluginInterface.GetIpcSubscriber<bool>(prefix + "Path.IsRunning");
        _pathfindAndMoveCloseTo = _pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>(prefix + "SimpleMove.PathfindAndMoveCloseTo");
        _pathStop = _pluginInterface.GetIpcSubscriber<object>(prefix + "Path.Stop");

        if (provider == ProviderAriadne)
        {
            _ariadneIsConnected = _pluginInterface.GetIpcSubscriber<bool>("Ariadne.IsConnected");
            _ariadneZoneStatus = _pluginInterface.GetIpcSubscriber<int>("Ariadne.ZoneStatus");
            _ariadneNearestReachable = _pluginInterface.GetIpcSubscriber<Vector3, float, float, Task<Vector3?>>("Ariadne.Query.Mesh.NearestPointReachable");
        }
        else
        {
            _vnavIsReady = _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            _vnavNearestReachable = _pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
        }

        _log.Info("NavClient: provider set to {0}", ProviderName);
    }

    private bool IsPluginLoaded(string internalName) =>
        _pluginInterface.InstalledPlugins.Any(p =>
            (p.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase)
             || p.Name.Equals(internalName, StringComparison.OrdinalIgnoreCase))
            && p.IsLoaded);

    private static bool TryInvoke(Func<bool> func)
    {
        try
        {
            return func();
        }
        catch
        {
            return false;
        }
    }
}
