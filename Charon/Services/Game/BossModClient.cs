using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Charon.Services.Game;

/// <summary>
/// Reads BossMod Reborn's active-module state over IPC (<c>BossMod.HasActiveModule</c> — the
/// exact signal BMR's own AI-follow gates on: <c>ActiveModule?.StateMachine.ActiveState != null</c>).
/// Fail-open: when BMR isn't installed there are no boss modules, so this returns false and
/// follow is never gated by it.
/// </summary>
public sealed class BossModClient
{
    private readonly ICallGateSubscriber<bool> _hasActiveModule;

    public BossModClient(IDalamudPluginInterface pluginInterface)
    {
        _hasActiveModule = pluginInterface.GetIpcSubscriber<bool>("BossMod.HasActiveModule");
    }

    /// <summary>True when a BMR boss module is active. False when BMR is absent or between fights.</summary>
    public bool HasActiveModule
    {
        get
        {
            try
            {
                return _hasActiveModule.InvokeFunc();
            }
            catch
            {
                return false; // BMR not loaded / endpoint unavailable
            }
        }
    }
}
