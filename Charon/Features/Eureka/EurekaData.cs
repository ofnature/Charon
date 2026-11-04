// Generated once (2026-11-03) from the Eureka research data: names merged from the sources below, then
// resolved to BNpcName ids with Lumina against the game files. Plain data - edit by hand from here on.
using System.Collections.Generic;
using System.Numerics;

namespace Charon.Features.Eureka;

/// <summary>
/// The facts the Eureka ESP draws from.
///
/// MOB AGGRO is FACTS gathered from public sources, keyed by BNpcName row ids resolved from the game's
/// own BNpcName sheet (Lumina, by English singular name):
///   - the community "Eureka Bestiary" sheet (Pyros + Hydatos tabs, per-mob Aggro column; no author or
///     licence stated, so facts only, never the sheet itself);
///   - the Console Games Wiki (race per mob: every Ashkin = low-HP aggro; the infobox aggro type where filled);
///   - named mobs from guides (SE forum "Eureka: The Fine Print", Icy Veins, Eurekan Academy, MeinMMO).
/// Family rules (Ashkin = low HP, sprites = magic) and single-source mobs are NOT confirmed per mob.
/// Anemos and Pagos have almost no per-mob data anywhere; everything not listed is unknown.
///
/// ELEMENTAL SPOTS are from EurekaHelper (snorux/EurekaHelper, maintained fork KangasZ/EurekaHelper),
/// XIV/Zones/Eureka*.cs `ElementalPositions` - licensed under the Apache License 2.0
/// (http://www.apache.org/licenses/LICENSE-2.0); positions copied and reformatted, nothing else changed.
///
/// BUNNY COFFER SPOTS are from Eureka Linker (Infiziert90/EurekaTrackerAutoPopper), BunnyChests.cs
/// `Positions`. MIT License, Copyright (c) 2023 Infi - permission is granted free of charge to use, copy,
/// modify and distribute, provided this notice is kept; the software is provided "as is", without warranty.
///
/// Positions are the game's own (x, height, z) world coordinates - both plugins compare them straight
/// against an object's Position.
/// </summary>
public static class EurekaData
{
    /// <summary>Mob aggro by (territory, BNpcName id); territory 0 = any Eureka zone.</summary>
    public static readonly IReadOnlyList<EurekaMobFact> Mobs = new EurekaMobFact[]
    {
        new(0, 7176, EurekaAggro.Magic, false), // any zone: Death Sprite (sprite family rule)
        new(0, 7472, EurekaAggro.Magic, true), // any zone: Ember Sprite (Eureka Bestiary sheet)
        new(0, 7773, EurekaAggro.Magic, true), // any zone: Ember Sprite (Eureka Bestiary sheet)
        new(0, 6982, EurekaAggro.Magic, false), // any zone: Rain Sprite (sprite family rule)
        new(0, 7471, EurekaAggro.Magic, true), // any zone: Snowmelt Sprite (Eureka Bestiary sheet)
        new(0, 7469, EurekaAggro.Magic, true), // any zone: Snowstorm Sprite (Eureka Bestiary sheet)
        new(0, 7774, EurekaAggro.Magic, true), // any zone: Snowstorm Sprite (Eureka Bestiary sheet)
        new(0, 7470, EurekaAggro.Magic, true), // any zone: Thunderstorm Sprite (Eureka Bestiary sheet)
        new(0, 7772, EurekaAggro.Magic, true), // any zone: Thunderstorm Sprite (Eureka Bestiary sheet)
        new(0, 6983, EurekaAggro.Magic, true), // any zone: Typhoon Sprite (Eureka Bestiary sheet)
        new(0, 7771, EurekaAggro.Magic, true), // any zone: Typhoon Sprite (Eureka Bestiary sheet)
        new(732, 7052, EurekaAggro.LowHp, false), // Anemos: Crystal Wolf (Ashkin family rule (CGW race))
        new(732, 6985, EurekaAggro.LowHp, false), // Anemos: Duskfall Bhoot (Ashkin family rule (CGW race))
        new(732, 6986, EurekaAggro.LowHp, false), // Anemos: Duskfall Mummy (Ashkin family rule (CGW race))
        new(732, 7222, EurekaAggro.LowHp, false), // Anemos: Duskfall Skeleton (Ashkin family rule (CGW race))
        new(732, 6990, EurekaAggro.LowHp, false), // Anemos: Fossil Dragon (Ashkin family rule (CGW race))
        new(732, 6973, EurekaAggro.LowHp, false), // Anemos: Four-eyes (Ashkin family rule (CGW race))
        new(732, 7045, EurekaAggro.LowHp, false), // Anemos: Ignis Fatuus (Ashkin family rule (CGW race))
        new(732, 6972, EurekaAggro.Sound, false), // Anemos: Magia Fan (SE forum only)
        new(732, 6809, EurekaAggro.Sound, true), // Anemos: Scape Leech (SE forum 'Eureka: The Fine Print' + MeinMMO)
        new(732, 6820, EurekaAggro.Sound, false), // Anemos: Sea Bishop (MeinMMO only)
        new(732, 6989, EurekaAggro.LowHp, false), // Anemos: Shadow Wraith (Ashkin family rule (CGW race))
        new(732, 6984, EurekaAggro.LowHp, false), // Anemos: Skeleton Noble (Ashkin family rule (CGW race))
        new(732, 6987, EurekaAggro.LowHp, false), // Anemos: Traveling Gourmand (Ashkin family rule (CGW race))
        new(732, 7040, EurekaAggro.LowHp, false), // Anemos: Val Revenant (Ashkin family rule (CGW race))
        new(732, 6980, EurekaAggro.LowHp, false), // Anemos: Val Specter (Ashkin family rule (CGW race))
        new(763, 7462, EurekaAggro.Sound, false), // Pagos: Ameretat (Eurekan Academy only)
        new(763, 7473, EurekaAggro.Sound, true), // Pagos: Frozen Void Dragon (CGW infobox + Icy Veins + Eurekan Academy)
        new(763, 7535, EurekaAggro.LowHp, false), // Pagos: Taxim (Ashkin family rule (CGW race))
        new(763, 7468, EurekaAggro.LowHp, false), // Pagos: Val Corpse (Ashkin family rule (CGW race))
        new(795, 7844, EurekaAggro.TrueSight, true), // Pyros: Ancient Voidragon (Eureka Bestiary sheet)
        new(795, 7824, EurekaAggro.Sight, true), // Pyros: Bird Eater (Eureka Bestiary sheet)
        new(795, 7805, EurekaAggro.Sight, true), // Pyros: Blazewing (Eureka Bestiary sheet)
        new(795, 7816, EurekaAggro.Sound, true), // Pyros: Clipper (Eureka Bestiary sheet)
        new(795, 14070, EurekaAggro.Sound, true), // Pyros: Clipper (Eureka Bestiary sheet)
        new(795, 7781, EurekaAggro.LowHp, true), // Pyros: Dark Parthenope (Eureka Bestiary sheet)
        new(795, 7779, EurekaAggro.LowHp, true), // Pyros: Dark Troubadour (Eureka Bestiary sheet)
        new(795, 7782, EurekaAggro.LowHp, true), // Pyros: Delphyne (Eureka Bestiary sheet)
        new(795, 7783, EurekaAggro.LowHp, true), // Pyros: Delphyne (Eureka Bestiary sheet)
        new(795, 7827, EurekaAggro.Sight, true), // Pyros: Dingo (Eureka Bestiary sheet)
        new(795, 7840, EurekaAggro.Sight, true), // Pyros: Fabricated Singha (Eureka Bestiary sheet)
        new(795, 7849, EurekaAggro.Sound, true), // Pyros: Flame Voidragon (Console Games Wiki infobox)
        new(795, 7792, EurekaAggro.Sight, true), // Pyros: Gibbon (Eureka Bestiary sheet)
        new(795, 7813, EurekaAggro.TrueSight, true), // Pyros: Haokah Unbound (Eureka Bestiary sheet)
        new(795, 6840, EurekaAggro.TrueSight, true), // Pyros: Henbane (Eureka Bestiary sheet)
        new(795, 7835, EurekaAggro.TrueSight, true), // Pyros: Henbane (Eureka Bestiary sheet)
        new(795, 7807, EurekaAggro.Sight, true), // Pyros: Hippomartimus (Eureka Bestiary sheet)
        new(795, 7832, EurekaAggro.Sight, true), // Pyros: Illuminati Escapee (Eureka Bestiary sheet)
        new(795, 7821, EurekaAggro.Sight, true), // Pyros: Islandhander (Eureka Bestiary sheet)
        new(795, 7767, EurekaAggro.Sound, true), // Pyros: Karlabos of Pyros (Eureka Bestiary sheet)
        new(795, 7817, EurekaAggro.Sound, true), // Pyros: Karlabos of Pyros (Eureka Bestiary sheet)
        new(795, 7810, EurekaAggro.Sight, true), // Pyros: Machairodus (Eureka Bestiary sheet)
        new(795, 7836, EurekaAggro.Sight, true), // Pyros: Matanga Castaway (Eureka Bestiary sheet)
        new(795, 7803, EurekaAggro.Sight, true), // Pyros: Mechanoguardian (Eureka Bestiary sheet)
        new(795, 7802, EurekaAggro.Sight, true), // Pyros: Mega-gris (Eureka Bestiary sheet)
        new(795, 7791, EurekaAggro.TrueSight, true), // Pyros: Northern Griffin (Eureka Bestiary sheet)
        new(795, 7829, EurekaAggro.Sight, true), // Pyros: Northern Grizzly (Eureka Bestiary sheet)
        new(795, 7818, EurekaAggro.Sight, true), // Pyros: Northern Ray (Eureka Bestiary sheet)
        new(795, 7830, EurekaAggro.Sight, true), // Pyros: Northern Swallow (Eureka Bestiary sheet)
        new(795, 7814, EurekaAggro.TrueSight, true), // Pyros: Overdue Tome (Eureka Bestiary sheet)
        new(795, 7826, EurekaAggro.Magic, true), // Pyros: Pure White Flame (Eureka Bestiary sheet)
        new(795, 7808, EurekaAggro.Sight, true), // Pyros: Pyros Apanda (Eureka Bestiary sheet)
        new(795, 7776, EurekaAggro.LowHp, true), // Pyros: Pyros Bhoot (Eureka Bestiary sheet)
        new(795, 7819, EurekaAggro.Sight, true), // Pyros: Pyros Bombfish (Eureka Bestiary sheet)
        new(795, 7804, EurekaAggro.Sight, true), // Pyros: Pyros Chelone (Eureka Bestiary sheet)
        new(795, 7790, EurekaAggro.Sight, true), // Pyros: Pyros Coeurl (Eureka Bestiary sheet)
        new(795, 7825, EurekaAggro.Sight, true), // Pyros: Pyros Crab (Eureka Bestiary sheet)
        new(795, 7823, EurekaAggro.Sight, true), // Pyros: Pyros Gulo Gulo (Eureka Bestiary sheet)
        new(795, 7833, EurekaAggro.Sound, true), // Pyros: Pyros Hawk (Eureka Bestiary sheet)
        new(795, 7845, EurekaAggro.TrueSight, true), // Pyros: Pyros Hecteyes (Eureka Bestiary sheet)
        new(795, 7794, EurekaAggro.Sight, true), // Pyros: Pyros Kongamato (Eureka Bestiary sheet)
        new(795, 7822, EurekaAggro.Sound, true), // Pyros: Pyros Nanka (Eureka Bestiary sheet)
        new(795, 7780, EurekaAggro.LowHp, true), // Pyros: Pyros Parthenope (Eureka Bestiary sheet)
        new(795, 7831, EurekaAggro.Sound, true), // Pyros: Pyros Piranu (Eureka Bestiary sheet)
        new(795, 7820, EurekaAggro.Sight, true), // Pyros: Pyros Pudding (Eureka Bestiary sheet)
        new(795, 7796, EurekaAggro.Sight, true), // Pyros: Pyros Serpent (Eureka Bestiary sheet)
        new(795, 7848, EurekaAggro.Sight, true), // Pyros: Pyros Shuck (Eureka Bestiary sheet)
        new(795, 7828, EurekaAggro.Sound, true), // Pyros: Pyros Slime (Eureka Bestiary sheet)
        new(795, 7793, EurekaAggro.Sound, true), // Pyros: Pyros Slug (Eureka Bestiary sheet)
        new(795, 7811, EurekaAggro.Sight, true), // Pyros: Pyros Snake (Eureka Bestiary sheet)
        new(795, 7775, EurekaAggro.LowHp, true), // Pyros: Pyros Wolf (Eureka Bestiary sheet)
        new(795, 7769, EurekaAggro.Sound, true), // Pyros: Pyros Wood Golem (Eureka Bestiary sheet)
        new(795, 7834, EurekaAggro.Sound, true), // Pyros: Pyros Wood Golem (Eureka Bestiary sheet)
        new(795, 7777, EurekaAggro.LowHp, true), // Pyros: Pyros Wraith (Eureka Bestiary sheet)
        new(795, 7801, EurekaAggro.Sound, true), // Pyros: Red Eye (Eureka Bestiary sheet)
        new(795, 7785, EurekaAggro.LowHp, true), // Pyros: Soulless Agent (Eureka Bestiary sheet)
        new(795, 7784, EurekaAggro.LowHp, true), // Pyros: Soulless Pathfinder (Eureka Bestiary sheet)
        new(795, 7842, EurekaAggro.Sight, true), // Pyros: Stegoceras (Eureka Bestiary sheet)
        new(795, 7728, EurekaAggro.LowHp, false), // Pyros: Sulphuric Slime (Ashkin family rule (CGW race))
        new(795, 7846, EurekaAggro.Sight, true), // Pyros: Val Bloodglider (Eureka Bestiary sheet)
        new(795, 7838, EurekaAggro.Sight, true), // Pyros: Val Hornbill (Eureka Bestiary sheet)
        new(795, 7789, EurekaAggro.Sound, true), // Pyros: Val Icetrap (Eureka Bestiary sheet)
        new(795, 7809, EurekaAggro.Sight, true), // Pyros: Val Mammoth (Eureka Bestiary sheet)
        new(795, 7839, EurekaAggro.Sight, true), // Pyros: Val Minotaur (Eureka Bestiary sheet)
        new(795, 6841, EurekaAggro.TrueSight, true), // Pyros: Val Skatene (Eureka Bestiary sheet)
        new(795, 7841, EurekaAggro.TrueSight, true), // Pyros: Val Skatene (Eureka Bestiary sheet)
        new(795, 7778, EurekaAggro.LowHp, true), // Pyros: Val Wraith (Eureka Bestiary sheet)
        new(795, 7815, EurekaAggro.Sight, true), // Pyros: Val Yeti (Eureka Bestiary sheet)
        new(795, 7812, EurekaAggro.Sight, true), // Pyros: Valking (Eureka Bestiary sheet)
        new(795, 7806, EurekaAggro.TrueSight, true), // Pyros: Wasserspeier (Eureka Bestiary sheet)
        new(795, 7843, EurekaAggro.Sight, true), // Pyros: Zebu (Eureka Bestiary sheet)
        new(827, 8057, EurekaAggro.LowHp, true), // Hydatos: dark rider (Eureka Bestiary sheet)
        new(827, 8030, EurekaAggro.Proximity, true), // Hydatos: Eo'ghrah (Eureka Bestiary sheet)
        new(827, 8023, EurekaAggro.Sound, true), // Hydatos: Hydatos tsanahale (Eureka Bestiary sheet)
        new(827, 8058, EurekaAggro.LowHp, true), // Hydatos: Hydatos wraith (Eureka Bestiary sheet)
        new(827, 7963, EurekaAggro.LowHp, false), // Hydatos: King's Jester (Ashkin family rule (CGW race))
        new(827, 7961, EurekaAggro.LowHp, false), // Hydatos: King's Pike (Ashkin family rule (CGW race))
        new(827, 8029, EurekaAggro.Proximity, true), // Hydatos: lake yabby (Eureka Bestiary sheet)
        new(827, 8034, EurekaAggro.Sound, true), // Hydatos: rusted dreadnaught (Eureka Bestiary sheet)
        new(827, 8059, EurekaAggro.LowHp, true), // Hydatos: samurai corpse (Eureka Bestiary sheet)
        new(827, 8056, EurekaAggro.LowHp, true), // Hydatos: soulless adventurer (Eureka Bestiary sheet)
        new(827, 8055, EurekaAggro.LowHp, true), // Hydatos: soulless researcher (Eureka Bestiary sheet)
        new(827, 8009, EurekaAggro.Sound, true), // Hydatos: Val Nanka (Eureka Bestiary sheet)
    };

