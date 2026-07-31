using System.Collections.Generic;

namespace Charon.Features.Loot;

/// <summary>
/// Collectible kinds, keyed by the item's <c>ItemAction.Action</c> value — the real discriminator.
///
/// There is NO <c>Type</c> column on the ItemAction sheet (its columns are Action, CondBattle,
/// CondLv, CondPVP, CondPVPOnly, Data, DataHQ — verified), and the number everyone calls "the item
/// action type" is <c>Action</c>.
///
/// This is an ALLOWLIST on purpose. <c>IsItemActionUnlocked</c> does NOT distinguish a collectible
/// from an ordinary consumable — it returns the same "not unlocked" value for a Potion as for a
/// genuinely unlearned orchestrion roll, so filtering on unlock state alone listed 21 potions and an
/// aetheryte ticket as things to "collect". Clicking one would have drunk it. Anything not known to
/// be a collectible is therefore excluded, and unrecognised kinds are logged so this list can grow
/// from observed values instead of guesses.
/// </summary>
public static class CollectibleKinds
{
    /// <summary>Minion / companion. VERIFIED: Wind-up Sun (item 7560).</summary>
    public const uint Minion = 853;

    /// <summary>Mount. VERIFIED: Chocobo Whistle (6001), Fat Chocobo Whistle (7553).</summary>
    public const uint Mount = 1322;

    /// <summary>
    /// Generic unlock link, covering BOTH emotes and hairstyles. VERIFIED: Modern Aesthetics - Curls
    /// (13704, hairstyle) and Ballroom Etiquette - Ultima (23363, emote) share this exact value, and
    /// both also share the "Miscellany" UI category — so the two cannot be told apart by any field
    /// available here. Separate per-type filters for emotes vs hairstyles are not possible.
    /// </summary>
    public const uint EmoteOrHairstyle = 2633;

    /// <summary>Orchestrion roll. VERIFIED: Homestead Orchestrion Roll (item 16809).</summary>
    public const uint OrchestrionRoll = 25183;

    /// <summary>
    /// Phantom job soul shard. VERIFIED: Mystic Knight's Soul Shard (logged from a live bag).
    /// Usable ONLY in the Occult Crescent — see <see cref="ZoneRestricted"/>.
    /// </summary>
    public const uint PhantomJobShard = 43142;

    /// <summary>
    /// Occult Crescent territories, where phantom job shards can actually be used.
    /// VERIFIED: South Horn = 1252 (o6b1), North Horn = 1346 (o6b2).
    /// </summary>
    public static readonly IReadOnlySet<uint> OccultCrescentTerritories = new HashSet<uint> { 1252, 1346 };

    /// <summary>
    /// Kinds that are real unlocks but only usable somewhere specific. They are listed everywhere so
    /// you can see you are holding one, but only offered for collection in the right zone — a button
    /// that fails everywhere but one map is worse than no button.
    /// </summary>
    public static readonly IReadOnlySet<uint> ZoneRestricted = new HashSet<uint> { PhantomJobShard };

    /// <summary>Whether this kind can be collected in the given territory.</summary>
    public static bool CanCollectHere(uint actionKind, uint territoryId) =>
        actionKind != PhantomJobShard || OccultCrescentTerritories.Contains(territoryId);

    /// <summary>
    /// Kinds Charon will offer to learn — only VERIFIED one-time unlocks. A wrong entry means
    /// consuming something that is not a collectible, so additions need evidence, not inference.
    ///
    /// Deliberately EXCLUDED: 2120 Triad Card. Those items are booster PACKS that open into random
    /// cards, not a one-time unlock, so "already collected" is not even the right question for them.
    /// </summary>
    public static readonly IReadOnlySet<uint> Known = new HashSet<uint>
    {
        Minion,
        Mount,
        EmoteOrHairstyle,
        OrchestrionRoll,
        PhantomJobShard,
    };

    /// <summary>Human-readable name for a known kind; empty for anything unrecognised.</summary>
    public static string Describe(uint actionKind) => actionKind switch
    {
        Minion => "minion",
        Mount => "mount",
        EmoteOrHairstyle => "emote or hairstyle",
        OrchestrionRoll => "orchestrion roll",
        PhantomJobShard => "phantom job",
        _ => string.Empty,
    };
}
