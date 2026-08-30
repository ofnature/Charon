using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Charon.Services.Game;

namespace Charon.Ipc;

/// <summary>
/// The text-advance lease for other plugins — built for Odysseus, which auto-advances dialogue
/// itself but only while its own run loop is alive; this lets it delegate that to Charon
/// globally instead.
///
/// | Charon.TextAdvance.Force     | Func&lt;int, bool&gt; | force ON for N seconds (capped 300; re-call to refresh); 0 releases |
/// | Charon.TextAdvance.IsEnabled | Func&lt;bool&gt;      | effective state (toggle OR live lease)                              |
///
/// The lease is deliberately a TTL, not a switch: a caller that crashes mid-quest simply stops
/// refreshing and the lease expires, so dialogue can never stay hijacked on an undriven box.
/// Callers refresh well inside their window (e.g. Force(60) every 30s) and Force(0) on stop.
/// </summary>
public sealed class TextAdvanceIpc : IDisposable
{
    private readonly ICallGateProvider<int, bool> _force;
    private readonly ICallGateProvider<bool> _isEnabled;

    public TextAdvanceIpc(IDalamudPluginInterface pluginInterface, TextAdvancer advancer)
    {
        _force = pluginInterface.GetIpcProvider<int, bool>("Charon.TextAdvance.Force");
        _isEnabled = pluginInterface.GetIpcProvider<bool>("Charon.TextAdvance.IsEnabled");

        _force.RegisterFunc(seconds =>
        {
            advancer.Force(seconds);
            return true;
        });
        _isEnabled.RegisterFunc(() => advancer.EffectivelyEnabled);
    }

    public void Dispose()
    {
        _force.UnregisterFunc();
        _isEnabled.UnregisterFunc();
    }
}
