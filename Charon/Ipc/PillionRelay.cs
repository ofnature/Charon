using System.Text.Json;
using System.Text.Json.Serialization;

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
/// Codec for owner-authoritative pillion assignments on the <c>charon.pillion</c> relay
/// channel (Services/RelayClient). Transport-neutral JSON; the tolerant parser makes the
/// schema extend-only. The former per-process Dalamud-IPC broadcast was retired — the LAN
/// relay's loopback mirror reaches same-machine siblings too.
/// </summary>
public static class PillionRelay
{
    public static string Serialize(string ownerName, string memberName, int seatIndex) =>
        JsonSerializer.Serialize(new PillionAssignmentMessage
        {
            OwnerName = ownerName,
            MemberName = memberName,
            SeatIndex = seatIndex,
        });

    /// <summary>Tolerant parse: null on bad/incomplete JSON, never an exception.</summary>
    public static PillionAssignmentMessage? Parse(string? json)
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
}
