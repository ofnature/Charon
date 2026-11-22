using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.DeepDungeon;

public enum PomanderRole
{
    Tank,
    Healer,
    Dps,
}

/// <summary>The two pomanders that buff only the toon that uses them.</summary>
public enum SelfBuffPomander
{
    Steel,
    Strength,
}

/// <summary>When a pomander is worth using blind.</summary>
public enum PomanderTiming
{
    /// <summary>Its effect is on the floor, the next floor or the party: any moment will do.</summary>
    Anytime,

    /// <summary>It acts on "targets within range" or turns the user into something for a short while:
    /// out of combat it does nothing useful.</summary>
    InCombat,
}

/// <summary>One party member, as this client sees them.</summary>
/// <param name="BuffSecondsLeft">Time left on this pomander's buff; 0 = not running (or not a buff).</param>
public sealed record PomanderCandidate(string Name, uint EntityId, PomanderRole Role, bool Alive, float BuffSecondsLeft);

/// <summary>
/// Who uses a pomander when the party's pouch is full of that kind. Pure.
///
/// The rule is the user's (2026-11-21). The trigger is the pouch being FULL of a kind — the first build
/// waited for a new one to be turned away, and covered steel and strength only; the user corrected both
/// the same evening ("need to auto use a pomander when its full", then "all pomanders ... most can be
/// used").
///
/// Steel and strength buff only their user, so they go by role: steel tank, healer, damage; strength
/// damage, healer, tank. A toon whose buff is still running is passed over — unless it has a minute or
/// less left, when it takes one to keep the buff up. If everyone is covered, nothing is used.
///
/// Every other kind acts on the floor, the next floor, the party or the enemies, so it does not matter
/// WHO uses it — only that exactly one toon does. That is the first living toon in strength's order
/// (damage first: a transformation is better spent on a damage dealer than on the tank or the healer).
///
/// The pouch is shared by the party, and every client reaches the same answer from what it sees (role
/// order, then name), so no messages are needed.
/// </summary>
public static class AutoPomanderPolicy
{
    /// <summary>The game's "You return the X to the coffer. You cannot carry any more of that item." (LogMessage row).</summary>
    public const uint OvercapLogMessageId = 7222;

    /// <summary>The most of one pomander the party can carry (BossMod's overcap test reads the same 3).</summary>
    public const int Cap = 3;

    public static bool IsFull(int held) => held >= Cap;

    /// <summary>A buff with this much or less left is refreshed by the next spare one.</summary>
    public const float RefreshBelowSeconds = 60f;

    /// <summary>DeepDungeonItem rows: 3 / 4 pomander of strength / steel, 25 / 26 the Eureka Orthos protomanders.</summary>
    public static SelfBuffPomander? KindOf(uint itemId) => itemId switch
    {
        4 or 26 => SelfBuffPomander.Steel,
        3 or 25 => SelfBuffPomander.Strength,
        _ => null,
    };

    /// <summary>
    /// From the items' own tooltips (DeepDungeonItem, read from the game files): witching (10, 32)
    /// "Transforms all targets within range", and rage (12), lust (13), resolution (16) and dread (22)
    /// "Transforms user into ..." — those wait for combat. Everything else names the floor, the next
    /// floor or the party.
    /// </summary>
    public static PomanderTiming TimingOf(uint itemId) => itemId is 10 or 32 or 12 or 13 or 16 or 22
        ? PomanderTiming.InCombat
        : PomanderTiming.Anytime;

    public static IReadOnlyList<PomanderRole> Order(SelfBuffPomander kind) => kind == SelfBuffPomander.Steel
        ? [PomanderRole.Tank, PomanderRole.Healer, PomanderRole.Dps]
        : [PomanderRole.Dps, PomanderRole.Healer, PomanderRole.Tank];

    /// <summary>
    /// How long each toon in the queue gets before the turn passes to the next. The first run (2026-11-21)
    /// had four kinds sit full for minutes: every one was "Rosa Discord's to use", and that toon's client
    /// was not using them (the switch is per character, and no client can see another's). So a turn that
    /// is not taken passes on — the toon at position n waits n turns.
    /// </summary>
    public static readonly TimeSpan TurnLength = TimeSpan.FromSeconds(10);

    /// <summary>The toons that may use a steel or strength, first choice first. Empty when nobody should.</summary>
    public static IReadOnlyList<PomanderCandidate> Queue(SelfBuffPomander kind, IEnumerable<PomanderCandidate> party) =>
        InOrder(Order(kind), party.Where(c => c.Alive && c.BuffSecondsLeft <= RefreshBelowSeconds));

    /// <summary>The toons that may use any other kind: everyone alive, damage dealers first.</summary>
    public static IReadOnlyList<PomanderCandidate> QueueAny(IEnumerable<PomanderCandidate> party) =>
        InOrder(Order(SelfBuffPomander.Strength), party.Where(c => c.Alive));

    /// <summary>
    /// Whether the toon at <paramref name="position"/> in the queue may act, the pomander having been ready
    /// to use for <paramref name="readyFor"/>: the first at once, each later one a turn after the one before.
    /// </summary>
    public static bool IsTurn(int position, TimeSpan readyFor) =>
        position >= 0 && readyFor >= TurnLength * position;

    /// <summary>The toon that should use a steel or strength first, or null when nobody should.</summary>
    public static PomanderCandidate? Pick(SelfBuffPomander kind, IEnumerable<PomanderCandidate> party) =>
        Queue(kind, party).FirstOrDefault();

    /// <summary>The toon that should use any other kind first: the first living one, damage dealers first.</summary>
    public static PomanderCandidate? PickAny(IEnumerable<PomanderCandidate> party) =>
        QueueAny(party).FirstOrDefault();

    /// <summary>ClassJob.Role: 1 tank, 4 healer, anything else deals damage.</summary>
    public static PomanderRole RoleOf(byte classJobRole) => classJobRole switch
    {
        1 => PomanderRole.Tank,
        4 => PomanderRole.Healer,
        _ => PomanderRole.Dps,
    };

    private static IReadOnlyList<PomanderCandidate> InOrder(IReadOnlyList<PomanderRole> order, IEnumerable<PomanderCandidate> eligible) =>
        eligible
            .OrderBy(c => IndexOf(order, c.Role))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.EntityId)
            .ToList();

    private static int IndexOf(IReadOnlyList<PomanderRole> order, PomanderRole role)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == role)
                return i;
        }

        return order.Count;
    }
}