    /// <summary>Elemental spawn spots per territory (EurekaHelper, Apache-2.0).</summary>
    public static readonly IReadOnlyDictionary<uint, Vector3[]> ElementalSpots = new Dictionary<uint, Vector3[]>
    {
        [732] = new Vector3[] // Anemos, 39 spots
        {
            new(-47.6998f, 35.0050f, -132.7875f),
            new(-71.0314f, 40.7219f, -412.3625f),
            new(-85.7398f, 26.0050f, 191.9998f),
            new(-121.6458f, 37.4431f, 6.5193f),
            new(-220.0411f, 43.8200f, -111.9934f),
            new(-227.0947f, 35.3904f, 121.2573f),
            new(-241.1971f, 43.7412f, -113.4114f),
            new(-336.4917f, 63.0996f, -418.0745f),
            new(-364.4897f, 69.4702f, -276.5466f),
            new(-373.7721f, 82.7268f, 18.4767f),
            new(-399.8804f, 17.5902f, 291.9889f),
            new(-408.8093f, 27.3615f, 425.7613f),
            new(-415.0757f, 45.3437f, 138.5250f),
            new(-464.8019f, 63.2258f, -435.7054f),
            new(-580.6493f, 41.8407f, -5.5684f),
            new(-588.3321f, 22.7790f, 238.9747f),
            new(-623.1824f, 45.7931f, -114.6636f),
            new(-642.7914f, 41.4217f, -119.1096f),
            new(-732.5733f, 25.7236f, 180.2680f),
            new(7.8140f, -23.3250f, 527.6335f),
            new(8.5997f, 30.2675f, -263.5725f),
            new(13.2696f, 14.8062f, 300.0340f),
            new(31.9998f, 38.6541f, -19.6001f),
            new(110.2639f, 27.2883f, 138.9691f),
            new(132.6454f, -6.9841f, 465.3566f),
            new(137.8132f, 37.1854f, -168.3959f),
            new(225.4329f, 61.9580f, 282.7937f),
            new(239.4716f, 40.2040f, 49.0624f),
            new(256.0000f, 32.2887f, -330.4921f),
            new(262.5656f, 30.2711f, -113.7084f),
            new(339.0738f, 42.0031f, 181.9042f),
            new(364.5631f, 37.6548f, -410.9562f),
            new(396.8458f, 33.9959f, -62.5329f),
            new(405.6295f, 41.0942f, -258.1847f),
            new(417.7422f, 30.2732f, 306.4423f),
            new(567.3381f, 35.4471f, -79.2503f),
            new(643.5429f, 35.3563f, -306.5175f),
            new(680.3775f, 40.0205f, -1.3001f),
            new(686.8856f, 38.0727f, -154.0496f),
        },
        [763] = new Vector3[] // Pagos, 40 spots
        {
            new(-41.5411f, -471.7219f, -365.6624f),
            new(-93.8976f, -740.9329f, 390.3791f),
            new(-141.2347f, -562.6061f, -282.6532f),
            new(-127.9997f, -546.0727f, -101.5866f),
            new(-181.1255f, -733.0151f, 431.1895f),
            new(-218.8462f, -720.4173f, 187.9130f),
            new(-224.1831f, -606.6824f, -133.2441f),
            new(-263.0846f, -569.7501f, -337.2927f),
            new(-270.0375f, -644.0015f, 14.7729f),
            new(-286.9152f, -587.2986f, -223.6939f),
            new(-286.1163f, -677.2271f, 385.7882f),
            new(-339.8168f, -671.7037f, 160.0002f),
            new(-432.6792f, -588.9239f, -539.0688f),
            new(-460.0778f, -703.4506f, 260.3547f),
            new(-467.4751f, -656.8985f, -26.8950f),
            new(-479.9998f, -700.4795f, 387.8571f),
            new(-493.1782f, -611.9679f, -50.3522f),
            new(-572.7231f, -618.6403f, -167.8880f),
            new(-596.4171f, -620.2308f, -455.2062f),
            new(-627.7185f, -697.3568f, 147.9566f),
            new(-677.4056f, -649.4050f, 42.3230f),
            new(-713.4697f, -624.1894f, -270.3222f),
            new(0.9566f, -721.7770f, 210.2904f),
            new(8.5741f, -738.0508f, 323.6507f),
            new(20.4318f, -546.3381f, 6.3047f),
            new(87.1165f, -510.4972f, -159.9996f),
            new(114.5379f, -632.4933f, 119.3930f),
            new(152.0979f, -495.1393f, -282.6871f),
            new(192.1393f, -647.5070f, 197.5168f),
            new(273.5149f, -532.7138f, -128.0004f),
            new(286.3525f, -641.0692f, 25.1877f),
            new(337.3805f, -721.5266f, 291.5819f),
            new(354.9172f, -688.8890f, 105.6524f),
            new(426.5608f, -578.4030f, -73.7690f),
            new(450.2676f, -747.3127f, 217.9324f),
            new(462.0497f, -553.8097f, -225.2466f),
            new(489.0103f, -740.3993f, 424.4987f),
            new(528.4097f, -688.6110f, 50.5712f),
            new(714.3189f, -630.0619f, -321.2806f),
            new(735.9999f, -629.8334f, -274.5996f),
        },
        [795] = new Vector3[] // Pyros, 35 spots
        {
            new(-22.7462f, 768.3129f, 510.3440f),
            new(-33.6970f, 683.3643f, 243.2133f),
            new(-110.5325f, 764.7305f, 617.9566f),
            new(-124.4339f, 674.7357f, 305.9441f),
            new(-153.7847f, 666.8735f, -199.7227f),
            new(-154.0888f, 764.3937f, 520.6639f),
            new(-231.1558f, 658.3021f, -558.7342f),
            new(-248.9779f, 658.0245f, -678.9961f),
            new(-332.1558f, 641.8102f, 622.6001f),
            new(-358.3534f, 661.6628f, 343.2160f),
            new(-398.8300f, 666.6412f, -672.0005f),
            new(-425.1501f, 659.0271f, 447.9998f),
            new(-439.0166f, 675.9194f, -206.9271f),
            new(-480.7868f, 673.9460f, -345.3124f),
            new(-596.1195f, 674.6573f, -288.5684f),
            new(36.2212f, 676.9820f, -201.4076f),
            new(59.1992f, 754.1885f, 702.0414f),
            new(134.1277f, 675.8171f, -721.8558f),
            new(145.9791f, 679.1502f, -401.4620f),
            new(181.1693f, 659.8738f, -278.7961f),
            new(183.2644f, 751.2846f, 837.4545f),
            new(197.4814f, 717.4583f, 299.7896f),
            new(233.8187f, 681.5016f, -584.3393f),
            new(265.5905f, 745.7182f, 758.2044f),
            new(272.4338f, 723.1208f, 163.4751f),
            new(311.0012f, 738.5522f, 510.6443f),
            new(351.5322f, 677.0166f, -669.4298f),
            new(387.7805f, 737.8065f, 566.4737f),
            new(423.8157f, 661.3527f, -212.1115f),
            new(444.6063f, 723.1206f, 360.9161f),
            new(460.0268f, 724.2298f, 482.7586f),
            new(462.5268f, 665.4831f, -601.6767f),
            new(472.0545f, 670.6680f, -332.1753f),
            new(501.5984f, 668.1235f, -467.8756f),
            new(732.0828f, 656.5740f, -343.1226f),
        },
        [827] = new Vector3[] // Hydatos, 32 spots
        {
            new(-116.4520f, 501.6467f, -331.4255f),
            new(-123.5271f, 501.0216f, -559.1261f),
            new(-253.4127f, 499.1309f, -518.0680f),
            new(-256.5048f, 514.3237f, -903.4661f),
            new(-312.1552f, 502.0328f, -228.2824f),
            new(-360.7999f, 500.0000f, -710.8348f),
            new(-365.2967f, 494.0000f, -68.0508f),
            new(-572.7837f, 509.9416f, -836.6340f),
            new(-579.5980f, 504.0798f, -213.0574f),
            new(-583.8156f, 507.7481f, -468.5090f),
            new(-611.1752f, 507.4220f, -59.8463f),
            new(-712.0532f, 511.6147f, -583.8992f),
            new(-716.3769f, 504.4299f, -370.0967f),
            new(-798.5133f, 514.8021f, -754.0926f),
            new(-801.7904f, 505.8443f, -49.6313f),
            new(-852.2482f, 508.8659f, -353.7695f),
            new(-895.5052f, 507.7101f, -130.0577f),
            new(28.6143f, 496.3249f, -54.4070f),
            new(105.5450f, 499.1306f, -590.0835f),
            new(113.3220f, 495.3159f, -191.0005f),
            new(141.9147f, 502.4549f, -386.1688f),
            new(155.7582f, 513.0920f, -810.1353f),
            new(355.9239f, 508.8036f, -491.6639f),
            new(400.4016f, 495.3814f, -33.8538f),
            new(466.7553f, 506.6967f, -243.0900f),
            new(471.3410f, 506.4836f, -742.8676f),
            new(629.0432f, 500.2527f, -472.0037f),
            new(651.8131f, 500.3221f, -713.3341f),
            new(726.0835f, 514.4786f, -203.8521f),
            new(773.1991f, 495.9364f, -60.5657f),
            new(828.4681f, 512.1960f, -428.0070f),
            new(873.1450f, 512.1006f, -739.3626f),
        },
    };

