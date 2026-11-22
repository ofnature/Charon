using System.Text.Json;
using System.Text.Json.Serialization;
using Charon.Features.DeepDungeon;

namespace Charon.Ipc;

/// <summary>One frame on the <c>charon.aetherpool</c> relay channel.</summary>
public sealed class AetherpoolMessage
{
    /// <summary>"state" (the sender's own levels) or "ask" (send me yours).</summary>
    [JsonPropertyName("act")]
    public string Act { get; set; } = string.Empty;

    /// <summary>The sender's character name — a state frame is ABOUT its sender.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>"state" only: which kind of deep dungeon (the game's DeepDungeon row, the same on every client).</summary>
    [JsonPropertyName("dd")]
    public int Dungeon { get; set; }

    [JsonPropertyName("arm")]
    public int Arm { get; set; }

    [JsonPropertyName("armor")]
    public int Armor { get; set; }
}

/// <summary>
/// Codec for sharing aetherpool levels across the fleet (see <see cref="AetherpoolBoard"/>). Tolerant
/// parse: bad JSON, an unknown act, a nameless frame or an impossible level is null.
/// </summary>
public static class AetherpoolRelay
{
    public const string ActState = "state";
    public const string ActAsk = "ask";

    public static string SerializeState(string name, int dungeon, int arm, int armor) =>
        JsonSerializer.Serialize(new AetherpoolMessage { Act = ActState, Name = name, Dungeon = dungeon, Arm = arm, Armor = armor });

    public static string SerializeAsk(string name) =>
        JsonSerializer.Serialize(new AetherpoolMessage { Act = ActAsk, Name = name });

    public static AetherpoolMessage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var message = JsonSerializer.Deserialize<AetherpoolMessage>(json);
            if (message == null || message.Name.Length == 0)
                return null;
            if (message.Act == ActAsk)
                return message;
            if (message.Act != ActState || message.Dungeon <= 0
                || !AetherpoolBoard.Plausible(message.Arm) || !AetherpoolBoard.Plausible(message.Armor))
                return null;
            return message;
        }
        catch
        {
            return null;
        }
    }
}
