// Ported from Character Panel Refined by Kouzukii - https://github.com/Kouzukii/ffxiv-characterstatus-refined
// (commit 0e4b68c, v1.8.6.0). MIT License, Copyright (c) 2022 Kouzukii - the full notice is in
// Charon/Features/CharacterPanel/LICENSE-CharacterPanelRefined.txt.
// Kept close to the original, formatting included, so a later upstream change can be diffed in.
namespace Charon.Features.CharacterPanel;

public enum Attributes : int {
    Strength = 1,
    Dexterity = 2,
    Vitality = 3,
    Intelligence = 4,
    Mind = 5,
    Piety = 6,
    MaxHp = 7,
    MaxMp = 8,
    MaxGp = 10,
    MaxCp = 11,
    Tenacity = 19,
    AttackPower = 20,
    Defense = 21,
    DirectHit = 22,
    MagicDefense = 24,
    CriticalHit = 27,
    AttackMagicPotency = 33,
    HealingMagicPotency = 34,
    Determination = 44,
    SkillSpeed = 45,
    SpellSpeed = 46,
    Craftsmanship = 70,
    Control = 71,
    Gathering = 72,
    Perception = 73
}