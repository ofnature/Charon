using System.Collections.Generic;

namespace Charon.Features.DeepDungeon;

/// <summary>
/// What a votive candelabra's effect does to the floor it is lit for (Pilgrim's Traverse). Pure.
///
/// The game's own sheet (DeepDungeon4GimmickEffectTransient, rows 1-10) carries only a name and a line of
/// verse — "Fear not the fire within." — so the effects are from the Console Games Wiki's Pilgrim's
/// Traverse page ("Votive Candelabras", read 2026-11-22; CC BY-NC-SA, rewritten short here, not copied).
/// Three are backed by the game's Status sheet: Anointed 4587 (regenerating HP), Mortific 4588 (damage
/// taken and dealt up, attacks give HP back) and Rapid Recitation 4590 (ability recast down, not charged
/// actions). Immolation turning the party into bombs is also the user's own word. The rest is the wiki's.
///
/// The wiki's rules: lit on one floor, it acts on the NEXT; through stone 30 only the helpful ones come
/// up, above that the last three can; a pomander of devotion always gives Immolation or Primordial Flesh.
/// </summary>
public sealed record VotiveEffect(int Id, string Name, string Short, string Detail, bool Harmful);

public static class VotiveEffects
{
    public static readonly IReadOnlyList<VotiveEffect> All =
    [
        new(1, "Primordial Flesh", "mudball: roll to kill",
            "Everyone becomes a mudball with Rocky Roll: roll forward and kill what you hit (heavy damage only, once you carry Knockback Penalty). No items and no coffers while transformed.", false),
        new(2, "Immolation", "bomb: Big Burst kills",
            "Everyone becomes a bomb with Big Burst, a blast 15y around you that kills enemies outright (it may not at a low aetherpool), and maximum HP is doubled. No items and no coffers while transformed.", false),
        new(3, "Anointment", "regen, full heal each tick",
            "Everyone regenerates, each tick healing to full. A hit big enough between ticks still kills.", false),
        new(4, "Mortification", "deal and take +30%, lifesteal",
            "Damage dealt and damage taken both go up 30%, and attacks give back HP for a share of the damage dealt.", false),
        new(5, "Rapid Recitation", "ability recasts halved",
            "Ability recast times are halved. Charged actions are not affected.", false),
        new(6, "Swift Passage", "much faster movement",
            "Movement speed goes up a great deal, well past Sprint.", false),
        new(7, "Foes' Frailty", "foes take +30%, deal -30%",
            "Every enemy takes 30% more damage and deals 30% less, including ones that appear later (mimics, lured enemies, respawns).", false),
        new(8, "Gathering Gloom", "Gloom: foes faster, stronger",
            "The floor is under Gloom: enemies move faster, hit harder and take less. A pomander of serenity or mazeroot incense removes it.", true),
        new(9, "Treacherous Ground", "more traps",
            "More traps on the floor (no more than one extra per room, none in the first room or in hallways). Sight and safety work as usual.", true),
        new(10, "Blind Faith", "map hidden",
            "The floor map is hidden. A pomander of sight or mazeroot incense shows it.", true),
    ];

    /// <summary>The effect with this sheet row id, or null for 0 (none lit) and for a row this list does not know.</summary>
    public static VotiveEffect? For(int id)
    {
        foreach (var effect in All)
        {
            if (effect.Id == id)
                return effect;
        }

        return null;
    }
}
