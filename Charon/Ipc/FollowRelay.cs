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

    /// <summary>"start", "stop", "state", or "portal".</summary>
    [JsonPropertyName("act")]
    public string Act { get; set; } = string.Empty;

    // --- "portal" only: where the sender stood when they transitioned, and what was there. ---

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }

    /// <summary>
    /// Sheet id of the interactable the sender was standing at — Dalamud's <c>IGameObject.BaseId</c>
    /// (the property formerly called DataId; the wire name predates the rename). Identical on every
    /// client, unlike the per-session entity id, so a receiver can confirm it is clicking the same
    /// object rather than whatever else happens to be nearby. 0 when unknown.
    /// </summary>
    [JsonPropertyName("obj")]
    public uint DataId { get; set; }
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

    /// <summary>
    /// A toon REPORTING its own follow state rather than being commanded: <c>Target</c> is the
    /// sender and <c>Leader</c> is whoever it currently follows (empty when it follows nobody).
    ///
    /// Follow state lives on each toon's own client, so the fleet view has no way to show who the
    /// other seven are trailing unless they say so. Announced on every change, on load, and on a
    /// slow refresh — a display that is silently stale is worse than no display at all.
    /// </summary>
    public const string ActState = "state";

    /// <summary>
    /// A toon ANNOUNCING that it just used an interactable to go somewhere — a spatial rift, raid
    /// portal or lift. Like <see cref="ActState"/> this is about its SENDER, so <c>Target</c> is
    /// the announcer and the frame is broadcast rather than addressed.
    ///
    /// This exists because inference is not good enough. A rift removes the leader from the object
    /// table with no second position to compare, and one that lands somewhere still walkable looks
    /// like ordinary movement — a follower would trudge the long way round or give up. The toon
    /// that actually used the thing is the only authoritative source for "a transition happened,
    /// and it happened HERE", so it says so instead of leaving seven other boxes to guess.
    /// </summary>
    public const string ActPortal = "portal";

    public static string Serialize(string leader, string target, string act) =>
        JsonSerializer.Serialize(new FollowMessage { Leader = leader, Target = target, Act = act });

    /// <summary>Announce a transition: <paramref name="sender"/> stood at (x,y,z) and used something.</summary>
    public static string SerializePortal(string sender, float x, float y, float z, uint dataId) =>
        JsonSerializer.Serialize(new FollowMessage
        {
            Target = sender,
            Act = ActPortal,
            X = x,
            Y = y,
            Z = z,
            DataId = dataId,
        });

    public static FollowMessage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var message = JsonSerializer.Deserialize<FollowMessage>(json);
            if (message == null
                || message.Target.Length == 0
                || (message.Act != ActStart && message.Act != ActStop
                    && message.Act != ActState && message.Act != ActPortal)
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
