using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Charon.Features.GrandCompany;

/// <summary>
/// A hand-off to Hephaestus: the JSON for its <c>CraftList</c>/<c>AddToQueue</c> gates, plus what had to be
/// left out and why.
///
/// TWO RULES, both from the other side's contract:
/// <list type="number">
/// <item>Only SUPPLY rows are crafts. A provisioning row is a gathered item (Raw Black Star, Pearl Grass), it
/// resolves to no recipe, and Hephaestus's request is ALL OR NOTHING — one entry it cannot make rejects the
/// whole list. Sending the gathering rows would therefore send nothing at all.</item>
/// <item>Quantities are the SHORTFALL (asked minus held, bags and retainers), and a row already covered is left
/// out rather than ordered again. Asking Hephaestus to make what is already in the bag is how a queue fills with
/// work nobody wanted.</item>
/// </list>
///
/// Pure: the caller supplies the snapshot and the held counts, so the payload is tested without a client.
/// </summary>
/// <param name="Json">The request body, or empty when there is nothing to send.</param>
/// <param name="Crafts">How many craft entries it carries.</param>
/// <param name="Units">Total units across those entries.</param>
/// <param name="Excluded">One line per reason something was left out — so a short hand-off explains itself.</param>
public sealed record CraftHandoff(string Json, int Crafts, int Units, IReadOnlyList<string> Excluded)
{
    private const string KeepOrderJsonName = "keepOrder";
    private const string ItemsJsonName = "items";
    private const string ItemIdJsonName = "itemId";
    private const string QuantityJsonName = "quantity";

    /// <summary>Hephaestus's wire format is camelCase: <c>keepOrder</c>, <c>items</c>, <c>itemId</c>, <c>quantity</c>.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Entry(uint ItemId, int Quantity);

    private sealed record Request(bool KeepOrder, List<Entry> Items);

    public bool Any => Crafts > 0;

    /// <summary>
    /// Build the hand-off. <paramref name="held"/> answers how many of an item the character already has (bags
    /// plus retainers), which is what turns "asked 20" into "make 12".
    /// </summary>
    public static CraftHandoff Build(GcRequestSnapshot? snapshot, Func<uint, int> held)
    {
        if (snapshot == null)
            return new CraftHandoff(string.Empty, 0, 0, ["no request list has been captured yet"]);

        var excluded = new List<string>();

        var crafts = snapshot.SupplyRows()
            .Select(e => (Entry: e, Shortfall: Math.Max(0, e.Requested - held(e.ItemId))))
            .Where(x => x.Shortfall > 0)
            .ToList();

        var covered = snapshot.SupplyRows().Count - crafts.Count;
        if (covered > 0)
            excluded.Add($"{covered} supply item(s) already covered by bags and retainers");

        var gathering = snapshot.ProvisioningRows().Count;
        if (gathering > 0)
        {
            excluded.Add($"{gathering} provisioning item(s) are gathered, not crafted — Hephaestus takes crafts "
                         + "only, and its request is all-or-nothing, so sending them would reject the whole list");
        }

        if (crafts.Count == 0)
        {
            excluded.Add("nothing to make");
            return new CraftHandoff(string.Empty, 0, 0, excluded);
        }

        var request = new Request(true, crafts.Select(c => new Entry(c.Entry.ItemId, c.Shortfall)).ToList());

        return new CraftHandoff(
            JsonSerializer.Serialize(request, JsonOptions),
            crafts.Count,
            crafts.Sum(c => c.Shortfall),
            excluded);
    }

    /// <summary>One line for the UI: what this would ask for, and what it is leaving behind.</summary>
    public static string Describe(CraftHandoff handoff)
    {
        var left = handoff.Excluded.Count > 0 ? $" — {string.Join("; ", handoff.Excluded)}" : string.Empty;

        return handoff.Any
            ? $"{handoff.Crafts} craft(s), {handoff.Units} unit(s){left}"
            : $"nothing to hand over{left}";
    }

    /// <summary>The JSON field names, so a test can assert the wire format without hard-coding it twice.</summary>
    public static IReadOnlyList<string> WireFieldNames =>
        [KeepOrderJsonName, ItemsJsonName, ItemIdJsonName, QuantityJsonName];
}
