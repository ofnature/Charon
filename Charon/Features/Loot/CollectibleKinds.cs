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
    /// One Triple Triad card — registering it to the deck. VERIFIED on two samples: Eald'narche Card
    /// (46814) and Alpha Card (24875), both Action 3357 with the card's own id in Data[0].
    ///
    /// NOT the same thing as a booster PACK. The seven "* Triad Card" packs (Bronze, Silver, Gold,
    /// Mythril, Platinum, Imperial, Dream) all share Action 2120 and are deliberately excluded — see
    /// <see cref="Known"/>. One action per behaviour, so the two never need telling apart by name.
    /// </summary>
    public const uint TripleTriadCard = 3357;

    /// <summary>
    /// Fashion accessory (parasols, lanterns and the like). VERIFIED on two samples: Parasol (30269)
    /// and Antique Lantern (52289) share this value, as do Loparasol and Neon Parasol.
    ///
    /// Worth knowing: some of these are genuinely VALUABLE on the market board — the Antique Lantern
    /// runs to millions of gil — and collecting consumes the item. Collect is per-item and manual, so
    /// nothing here is ever spent without a deliberate click, but this is the one kind where "learn
    /// it rather than sell it" is a real decision instead of an obvious one.
    /// </summary>
    public const uint FashionAccessory = 20086;

    /// <summary>
    /// Chocobo barding. VERIFIED across 21 samples (Voidcast 40367, Ice 9355, Hive 12083, Ruby
    /// 29402, Hades 28616, Byakko 21924 …), all sharing this action with the barding's own id in
    /// Data[0] — the same shape as <see cref="TripleTriadCard"/>.
    ///
    /// Like <see cref="FashionAccessory"/> this is a kind where an UNLEARNED one can be worth real
    /// money (Voidcast Barding runs to half a million gil) and collecting consumes it. Manual
    /// per-item click is what keeps that safe.
    /// </summary>
    public const uint ChocoboBarding = 1013;

    /// <summary>
    /// Facewear — the "The Faces We Wear" glasses, goggles and eyepatches. VERIFIED across the whole
    /// 53-item family (Slim Frame Glasses 45006, Monocles 44264, Holospecs 50455 …), every one
    /// sharing this action with an all-zero Data: the item itself is the unlock.
    /// </summary>
    public const uint Facewear = 37312;

    /// <summary>
    /// Occult Record note ("Use to add to the Occult Record"). VERIFIED: Notes on the Cloister
    /// Demon (item 47728).
    ///
    /// NOT zone-gated, unlike its neighbour <see cref="PhantomJobShard"/>: the shard's tooltip
    /// carries an explicit "Can only be used on the Occult Crescent" line and this one does not.
    /// That difference is the evidence — if a note turns out to fail outside the zone, the fix is to
    /// add it to <see cref="ZoneRestricted"/> rather than to assume it now.
    /// </summary>
    public const uint OccultRecordNote = 43141;

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
    /// Deliberately EXCLUDED: 2120. Those seven items are Triple Triad booster PACKS that open into
    /// random cards, not a one-time unlock, so "already collected" is not even the right question for
    /// them. Individual cards are a different action entirely (<see cref="TripleTriadCard"/>) and DO
    /// belong here — excluding 2120 was never meant to exclude cards.
    /// </summary>
    public static readonly IReadOnlySet<uint> Known = new HashSet<uint>
    {
        Minion,
        Mount,
        EmoteOrHairstyle,
        OrchestrionRoll,
        TripleTriadCard,
        FashionAccessory,
        Facewear,
        ChocoboBarding,
        OccultRecordNote,
        PhantomJobShard,
    };

    /// <summary>Human-readable name for a known kind; empty for anything unrecognised.</summary>
    public static string Describe(uint actionKind) => actionKind switch
    {
        Minion => "minion",
        Mount => "mount",
        EmoteOrHairstyle => "emote or hairstyle",
        OrchestrionRoll => "orchestrion roll",
        TripleTriadCard => "triple triad card",
        FashionAccessory => "fashion accessory",
        Facewear => "facewear",
        ChocoboBarding => "chocobo barding",
        OccultRecordNote => "occult record",
        PhantomJobShard => "phantom job",
        _ => string.Empty,
    };
}