    /// <summary>Bunny coffer spots per territory (Eureka Linker, MIT). Anemos has no bunny FATE.</summary>
    public static readonly IReadOnlyDictionary<uint, Vector3[]> CofferSpots = new Dictionary<uint, Vector3[]>
    {
        [763] = new Vector3[] // Pagos, 60 spots
        {
            new(-737.8369f, -677.9246f, 143.3176f),
            new(-651.9872f, -688.0630f, 85.1839f),
            new(-606.5342f, -700.6216f, 143.0238f),
            new(-589.4037f, -698.3553f, 15.3750f),
            new(-430.1872f, -680.5923f, 60.5043f),
            new(-386.3382f, -677.4930f, 468.2163f),
            new(-340.4522f, -671.2720f, 425.2418f),
            new(-298.3470f, -664.6113f, 450.3740f),
            new(-289.4620f, -585.3194f, -178.6195f),
            new(-285.1725f, -586.4724f, -245.1630f),
            new(-283.3895f, -573.5060f, -337.2024f),
            new(-259.9016f, -602.8868f, -139.1166f),
            new(-257.5845f, -627.9702f, -67.4454f),
            new(-222.8215f, -557.8751f, -370.6961f),
            new(-222.4405f, -628.8225f, -69.5272f),
            new(-199.5562f, -594.9023f, -154.8800f),
            new(-175.5653f, -568.1696f, -237.6590f),
            new(-158.7989f, -576.3018f, -200.3996f),
            new(-147.9979f, -563.5579f, -282.9213f),
            new(292.9121f, -640.7648f, 15.7098f),
            new(351.9018f, -690.0383f, 115.0498f),
            new(398.6197f, -678.8519f, -15.5930f),
            new(445.4254f, -741.8608f, 188.4324f),
            new(476.7410f, -739.2648f, 421.0621f),
            new(487.0211f, -756.6284f, 291.6779f),
            new(514.9791f, -702.1201f, 139.7072f),
            new(516.0520f, -679.7125f, -37.7927f),
            new(530.7849f, -720.4700f, 230.5152f),
            new(543.9090f, -730.7676f, 355.7726f),
            new(631.0588f, -730.9005f, 317.3849f),
            new(643.9543f, -702.8303f, 117.7415f),
            new(-834.3569f, -612.2924f, -289.3059f),
            new(-765.2896f, -611.9983f, -159.5746f),
            new(-693.0172f, -647.2126f, 35.8301f),
            new(-665.1354f, -627.3637f, -355.8533f),
            new(-660.2762f, -624.1875f, -221.7502f),
            new(-631.2686f, -620.1370f, -450.9197f),
            new(-567.9847f, -599.2828f, -580.3564f),
            new(-531.0604f, -616.7406f, -75.6609f),
            new(-358.4612f, -648.7422f, -195.9099f),
            new(-341.6468f, -656.3936f, -87.5932f),
            new(-60.7675f, -477.3432f, -346.5658f),
            new(-50.4911f, -462.5427f, -398.8157f),
            new(51.2085f, -635.3545f, 140.7099f),
            new(104.5841f, -475.1954f, -341.0629f),
            new(121.1856f, -460.1292f, -416.6242f),
            new(147.8505f, -635.1290f, 196.7050f),
            new(258.8517f, -641.5285f, 171.4124f),
            new(300.4921f, -643.3304f, 89.6410f),
            new(383.1363f, -549.2653f, -180.7012f),
            new(422.8629f, -578.9005f, -68.5270f),
            new(434.6870f, -531.4767f, -289.3110f),
            new(462.8273f, -552.3704f, -230.8436f),
            new(536.8254f, -553.5660f, -243.9232f),
            new(562.9125f, -569.6530f, -148.1824f),
            new(583.9424f, -579.3117f, -87.1424f),
            new(671.0728f, -629.6361f, -242.9718f),
            new(713.2711f, -630.6937f, -326.9075f),
            new(734.8610f, -630.5897f, -284.3216f),
            new(817.1915f, -629.0625f, -276.2072f),
        },
        [795] = new Vector3[] // Pyros, 60 spots
        {
            new(-469.8102f, 659.1795f, 441.7094f),
            new(-464.4484f, 660.6446f, 419.2033f),
            new(-438.3944f, 660.7888f, 400.7463f),
            new(-340.1021f, 660.3159f, 384.9267f),
            new(-198.2953f, 757.9987f, 477.8044f),
            new(-197.1622f, 759.5239f, 599.0419f),
            new(-189.4791f, 671.6885f, 323.3288f),
            new(-150.4335f, 762.6660f, 451.7729f),
            new(-105.5926f, 762.6840f, 686.5082f),
            new(-38.8916f, 769.8203f, 504.8099f),
            new(-38.3755f, 675.3821f, 354.2082f),
            new(-11.6334f, 773.3010f, 601.2862f),
            new(2.4903f, 764.1788f, 411.1573f),
            new(32.1309f, 754.2598f, 689.9406f),
            new(92.8180f, 754.2609f, 825.0632f),
            new(146.3309f, 752.4515f, 756.0107f),
            new(156.9551f, 751.1077f, 704.1921f),
            new(157.0789f, 754.6462f, 841.5557f),
            new(184.4552f, 747.6003f, 617.2846f),
            new(248.7603f, 723.1207f, 118.5381f),
            new(280.3537f, 746.5175f, 754.3834f),
            new(293.8393f, 739.3859f, 531.1690f),
            new(310.4404f, 742.0140f, 567.1605f),
            new(367.9469f, 744.0480f, 639.2587f),
            new(371.0945f, 737.9260f, 491.7239f),
            new(378.1241f, 724.9152f, 287.1851f),
            new(432.9954f, 731.9840f, 568.7686f),
            new(448.9699f, 725.0576f, 457.0699f),
            new(460.4148f, 723.1206f, 311.0332f),
            new(469.0294f, 726.3409f, 535.0562f),
            new(-628.2433f, 669.8270f, -319.8876f),
            new(-611.9520f, 683.3906f, -232.7007f),
            new(-518.2347f, 680.2383f, -247.2683f),
            new(-457.8732f, 669.7634f, -367.8863f),
            new(-412.5399f, 671.2578f, -304.7147f),
            new(-405.1931f, 666.2214f, -700.3820f),
            new(-404.9317f, 674.0713f, -574.5485f),
            new(-339.9076f, 670.2526f, -476.6447f),
            new(-244.1402f, 658.0245f, -691.2558f),
            new(-233.8513f, 668.9334f, -374.3894f),
            new(-222.2047f, 658.1601f, -553.8273f),
            new(-125.8165f, 665.9815f, -333.8782f),
            new(-70.9175f, 672.8761f, -193.4458f),
            new(-52.0266f, 660.1030f, -299.5649f),
            new(-43.1155f, 671.0736f, -399.5620f),
            new(-13.5844f, 676.6263f, -706.9113f),
            new(121.6145f, 685.3226f, -483.2627f),
            new(148.1590f, 687.7542f, -612.4835f),
            new(206.4645f, 659.4509f, -280.9397f),
            new(352.7195f, 662.5346f, -249.7375f),
            new(372.5202f, 659.9396f, -463.6187f),
            new(421.3589f, 663.7094f, -683.5093f),
            new(432.8823f, 660.4274f, -228.2652f),
            new(430.1118f, 671.8463f, -413.4186f),
            new(447.6103f, 666.0066f, -341.5857f),
            new(536.2314f, 668.1235f, -473.6654f),
            new(543.4789f, 668.9618f, -559.8126f),
            new(549.6398f, 675.0362f, -671.2271f),
            new(664.5829f, 676.4869f, -104.6262f),
            new(836.0986f, 656.5436f, -396.1939f),
        },
        [827] = new Vector3[] // Hydatos, 30 spots
        {
            new(-933.6184f, 523.2363f, -736.6334f),
            new(-920.3497f, 505.9977f, -172.3633f),
            new(-863.5947f, 513.3182f, -440.5171f),
            new(-812.4462f, 515.9799f, -936.0333f),
            new(-799.7302f, 505.5284f, -109.4438f),
            new(-678.8408f, 504.2658f, -366.4915f),
            new(-665.9830f, 509.7275f, -589.6073f),
            new(-614.0915f, 511.9456f, -872.6113f),
            new(-562.1750f, 496.6946f, -13.6878f),
            new(-438.4716f, 505.1122f, -682.6366f),
            new(-350.3554f, 500.4460f, -108.9439f),
            new(-322.9585f, 500.9631f, -730.7684f),
            new(-182.3987f, 501.3574f, -591.2992f),
            new(-159.0647f, 495.7984f, -44.4381f),
            new(8.3712f, 504.5359f, -368.8599f),
            new(122.1020f, 499.9993f, -616.1780f),
            new(140.4375f, 496.0539f, 12.4500f),
            new(172.5284f, 503.0239f, -349.5716f),
            new(226.8052f, 505.5214f, -886.2001f),
            new(287.7519f, 505.1768f, -519.0092f),
            new(344.5299f, 495.0796f, -212.5871f),
            new(368.6950f, 505.9346f, -400.7010f),
            new(467.7203f, 505.0858f, -710.6624f),
            new(580.4015f, 497.0735f, -80.3802f),
            new(659.8676f, 502.9848f, -732.6710f),
            new(671.4265f, 508.2654f, -341.5508f),
            new(832.6055f, 513.5617f, -520.9632f),
            new(843.4305f, 494.4439f, -7.3526f),
            new(851.3032f, 517.4547f, -883.7493f),
            new(872.1447f, 518.4930f, -237.8645f),
        },
    };
}
