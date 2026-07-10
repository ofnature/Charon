using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Charon.Ipc;

/// <summary>One seat assignment broadcast by the mount owner's Charon instance.</summary>
public sealed class PillionAssignmentMessage
{
    [JsonPropertyName("owner")]
    public string OwnerName { get; set; } = string.Empty;

    [JsonPropertyName("member")]
    public string MemberName { get; set; } = string.Empty;

    [JsonPropertyName("seat")]
    public int SeatIndex { get; set; }
}

/// <summary>
/// Charon↔Charon IPC for pillion seat assignments (endpoint <c>Charon.Pillion.SeatAssignment</c>,
/// JSON payload). The owner's instance broadcasts an assignment; an instance whose local character
/// is the named member answers by riding the owner's mount on the assigned seat.
///
/// Note: Dalamud IPC call gates are per-process — this reaches Charon instances in the same game
/// client today. Cross-client delivery (multibox) needs a relay over the Daedalus LAN
/// CoordinationBus once Daedalus exposes one; the message format here is already transport-neutral.
/// </summary>
public sealed class CharonPillionIpc : IDisposable
{
    internal const string SeatAssignmentEndpoint = "Charon.Pillion.SeatAssignment";

    private readonly ICallGateProvider<string, object> _seatAssignment;
    private readonly IPluginLog _log;

    /// <summary>Raised on the receiving side for every assignment broadcast (including our own).</summary>
    public event Action<PillionAssignmentMessage>? OnAssignmentReceived;

    public CharonPillionIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;
        _seatAssignment = pluginInterface.GetIpcProvider<string, object>(SeatAssignmentEndpoint);
        _seatAssignment.RegisterAction(OnMessage);
    }

    public void BroadcastAssignment(string ownerName, string memberName, int seatIndex)
    {
        try
        {
            var json = JsonSerializer.Serialize(new PillionAssignmentMessage
            {
                OwnerName = ownerName,
                MemberName = memberName,
                SeatIndex = seatIndex,
            });
            _seatAssignment.SendMessage(json);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to broadcast pillion seat assignment");
        }
    }

    private void OnMessage(string json)
    {
        var message = Parse(json);
        if (message != null)
            OnAssignmentReceived?.Invoke(message);
    }

    /// <summary>Tolerant parse: null on bad/incomplete JSON, never an exception.</summary>
    internal static PillionAssignmentMessage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var message = JsonSerializer.Deserialize<PillionAssignmentMessage>(json);
            if (message == null
                || message.OwnerName.Length == 0
                || message.MemberName.Length == 0
                || message.SeatIndex < 1) // passenger seats are 1-based
                return null;
            return message;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _seatAssignment.UnregisterAction();
    }
}
