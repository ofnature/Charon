using System.Collections.Generic;

namespace Charon.Features.Consumables;

/// <summary>
/// ItemAction kinds that are safe to USE on sight: the item exists only to be consumed, and using
/// it costs nothing because it cannot be sold or traded in the first place.
///
/// This is an ALLOWLIST for the same reason <see cref="Loot.CollectibleKinds"/> is one. The game
/// reports an ordinary potion as "not unlocked" exactly like a genuinely unlearned mount, and a
/// blanket "use every untradable consumable" would drink the potions, eat the food and burn the
/// tickets. A kind earns its place here with evidence, never by inference.
/// </summary>
public static class ConsumableKinds
{
    /// <summary>
    /// Instant Gold Saucer currency. VERIFIED on XIVAPI: the whole MGP family shares this single
    /// ItemAction.Action, each with the amount in Data[0] — MGP Voucher 10131 (100), MGP Bronze
    /// Card 22522 (5,000), MGP Gold Card 14971 (30,000), MGP Platinum Card 16784 (50,000) — and all
    /// four are IsUntradable. The MGP CURRENCY itself (item 29) carries Action 0, so the currency
    /// can never be mistaken for a card that grants it.
    /// </summary>
    public const uint InstantMgp = 3800;

    /// <summary>Kinds Charon will offer to use. Additions need a verified sample, not a guess.</summary>
    public static readonly IReadOnlySet<uint> Known = new HashSet<uint> { InstantMgp };

    /// <summary>Human-readable name for a known kind; empty for anything unrecognised.</summary>
    public static string Describe(uint actionKind) => actionKind switch
    {
        InstantMgp => "Gold Saucer points",
        _ => string.Empty,
    };
}
