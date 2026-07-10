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

    private void EnsureSubscribers()
    {
        _navIsReady ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _pathIsRunning ??= _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _pathfindAndMoveCloseTo ??= _pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _pathStop ??= _pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
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
