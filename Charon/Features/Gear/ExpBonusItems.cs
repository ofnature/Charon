using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Gear;

/// <summary>One piece of EXP-bonus gear, with the bonus text shown in the cleanup preview.</summary>
public sealed record ExpBonusItem(uint ItemId, string Name, string Bonus);

/// <summary>
/// Equipment that grants a passive EXP bonus while worn. These are exactly the items an armoury
/// cleanup must not throw out: they belong to no gearset (nobody builds a gearset around a level-10
/// hat), they live in the armoury forever, and several are unobtainable pre-order rewards that
/// cannot be re-acquired if lost. They seed the never-evict list on first run.
///
/// Item ids resolved against XIVAPI, not from memory — the near misses matter here: "Menphina's
/// Ring" (4414) and the "Ala Mhigan Earrings of Aiming" family (19183-19187) are ordinary gear with
/// no EXP bonus, and a name-based guess would have swept them in.
///
/// This list is a floor, not a ceiling: a future expansion's pre-order earring will not be here
/// until it is added, and the user can always tick Keep on anything else.
/// </summary>
public static class ExpBonusItems
{
    public static readonly IReadOnlyList<ExpBonusItem> All =
    [
        new(2632, "Garlond Goggles", "+20% EXP below level 11"),
        new(2633, "Moogle Cap", "+20% EXP below level 11"),
        new(2634, "Helm of Light", "+20% EXP below level 11"),
        new(8567, "Friendship Circlet", "+20% EXP below level 26"),
        new(14043, "Brand-new Ring", "+30% EXP below level 31"),
        new(16039, "Ala Mhigan Earrings", "+30% EXP below level 51"),
        new(24589, "Aetheryte Earring", "+30% EXP below level 71"),
        new(31393, "Bozjan Earring", "+30% EXP below level 71"),
        new(33648, "Menphina's Earring", "+30% EXP below level 81"),
        new(41081, "Azeyma's Earrings", "+30% EXP below level 91"),
        new(44410, "Neophyte's Ring", "bonus EXP while partied with a mentor"),
    ];

    /// <summary>Ids only — what seeds the never-evict list.</summary>
    public static IReadOnlyList<uint> ItemIds { get; } = All.Select(i => i.ItemId).ToList();

    private static readonly HashSet<uint> Lookup = new(ItemIds);

    public static bool Contains(uint itemId) => Lookup.Contains(itemId);

    /// <summary>The bonus blurb for an id, or empty when it is not EXP gear.</summary>
    public static string BonusFor(uint itemId) =>
        All.FirstOrDefault(i => i.ItemId == itemId)?.Bonus ?? string.Empty;
}
