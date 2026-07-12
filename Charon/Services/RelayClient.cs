using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Charon.Services;

/// <summary>
/// Cross-client Charon messaging via the Daedalus LAN relay
/// (<c>Daedalus.Relay.Publish</c> / <c>Daedalus.Relay.Message</c>, see
/// D:\Dev\Olympus\.cursor\rules\charon-lan-integration.md). The relay ferries opaque
/// {channel, json} frames over the CoordinationBus UDP broadcast, reaching BOTH other
/// machines and same-machine sibling game clients (loopback mirror).
///
/// Semantics to respect:
/// - The PUBLISHER never receives its own frame — no flow may rely on hearing itself.
/// - Publish is a silent no-op while Daedalus is absent or its LAN coordinator is off;
///   consumers must keep an observation-based fallback (pillion self-boarding does).
/// - Messages arrive on the framework thread.
/// </summary>
public sealed class RelayClient : IDisposable
{
    /// <summary>Owner-authoritative pillion seat assignments (PillionAssignmentMessage JSON).</summary>
    public const string PillionChannel = "charon.pillion";

    /// <summary>Rally-to-flag broadcasts (roadmap #6 — not yet implemented).</summary>
    public const string RallyChannel = "charon.rally";

    /// <summary>Assemble-party triggers (roadmap #1 — not yet implemented).</summary>
    public const string AssembleChannel = "charon.assemble";

    private readonly ICallGateSubscriber<string, string, object?> _publish;
    private readonly ICallGateSubscriber<string, string, object?> _message;
    private readonly IPluginLog _log;

    /// <summary>Raised on the framework thread for every relay frame from another client.</summary>
    public event Action<string, string>? OnMessage;

    public RelayClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;
        _publish = pluginInterface.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Publish");
        _message = pluginInterface.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Message");
        _message.Subscribe(OnRelayMessage);
    }

    /// <summary>Broadcast to every other client on the LAN bus. Fail-open no-op when Daedalus is absent.</summary>
    public void Publish(string channel, string json)
    {
        try
        {
            _publish.InvokeAction(channel, json);
        }
        catch
        {
            // Daedalus not loaded — observation-based fallbacks carry the feature.
        }
    }

    private void OnRelayMessage(string channel, string json)
    {
        try
        {
            OnMessage?.Invoke(channel, json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Relay message handler failed (channel '{0}')", channel);
        }
    }

    public void Dispose()
    {
        _message.Unsubscribe(OnRelayMessage);
    }
}
