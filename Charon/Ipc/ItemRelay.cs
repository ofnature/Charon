using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charon.Ipc;

/// <summary>One place a stack of an item is sitting, on the box that answered.</summary>
public sealed class RelayHolding
{
    /// <summary>Where it is: bags, saddlebag, chest, or a named retainer.</summary>
    [JsonPropertyName("where")]
    public string Where { get; set; } = string.Empty;

    [JsonPropertyName("nq")]
    public int Nq { get; set; }

    [JsonPropertyName("hq")]
    public int Hq { get; set; }
}

/// <summary>
/// A frame on the <c>charon.items</c> channel: either an ASK ("who has item N?") or the ANSWER a
/// box sends back about itself.
///
/// Why the relay and not IPC: Dalamud IPC reaches only plugins inside the SAME client, and XA
/// Database's store is one SQLite file per MACHINE. Neither can answer across two PCs, and the
/// fleet spans two. The relay is the only channel Charon has that crosses them.
/// </summary>
public sealed class ItemMessage
{
    /// <summary>Character that sent the frame.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary><see cref="ItemRelay.ActAsk"/> or <see cref="ItemRelay.ActAnswer"/>.</summary>
    [JsonPropertyName("act")]
    public string Act { get; set; } = string.Empty;

    /// <summary>Ties an answer to the ask that caused it, so a late reply cannot pollute a new search.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; set; }

    /// <summary>
    /// Whether the answering character can RECEIVE or HAND OVER items at all. A free trial account
    /// cannot trade, use the market board or join a free company, so it can neither be sent
    /// materials nor contribute them. A planner that ignored this would build a plan the game
    /// refuses, so it travels with every answer rather than being looked up later.
    /// </summary>
    [JsonPropertyName("canTrade")]
    public bool CanTrade { get; set; }

    /// <summary>When the answering box last actually SAW these containers (ISO-8601 UTC).</summary>
    [JsonPropertyName("seenUtc")]
    public string SeenUtc { get; set; } = string.Empty;

    [JsonPropertyName("holdings")]
    public List<RelayHolding> Holdings { get; set; } = new();
}

public static class ItemRelay
{
    /// <summary>"Who has this item?" — broadcast to every box.</summary>
    public const string ActAsk = "ask";

    /// <summary>"Here is what I hold" — one box's reply.</summary>
    public const string ActAnswer = "answer";

    /// <summary>Where a holding lives. Strings, because they cross a wire and outlive an enum.</summary>
    public const string WhereBags = "bags";
    public const string WhereSaddlebag = "saddlebag";
    public const string WhereChest = "fcchest";

    /// <summary>A retainer's own bags — the name follows, as <c>retainer:T'sola</c>.</summary>
    public const string WhereRetainerPrefix = "retainer:";

    public static string Ask(string from, string id, uint itemId) =>
        JsonSerializer.Serialize(new ItemMessage { From = from, Act = ActAsk, Id = id, ItemId = itemId });

    public static string Answer(
        string from, string id, uint itemId, bool canTrade, string seenUtc, List<RelayHolding> holdings) =>
        JsonSerializer.Serialize(new ItemMessage
        {
            From = from,
            Act = ActAnswer,
            Id = id,
            ItemId = itemId,
            CanTrade = canTrade,
            SeenUtc = seenUtc,
            Holdings = holdings,
        });

    /// <summary>Parses a frame, or null. A malformed frame is ignored, never thrown at the tick.</summary>
    public static ItemMessage? Parse(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ItemMessage>(json);
            if (message == null || message.Act.Length == 0 || message.From.Length == 0)
                return null;
            return message;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
