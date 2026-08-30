using System.Collections.Generic;

namespace Charon.Features.DeepDungeon;

/// <summary>
/// Deep-dungeon object ids, ported from NecroLens's DataIds (MIT, Jukkales/NecroLens) — a
/// curated, per-dungeon table grown from live observation over years; exactly the kind of data
/// that must be copied, never re-derived. Base ids for the world objects (chests, traps,
/// passage, return) and NameIds for the special BattleNpcs (mimics, friendlies).
/// </summary>
public static class DeepDungeonIds
{
    public const uint SilverChest = 2007357;
    public const uint GoldChest = 2007358;
    public const uint MimicChest = 2006020;
    public const uint AccursedHoard = 2007542;
    public const uint AccursedHoardCoffer = 2007543;

    /// <summary>Bronze coffers, per dungeon (PotD / HoH / EO / Pilgrim's Traverse).</summary>
    public static readonly HashSet<uint> BronzeChests = new()
    {
        782, 783, 784, 785, 786, 787, 788, 789, 790, 802, 803, 804, 805,
        1036, 1037, 1038, 1039, 1040, 1041, 1042, 1043, 1044, 1045, 1046, 1047, 1048, 1049,
        1541, 1542, 1543, 1544, 1545, 1546, 1547, 1548, 1549, 1550, 1551, 1552, 1553, 1554,
        1882, 1884, 1885, 1886, 1888, 1889, 1890, 1891, 1892, 1893, 1906, 1907, 1908,
    };

    /// <summary>Revealed trap objects, with their names.</summary>
    public static readonly Dictionary<uint, string> Traps = new()
    {
        { 2007182, "Landmine" },
        { 2007183, "Luring Trap" },
        { 2007184, "Enfeebling Trap" },
        { 2007185, "Impeding Trap" },
        { 2007186, "Toad Trap" },
        { 2009504, "Odder Trap" },
        { 2013284, "Owlet Trap" },
        { 2014939, "Fae Trap" },
    };

    public static readonly HashSet<uint> Passages = new() { 2007188, 2009507, 2013287, 2014756 };
    public static readonly HashSet<uint> Returns = new() { 2007187, 2009506, 2013286, 2014755 };

    /// <summary>Mimic BattleNpc NameIds — they get the bigger PotD aggro radius.</summary>
    public static readonly HashSet<uint> MimicNames = new()
    {
        2566, 6362, 6363, 7392, 7393, 7394, 5832, 5834, 5835, 15997, 15998, 15999, 16002, 16003, 18889, 18890
    };

    /// <summary>Friendly floor NPCs (never drawn as threats).</summary>
    public static readonly HashSet<uint> FriendlyNames = new()
    {
        5840, 5041, 7610, 7396, 7397, 7398, 16007, 16008, 16009, 16012, 18898, 18899, 18900
    };

    /// <summary>Object ids NecroLens learned to skip (triggered traps, boss-room scenery …).</summary>
    public static readonly HashSet<uint> Ignored = new()
    {
        0, 6388, 1023070, 2000608, 2005809, 2001168,
        15898, 15899, 15860, 18867, 18868, 18869, 10489, 16926, 7245, 13961, 10487
    };

    /// <summary>Palace of the Dead map ids — mimics there aggro from ~14y instead of ~10y.</summary>
    public static readonly HashSet<uint> PotdMaps = new()
    {
        561, 562, 563, 564, 565, 593, 594, 595, 596, 597, 598, 599, 600, 601, 602, 603, 604, 605, 606, 607
    };
}
