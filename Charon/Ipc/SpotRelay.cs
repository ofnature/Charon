using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Charon.Features.DeepDungeon;

namespace Charon.Ipc;

/// <summary>
/// A frame on the <c>charon.spots</c> channel — the fleet's replacement for Palace Pal's dead server.
///
/// Three acts:
///   - <see cref="SpotRelay.ActSeen"/>: spots a box has just SEEN (Sight/Intuition revealed them).
///     Everyone adds them; nobody re-broadcasts, so a frame cannot loop.
///   - <see cref="SpotRelay.ActAsk"/>: "I just entered floor set T and know N spots there."
///   - <see cref="SpotRelay.ActSpots"/>: one batch of an answer to an ask, carrying the answerer's total
///     so other would-be answerers can stand down (see <see cref="SpotRelay.ShouldAnswer"/>).
///
/// The relay is a single UDP broadcast per frame with no splitting (Daedalus <c>LanCoordinator.Send</c>),
/// so spots travel in batches of <see cref="SpotRelay.BatchSize"/> (about 2 KB). Coordinates go UNROUNDED:
/// rounding could carry a spot across a 0.2y grid line and turn one spot into two.
/// </summary>
public sealed class SpotMessage
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("act")]
    public string Act { get; set; } = string.Empty;

    /// <summary>Ties answers to the ask that caused them.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>TerritoryType of the floor set.</summary>
    [JsonPropertyName("t")]
    public uint Territory { get; set; }

    /// <summary>How many spots the sender knows in that territory.</summary>
    [JsonPropertyName("n")]
    public int Known { get; set; }

    /// <summary>Rows of [kind, x, y, z, seen].</summary>
    [JsonPropertyName("s")]
    public List<float[]> Spots { get; set; } = new();
}

public static class SpotRelay
{
    public const string ActSeen = "seen";
    public const string ActAsk = "ask";
    public const string ActSpots = "spots";

    public const int BatchSize = 40;

    private static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static string Seen(string from, uint territory, IEnumerable<Spot> spots) =>
        Serialize(new SpotMessage { From = from, Act = ActSeen, Territory = territory, Spots = Rows(spots) });

    public static string Ask(string from, string id, uint territory, int known) =>
        Serialize(new SpotMessage { From = from, Act = ActAsk, Id = id, Territory = territory, Known = known });

    /// <summary>An answer, already split into frames.</summary>
    public static List<string> Answer(string from, string id, uint territory, IReadOnlyCollection<Spot> spots) =>
        spots.Chunk(BatchSize)
            .Select(chunk => Serialize(new SpotMessage
            {
                From = from, Act = ActSpots, Id = id, Territory = territory, Known = spots.Count, Spots = Rows(chunk),
            }))
            .ToList();

    public static SpotMessage? Parse(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<SpotMessage>(json, Options);
            return message is { Act.Length: > 0 } ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The spots a frame carries; malformed rows are dropped, never guessed at.</summary>
    public static IEnumerable<Spot> SpotsOf(SpotMessage message)
    {
        foreach (var row in message.Spots ?? new List<float[]>())
        {
            if (row is { Length: >= 5 } && row[0] is 1f or 2f)
                yield return new Spot((SpotKind)(byte)row[0], row[1], row[2], row[3], row[4] != 0f);
        }
    }

    /// <summary>
    /// Whether this box should answer an ask: only when it knows MORE than the asker, and only when no
    /// other answer to the same ask has arrived from a box that knows at least as much. Every box on the
    /// LAN hears every ask, and without this rule all of them would answer at once.
    /// </summary>
    public static bool ShouldAnswer(int mine, int askerKnows, int bestOtherAnswer) =>
        mine > askerKnows && mine > bestOtherAnswer;

    private static List<float[]> Rows(IEnumerable<Spot> spots) =>
        spots.Select(s => new[] { (float)s.Kind, s.X, s.Y, s.Z, s.Seen ? 1f : 0f }).ToList();

    private static string Serialize(SpotMessage message) => JsonSerializer.Serialize(message, Options);
}
