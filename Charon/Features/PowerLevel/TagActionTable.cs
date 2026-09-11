namespace Charon.Features.PowerLevel;

/// <summary>One job's tag: the action, when it unlocks, its reach, and whether it is a cast.</summary>
/// <param name="Range">Yalms to the target's hitbox edge. Sheet range -1 means "weapon range",
/// which is 25y for the ranged physical jobs.</param>
/// <param name="IsCast">Casts fail while moving, so they only fire while the toon stands still.</param>
public sealed record TagAction(uint ActionId, string Name, int Level, float Range, bool IsCast);

/// <summary>
/// The ranged attack each job uses to TAG a mob for Quick Kill — one hit from range, so a toon
/// joins the kill without walking into it. Deliberately not a rotation: the cheapest ranged
/// damage each job owns, nothing smarter. Pure and testable.
///
/// Every id VERIFIED against the Action sheet (XIVAPI, player PvE rows — the PvP variants share
/// the names but not the ids) and every ClassJob row id likewise. The melee and tank tags unlock
/// at 15, which lines up with the leveling doctrine that toons under 15 run class hunts instead;
/// ranged and caster jobs tag with their very first attack. Upgraded forms (Stone to Glare, Heavy
/// Shot to Burst Shot) are resolved by the game at use time through GetAdjustedActionId, so the
/// base ids stay correct at every level.
///
/// BEASTMASTER tags with Capture: a 10y attack (potency 100) that also marks the beast with
/// Interest Captured, and if the beast is DEFEATED while marked, that Beastmaster forges a pact
/// with it and gains a bestiary entry — so every Beastmaster landing its own Capture before the
/// carry finishes the kill is exactly what power-levelling them wants. Verified from the Action
/// sheet (hostile target, range 10, ability) and the game's own description text. Its other
/// long-range buttons are NOT tags: Gauge only inspects capture odds, Parting Blow and Trick
/// order the familiar (Parting Blow is an AoE that could clip unclaimed mobs), and Shield Charge
/// rushes in.
///
/// NO TAG: Pugilist/Monk has no ranged attack at all. It returns null and Quick Kill says so,
/// rather than improvising a melee swing.
/// </summary>
public static class TagActionTable
{
    // ClassJob row ids (verified)
    public const uint Gladiator = 1;
    public const uint Pugilist = 2;
    public const uint Marauder = 3;
    public const uint Lancer = 4;
    public const uint Archer = 5;
    public const uint Conjurer = 6;
    public const uint Thaumaturge = 7;
    public const uint Paladin = 19;
    public const uint Monk = 20;
    public const uint Warrior = 21;
    public const uint Dragoon = 22;
    public const uint Bard = 23;
    public const uint WhiteMage = 24;
    public const uint BlackMage = 25;
    public const uint Arcanist = 26;
    public const uint Summoner = 27;
    public const uint Scholar = 28;
    public const uint Rogue = 29;
    public const uint Ninja = 30;
    public const uint Machinist = 31;
    public const uint DarkKnight = 32;
    public const uint Astrologian = 33;
    public const uint Samurai = 34;
    public const uint RedMage = 35;
    public const uint BlueMage = 36;
    public const uint Gunbreaker = 37;
    public const uint Dancer = 38;
    public const uint Reaper = 39;
    public const uint Sage = 40;
    public const uint Viper = 41;
    public const uint Pictomancer = 42;
    public const uint Beastmaster = 43;

    private static readonly TagAction ShieldLob = new(24, "Shield Lob", 15, 20f, false);
    private static readonly TagAction Tomahawk = new(46, "Tomahawk", 15, 20f, false);
    private static readonly TagAction Unmend = new(3624, "Unmend", 15, 20f, false);
    private static readonly TagAction LightningShot = new(16143, "Lightning Shot", 15, 20f, false);
    private static readonly TagAction PiercingTalon = new(90, "Piercing Talon", 15, 20f, false);
    private static readonly TagAction ThrowingDagger = new(2247, "Throwing Dagger", 15, 20f, false);
    private static readonly TagAction Enpi = new(7486, "Enpi", 15, 20f, false);
    private static readonly TagAction Harpe = new(24386, "Harpe", 15, 25f, true);
    private static readonly TagAction WrithingSnap = new(34632, "Writhing Snap", 15, 20f, false);
    private static readonly TagAction HeavyShot = new(97, "Heavy Shot", 1, 25f, false);
    private static readonly TagAction SplitShot = new(2866, "Split Shot", 1, 25f, false);
    private static readonly TagAction Cascade = new(15989, "Cascade", 1, 25f, false);
    private static readonly TagAction Stone = new(119, "Stone", 1, 25f, true);
    private static readonly TagAction Blizzard = new(142, "Blizzard", 1, 25f, true);
    private static readonly TagAction Ruin = new(163, "Ruin", 1, 25f, true);
    private static readonly TagAction ScholarRuin = new(17869, "Ruin", 1, 25f, true); // SCH has its own row
    private static readonly TagAction Malefic = new(3596, "Malefic", 1, 25f, true);
    private static readonly TagAction Dosis = new(24283, "Dosis", 1, 25f, true);
    private static readonly TagAction Jolt = new(7503, "Jolt", 2, 25f, true);
    private static readonly TagAction FireInRed = new(34650, "Fire in Red", 1, 25f, true);
    private static readonly TagAction WaterCannon = new(11385, "Water Cannon", 1, 25f, true);
    private static readonly TagAction Capture = new(44880, "Capture", 1, 10f, false);

    /// <summary>The job's tag action regardless of level; null when the job has none.</summary>
    public static TagAction? ForJob(uint classJobId) => classJobId switch
    {
        Gladiator or Paladin => ShieldLob,
        Marauder or Warrior => Tomahawk,
        DarkKnight => Unmend,
        Gunbreaker => LightningShot,
        Lancer or Dragoon => PiercingTalon,
        Rogue or Ninja => ThrowingDagger,
        Samurai => Enpi,
        Reaper => Harpe,
        Viper => WrithingSnap,
        Archer or Bard => HeavyShot,
        Machinist => SplitShot,
        Dancer => Cascade,
        Conjurer or WhiteMage => Stone,
        Thaumaturge or BlackMage => Blizzard,
        Arcanist or Summoner => Ruin,
        Scholar => ScholarRuin,
        Astrologian => Malefic,
        Sage => Dosis,
        RedMage => Jolt,
        Pictomancer => FireInRed,
        BlueMage => WaterCannon,
        Beastmaster => Capture,
        _ => null, // Pugilist/Monk (no ranged attack exists), DoH/DoL
    };

    /// <summary>The tag this job can use at this level; null when none is learned yet.</summary>
    public static TagAction? Get(uint classJobId, int level) =>
        ForJob(classJobId) is { } tag && level >= tag.Level ? tag : null;
}
