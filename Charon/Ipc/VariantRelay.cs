using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charon.Ipc;

/// <summary>One frame on the <c>charon.variant</c> relay channel.</summary>
public sealed class VariantMessage
{
    /// <summary>"state" (the sender's own record) or "ask" (send me yours).</summary>
    [JsonPropertyName("act")]
    public string Act { get; set; } = string.Empty;

    /// <summary>The sender's character name — a state frame is ABOUT its sender.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>"state" only: the game's completion bit array, as hex.</summary>
    [JsonPropertyName("bits")]
    public string Bits { get; set; } = string.Empty;
}

/// <summary>
/// Codec for sharing variant-dungeon route records across the fleet. Each box reports its OWN record
/// (only its own client can read it) on change, on load and on a slow refresh, and answers an "ask" with
/// it — so a box entering a variant dungeon learns the party's records in a second instead of a minute.
/// Tolerant parse: bad JSON, an unknown act, a nameless frame or malformed bits is null.
/// </summary>
public static class VariantRelay
{
    public const string ActState = "state";
    public const string ActAsk = "ask";

    /// <summary>Far more than the game's 7 bytes, so a future patch adding routes still fits; anything bigger is junk.</summary>
    public const int MaxBytes = 32;

    public static string SerializeState(string name, byte[] bits) =>
        JsonSerializer.Serialize(new VariantMessage { Act = ActState, Name = name, Bits = Convert.ToHexString(bits) });

    public static string SerializeAsk(string name) =>
        JsonSerializer.Serialize(new VariantMessage { Act = ActAsk, Name = name });

    public static VariantMessage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var message = JsonSerializer.Deserialize<VariantMessage>(json);
            if (message == null || message.Name.Length == 0)
                return null;
            if (message.Act == ActAsk)
                return message;
            if (message.Act != ActState || DecodeBits(message.Bits) == null)
                return null;
            return message;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The bits of a state frame, or null when they are not hex or are implausibly long.</summary>
    public static byte[]? DecodeBits(string hex)
    {
        if (hex.Length == 0 || hex.Length % 2 != 0 || hex.Length > MaxBytes * 2)
            return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
