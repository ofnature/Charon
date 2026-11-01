using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.DeepDungeon;

/// <summary>One party member as the coffer rule sees them.</summary>
public readonly record struct PartyVitals(string Name, uint Hp, uint MaxHp);

/// <summary>
/// When a SILVER deep-dungeon coffer may be opened. Pure — no Dalamud types.
///
/// Silver coffers can be trap chests, and the damage has killed unattended toons. Charon used to skip
/// one only below 77% of ITS OWN HP; the rule now follows Deep Ducking's (Aethertek's deep dungeon
/// plugin, idea only — its automation is private): open silver ONLY when every party member present is
/// at FULL HP and nothing hostile is in combat with any of them. The case that kills is opening one mid
/// pull, or beside a partner who is already low — neither of which the old self-only check could see.
/// A member at 0 HP is not at full HP, so a dead partner holds the coffer too (a raise comes first).
/// Every refusal names its reason, so the status line says WHO is not topped up.
/// </summary>
public static class SilverCofferPolicy
{
    public static (bool MayOpen, string Reason) Decide(IReadOnlyList<PartyVitals> party, int threats)
    {
        if (threats > 0)
            return (false, $"holding — silver coffer: {threats} hostile(s) in combat with the party");

        var hurt = party.Where(m => m.MaxHp == 0 || m.Hp < m.MaxHp).Select(m => m.Name).ToList();
        if (hurt.Count > 0)
            return (false, $"holding — silver coffer: not everyone is at full HP ({string.Join(", ", hurt)})");

        return (true, "silver coffer: party full and no threats");
    }
}
