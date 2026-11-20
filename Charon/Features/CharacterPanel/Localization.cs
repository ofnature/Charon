// Ported from Character Panel Refined by Kouzukii - https://github.com/Kouzukii/ffxiv-characterstatus-refined
// (commit 0e4b68c, v1.8.6.0). MIT License, Copyright (c) 2022 Kouzukii - the full notice is in
// Charon/Features/CharacterPanel/LICENSE-CharacterPanelRefined.txt.
// Kept close to the original, formatting included, so a later upstream change can be diffed in.
// Changed for Charon: generated from Localization.resx (English only; the original also ships de/fr/ja).
namespace Charon.Features.CharacterPanel;

/// <summary>The original's English strings (its Localization.resx). Charon is English only.</summary>
public static class Localization {
    public const string Panel_Crit_Chance = "Crit Chance";
    public const string Panel_Crit_Damage = "Crit Damage";
    public const string Panel_Damage_Increase = "Damage Increase";
    public const string Panel_Damage_per_100_Potency = "Avg. Damage";
    public const string Panel_Heal_per_100_Potency = "Avg. Healing";
    public const string Panel_Damage_Mitigation = "Damage Mitigation";
    public const string Panel_Direct_Hit_Chance = "Direct Hit Chance";
    public const string Panel_FireIII_GCD = "Fire III Cast";
    public const string Panel_RubyRite_GCD = "Ruby Rite Recast";
    public const string Panel_BlizzardInCyan_GCD = "Blizzard i.C. Recast";
    public const string Panel_GCD = "Recast";
    public const string Panel_Magic_Mitigation = "Magic Mitigation";
    public const string Panel_Mana_per_Tick = "Mana per Tick";
    public const string Panel_Physical_Mitigation = "Physical Mitigation";
    public const string Panel_Skill_Speed_Increase = "Recast Time Reduction";
    public const string Panel_Speed_Properties = "Speed Properties";
    public const string Panel_Item_level_Sync = "Item Level Sync";
    public const string Tooltips_Crit_Tooltip = "{@Title}Critical Hit{@Clear} affects the rate at which your attacks and heals can critically hit as well as the damage dealt and HP restored by a critical hit.\n\nCritical Hit Rate and Damage increase by 0.1% every {PointsPerTier} points.";
    public const string Tooltips_Determination_Tooltip = "{@Title}Determination{@Clear} increases damage dealt by attacks as well as HP restored by healing actions.\n\nDetermination's effect increases by 0.1% every {PointsPerTier} points.";
    public const string Tooltips_Direct_Hit_Tooltip = "{@Title}Direct Hit Rate{@Clear} affects the rate at which your attacks can direct hit.\nA direct hit increases damage done by 25%.\n\nDirect Hit Rate increases by 0.1% every {PointsPerTier} points.";
    public const string Tooltips_Skill_Spell_Speed_Tooltip = "{@Title}Skill/Spell Speed{@Clear} shortens cast times and recast timers as well as increase damage done by damage over time effects.";
    public const string Tooltips_Skill_Spell_Speed_GCD = "A {GCD}s GCD is sped up by 0.01s on average every {PointsPerTier} points.";
    public const string Tooltips_Skill_Spell_Speed_With_Mod = "{@Highlight}These values assume {@Red}{ModName}{@Clear} is active.\nHold Ctrl to view these values without {@Red}{ModName}{@Clear}.{@Clear}";
    public const string Tooltips_Skill_Spell_Speed_Without_Mod = "{@Highlight}Hold Ctrl to view these values assuming {@Red}{ModName}{@Clear} is active.{@Clear}";
    public const string Tooltips_Skill_Spell_Speed_DoT_Increase = "DoT damage increases by 0.1% every {PointsPerTier} points.";
    public const string Tooltips_Next_Tier = "To reach the next tier you need {@Highlight}{NextTier}{@Clear} {Points}.";
    public const string Tooltips_Wasting = "You are currently wasting {@Wasting}{Wasting}{@Clear} {Points}.";
    public const string Tooltips_Wasting_Points_Singular = "point";
    public const string Tooltips_Wasting_Points_Plural = "points";
    public const string Tooltips_Expected_Damage = "Average damage of a 100 potency skill, including critical and direct hits as well as passive traits such as Maim and Mend.\n\nA normal attack will do {@Highlight}{NormalValue}{@Clear} damage.\nA critical hit will do {@Highlight}{CritValue}{@Clear} damage.\n\nDamage always has a variance of \u00b15%.";
    public const string Tooltips_Expected_Heal = "Average HP restored by a 100 potency healing action, including critical hits as well as passive traits such as Maim and Mend.\n\nA normal heal will restore {@Highlight}{NormalValue}{@Clear} HP.\nA critical hit will restore {@Highlight}{CritValue}{@Clear} HP.\n\nHP restored will always vary by \u00b13%.";
    public const string Tooltips_Tenacity_Tooltip = "{@Title}Tenacity{@Clear} increases damage dealt and HP restored by your own actions as well as reducing damage taken.\n\nDamage reduction increases by 0.1% every {PointsPerTier} points.";
    public const string Tooltips_Tenacity_Damage_Increase = "Damage and healing increases by 0.1% every {PointsPerTier} points.";
    public const string Tooltips_Piety_Tooltip = "{@Title}Piety{@Clear} increases the amount of MP gained per server tick (every 3s).\nBy default you will gain 200 MP per tick.\n\nPiety gives 1 extra MP every {PointsPerTier} points.";
    public const string Tooltips_Defense_Tooltip = "{@Title}Defense{@Clear} reduces physical damage taken by 1% every {PointsPerTier} points.";
    public const string Tooltips_Magic_Defense_Tooltip = "{@Title}Magic Defense{@Clear} reduces magic damage taken by 1% every {PointsPerTier} points.";
    public const string Tooltips_Vitality_Tooltip = "{@Title}Vitality{@Clear} increases max HP by {HpPerPoint} every point.\nAs a {Job} you have {BaseHp} base HP.";
    public const string Tooltips_Main_Stat_Tooltip = "{@Title}Main Stat{@Clear} increases damage dealt as well as HP restored by your actions.\nAn increase of 10% main stat constitutes a 10% increase in damage and healing.\n\nSMNs physick is the only exception since it scales with MND instead of INT.";
    public const string Tooltips_Item_Level_Sync = "{@Title}Item Level Sync{@Clear} applies whenever you enter a duty that {@Red}lowers your character's level{@Clear} or one that enforces a maximum ilvl, either when the duty is listed as {@Red}\ue0bd Item Level Sync{@Clear} or when queueing with {@Red}Minimum IL{@Clear} enabled.\n\nAny item with an ilvl higher than the limit will lose all its melds and its stats will be truncated to the highest stat of an equivalent item at the maximum ilvl.";
    public const string Panel_excluding_Consumables = "excl. Consumables";
    public const string Panel_CP = "CP";
    public const string Panel_GP = "GP";
    public const string Buff_LeyLines = "Ley Lines";
    public const string Buff_LeyLines_Abbrev = "LL";
    public const string Buff_GreasedLightning = "Greased Lightning";
    public const string Buff_GreasedLightning_Abbrev = "GL";
    public const string Buff_Shifu = "Shifu";
    public const string Buff_PresenceOfMind = "Presence of Mind";
    public const string Buff_PresenceOfMind_Abbrev = "PoM";
    public const string Buff_Huton = "Huton";
    public const string Buff_ArmysPaeon = "Army's Paeon";
    public const string Buff_ArmysPaeon_Abbrev = "AP";
    public const string Buff_Swiftscaled = "Swiftscaled";
    public const string Buff_Swiftscaled_Abbrev = "S";
    public const string Buff_Hyperphantasia = "Hyperphantasia";
    public const string Buff_Hyperphantasia_Abbrev = "Hp";
}
