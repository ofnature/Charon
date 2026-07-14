using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charon.Ipc;

/// <summary>One follow command broadcast over the LAN relay.</summary>
public sealed class FollowMessage
{
    /// <summary>Who to follow (the sender of the command).</summary>
    [JsonPropertyName("leader")]
    public string Leader { get; set; } = string.Empty;

    /// <summary>Which toon this command is addressed to.</summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>"start" or "stop".</summary>
    [JsonPropertyName("act")]
    public string Act { get; set; } = string.Empty;
}

/// <summary>
/// Codec for Fleet Follow commands on the <c>charon.follow</c> relay channel
/// (<see cref="Charon.Services.RelayClient"/>). A toon acts only on commands whose
/// <see cref="FollowMessage.Target"/> is its own character. Tolerant parse — bad JSON is null.
/// </summary>
public static class FollowRelay
{
    public const string ActStart = "start";
    public const string ActStop = "stop";

    public static string Serialize(string leader, string target, string act) =>
        JsonSerializer.Serialize(new FollowMessage { Leader = leader, Target = target, Act = act });

    public static FollowMessage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var message = JsonSerializer.Deserialize<FollowMessage>(json);
            if (message == null
                || message.Target.Length == 0
                || (message.Act != ActStart && message.Act != ActStop)
                || (message.Act == ActStart && message.Leader.Length == 0))
                return null;
            return message;
        }
        catch
        {
            return null;
        }
    }
}
