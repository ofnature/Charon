using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charon.Ipc;

/// <summary>
/// Codec for the <c>charon.fleet</c> relay channel: fleet-leader designation and fleet-wide
/// commands.
///
/// This is the RELAY, not a Dalamud IPC gate — IPC only reaches plugins inside the same game
/// client, whereas the leader designation has to reach every box on the LAN.
///
/// Leave-duty is commanded explicitly rather than inferred: watching a leader's territory cannot
/// tell a deliberate exit from a disconnect, and the two want opposite responses.
/// </summary>
public sealed class FleetMessage
{
    /// <summary>Character that sent the frame — used to trust-gate it.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary><see cref="FleetRelay.ActLeaveDuty"/> or <see cref="FleetRelay.ActSetLeader"/>.</summary>
    [JsonPropertyName("act")]
    public string Act { get; set; } = string.Empty;

    /// <summary>
    /// The fleet leader this frame concerns: the designated toon for <c>setleader</c>, the issuing
    /// leader for <c>leave</c>. Kept separate from <see cref="From"/> because any box may nominate
    /// a DIFFERENT toon as leader (picking the roommate's character, say).
    /// </summary>
    [JsonPropertyName("leader")]
    public string Leader { get; set; } = string.Empty;
}

public static class FleetRelay
{
    /// <summary>Leave the duty you are in.</summary>
    public const string ActLeaveDuty = "leave";

    /// <summary>Adopt <see cref="FleetMessage.Leader"/> as the fleet leader on every box.</summary>
    public const string ActSetLeader = "setleader";

    public static string Serialize(string from, string act, string leader) =>
        JsonSerializer.Serialize(new FleetMessage { From = from, Act = act, Leader = leader });

    /// <summary>Parse a relay frame. Null on anything malformed — never throws at the caller.</summary>
    public static FleetMessage? Parse(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<FleetMessage>(json);
            return message == null || message.From.Length == 0 || message.Act.Length == 0 ? null : message;
        }
        catch
        {
            return null;
        }
    }
}
