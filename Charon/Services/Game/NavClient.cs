using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Minimal vnavmesh IPC client for the short walk to a mount owner before boarding.
/// Same endpoints Daedalus's VNavService uses. Fail-open: when vnavmesh is not installed or
/// not ready, every call is a safe no-op and <see cref="IsAvailable"/> is false — boarding
/// then simply requires the toons to already stand near the mount.
/// </summary>
public sealed class NavClient
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;

    private ICallGateSubscriber<bool>? _navIsReady;
    private ICallGateSubscriber<bool>? _pathIsRunning;
    private ICallGateSubscriber<Vector3, bool, float, bool>? _pathfindAndMoveCloseTo;
    private ICallGateSubscriber<object>? _pathStop;
    private ICallGateSubscriber<Vector3, float, float, Vector3?>? _nearestPointReachable;

    public NavClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    public bool IsAvailable
    {
        get
        {
            if (!IsPluginLoaded("vnavmesh"))
                return false;

            EnsureSubscribers();
            return TryInvoke(() => _navIsReady!.InvokeFunc());
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
    /// True when <paramref name="destination"/> can actually be walked to from where we stand
    /// (<c>vnavmesh.Query.Mesh.NearestPointReachable</c>). Catches the portal case: a leader who
    /// took a teleport stone can be visible and close on the map yet sit on a disconnected
    /// navmesh island — the query then returns the nearest point reachable from US, which is far
    /// from them (or null). FAIL-OPEN: any error/absence returns true so following never breaks
    /// on a query hiccup.
    /// </summary>
    public bool IsReachable(Vector3 destination, float tolerance = 5f)
    {
        EnsureSubscribers();
        try
        {
            var reachable = _nearestPointReachable!.InvokeFunc(destination, tolerance, tolerance);
            if (reachable == null)
                return false;

            return Vector3.Distance(reachable.Value, destination) <= tolerance;
        }
        catch
        {
            return true; // endpoint unavailable (older vnavmesh) — don't block following
        }
    }

    private void EnsureSubscribers()
    {
        _navIsReady ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _pathIsRunning ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _pathfindAndMoveCloseTo ??= _pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _pathStop ??= _pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        _nearestPointReachable ??= _pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
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
