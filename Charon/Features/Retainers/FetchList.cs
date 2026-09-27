using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Charon.Features.Retainers;

/// <summary>One item a caller wants back out of a retainer.</summary>
public sealed record FetchRequest(uint ItemId, int Quantity, bool HighQuality);

/// <summary>
/// Parses a shopping list for <c>Charon.Retainers.RequestFetchList</c>. Pure — no Dalamud types.
///
/// Shape, matching the lists Hephaestus already sends elsewhere:
/// <code>{ "items": [ { "itemId": 5106, "quantity": 12 } ] }</code>
/// <c>hq</c> is optional per entry and defaults to false, which means "any quality, NQ first" —
/// the same meaning <c>RequestFetch</c> already gives it.
///
/// The list is an INTENTION, not a queue that runs to completion: the caller can Stop, and every
/// item is re-planned against the live store when its turn comes. A stored batch replayed blindly
/// is the shape that produced AutoRetainer's drain-until-done behaviour, and it is not repeated here.
/// </summary>
public static class FetchList
{
    /// <summary>At most this many entries per call — a malformed caller cannot queue thousands.</summary>
    public const int MaxItems = 50;

    /// <summary>
    /// Returns the requests, or an empty list with <paramref name="refusal"/> set. Bad entries are
    /// DROPPED rather than failing the whole list, but a list that ends up empty is a refusal:
    /// answering "fine" to a request that will do nothing is how a caller waits forever.
    /// </summary>
    public static List<FetchRequest> Parse(string? json, out string? refusal)
    {
        refusal = null;
        var items = new List<FetchRequest>();

        if (string.IsNullOrWhiteSpace(json))
        {
            refusal = "no list given";
            return items;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                refusal = "the list has no \"items\" array";
                return items;
            }

            foreach (var entry in array.EnumerateArray())
            {
                if (items.Count >= MaxItems)
                {
                    refusal = $"more than {MaxItems} items — the rest were ignored";
                    break;
                }

                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("itemId", out var idProp)
                    || !idProp.TryGetUInt32(out var itemId)
                    || itemId == 0)
                    continue;

                var quantity = entry.TryGetProperty("quantity", out var qtyProp) && qtyProp.TryGetInt32(out var q)
                    ? q
                    : 0;
                if (quantity <= 0)
                    continue;

                var hq = entry.TryGetProperty("hq", out var hqProp) && hqProp.ValueKind == JsonValueKind.True;
                items.Add(new FetchRequest(itemId, quantity, hq));
            }
        }
        catch (JsonException ex)
        {
            refusal = $"the list is not valid JSON ({ex.Message})";
            return items;
        }

        if (items.Count == 0 && refusal == null)
            refusal = "the list asked for nothing";

        return items;
    }
}
