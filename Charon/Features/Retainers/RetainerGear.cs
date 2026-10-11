using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Gear;

namespace Charon.Features.Retainers;

/// <summary>
/// One piece of gear a retainer wears or could wear. <paramref name="Container"/> is -1 for a worn
/// piece (then <paramref name="Slot"/> is the slot it is actually in — a ring's sheet row cannot say
/// which hand); otherwise it is where the piece is sitting in the player's bags or armoury.
/// <paramref name="Gathering"/> and <paramref name="Perception"/> are the piece's REAL values: base,
/// HQ bonus and melded materia, capped (see <see cref="RetainerGearMath.StatWithMateria"/>).
/// </summary>
public sealed record RetainerPiece(
    uint ItemId,
    string Name,
    bool Hq,
    GearSlot Slot,
    int ItemLevel,
    int Gathering,
    int Perception,
    bool TwoHanded = false,
    bool Unique = false,
    int Container = -1,
    int SourceSlot = -1)
{
    public bool Worn => Container < 0;
    public bool IsRing => Slot is GearSlot.RingRight or GearSlot.RingLeft;
}

/// <summary>The three numbers a retainer's gear adds up to.</summary>
public readonly record struct RetainerGearStats(int ItemLevel, int Gathering, int Perception);

/// <summary>
/// What the gear is being chosen FOR: one venture's gate and tier ladder. A gatherer must reach
/// <paramref name="RequiredGathering"/> to be sent at all and then tiers on Perception; a combat
/// retainer gates and tiers on item level.
/// </summary>
public sealed record GearGoal(
    bool Gatherer,
    int RequiredGathering,
    int RequiredItemLevel,
    IReadOnlyList<int> Thresholds,
    int QuantitySteps,
    string VentureName)
{
    public bool Meets(RetainerGearStats stats) =>
        Gatherer ? stats.Gathering >= RequiredGathering : stats.ItemLevel >= RequiredItemLevel;

    public int Tier(RetainerGearStats stats) =>
        VentureCatalog.TierFor(Gatherer ? stats.Perception : stats.ItemLevel, Thresholds, QuantitySteps);

    /// <summary>The next gate above the retainer's tier stat, or null at the top of the ladder.</summary>
    public int? NextGate(RetainerGearStats stats)
    {
        var stat = Gatherer ? stats.Perception : stats.ItemLevel;
        foreach (var gate in Thresholds.Where(g => g > 0).OrderBy(g => g))
        {
            if (stat < gate)
                return gate;
        }

        return null;
    }
}

/// <summary>
/// A retainer's gear numbers as last read at a bell. PERSISTED (Charon.json), so it carries no derived
/// members — Dalamud saves config with Newtonsoft, which evaluates every public getter.
/// </summary>
public sealed class RetainerGearSnapshot
{
    public int ItemLevel { get; set; }
    public int Gathering { get; set; }
    public int Perception { get; set; }
    public DateTime CapturedUtc { get; set; }
}

/// <summary>Rules the game enforces on changing a retainer's gear.</summary>
public static class RetainerGearRules
{
    /// <summary>
    /// Why this retainer's gear cannot be changed right now, or null when it can. THE GAME REFUSES gear
    /// changes on a retainer who is out on a venture (user, 2026-10-11 — it is what made the first live
    /// run fail on every piece). A finished venture that has not been collected still counts: she is
    /// "out" until its report is viewed, and an unread timer is treated the same way rather than tried.
    /// </summary>
    public static string? ChangeBlocker(string retainer, VentureState state) => state switch
    {
        VentureState.Idle => null,
        VentureState.Ready =>
            $"{retainer} has a finished venture to collect — the game will not change her gear until its report is viewed (collect it WITHOUT reassigning, equip, then send her out)",
        _ => $"{retainer} is out on a venture — the game will not change her gear until she is back",
    };
}

/// <summary>Which venture a retainer's gear should be chosen for.</summary>
public static class GearGoals
{
    /// <summary>
    /// The venture the player PICKED for this retainer, else the highest-level hunt her job and level
    /// allow. Deliberately not "the best venture she can run today": that is decided by the gear she is
    /// wearing, so aiming at it would never ask for better gear. The top hunt has the highest gate and
    /// the highest tier ladder — gear that clears it clears everything below.
    /// </summary>
    public static GearGoal For(string job, int level, VentureDef? picked, IEnumerable<VentureDef> ventures)
    {
        var venture = picked ?? ventures
            .Where(v => v.Kind == VentureKind.Hunting && !v.IsRandom && v.RetainerLevel <= level
                        && VentureCatalog.JobFits(job, v.JobCategory))
            .OrderByDescending(v => v.RetainerLevel)
            .ThenByDescending(v => v.RequiredGathering + v.RequiredItemLevel)
            .FirstOrDefault();

        if (venture == null)
            return new GearGoal(job is "MIN" or "BTN" or "FSH", 0, 0, [], 0, "no venture known for this job");

        return new GearGoal(!venture.UsesItemLevel, venture.RequiredGathering, venture.RequiredItemLevel,
            venture.Thresholds, venture.Quantities.Count, $"Lv. {venture.RetainerLevel} {venture.Name}");
    }
}

/// <summary>Put <paramref name="Wear"/> into <paramref name="Slot"/>, displacing <paramref name="Replacing"/>.</summary>
public sealed record GearSwap(GearSlot Slot, RetainerPiece? Replacing, RetainerPiece Wear);

/// <summary>The optimiser's answer. No swaps means the worn set is already the best reachable.</summary>
public sealed record GearPlan(
    IReadOnlyList<GearSwap> Swaps,
    RetainerGearStats Before,
    RetainerGearStats After,
    bool MeetsBefore,
    bool MeetsAfter,
    int TierBefore,
    int TierAfter,
    string Note);

/// <summary>
/// The arithmetic of retainer gear. Pure — no Dalamud types.
///
/// Sources (facts and formulas, each with its licence): the stat formula is ECommons'
/// <c>ExcelItemHelper.GetStat</c> (MIT) — base value, plus the HQ bonus, plus each melded materia's
/// value for its grade, the total capped at what the item's level allows in that slot. The average
/// item level is AutoRetainer's <c>ItemLevel.Calculate</c> (BSD-3, itself after Caraxi's SimpleTweaks):
/// twelve slots, a two-handed main hand counted twice because it fills the off-hand's share.
/// </summary>
public static class RetainerGearMath
{
    /// <summary>The slots that count toward a retainer's average item level (no waist, no soul crystal).</summary>
    public const int SlotsCounted = 12;

    /// <summary>
    /// A piece's real value for one stat. <paramref name="cap"/> &lt;= 0 means "no cap known", which
    /// leaves the sum alone rather than zeroing it.
    /// </summary>
    public static int StatWithMateria(int baseValue, int hqBonus, bool hq, IEnumerable<int> materia, int cap)
    {
        var total = baseValue + (hq ? hqBonus : 0) + materia.Sum();
        return cap > 0 && total > cap ? cap : total;
    }

    public static RetainerGearStats Stats(IEnumerable<RetainerPiece> worn)
    {
        int levels = 0, gathering = 0, perception = 0;
        foreach (var piece in worn)
        {
            levels += piece.ItemLevel * (piece.Slot == GearSlot.MainHand && piece.TwoHanded ? 2 : 1);
            gathering += piece.Gathering;
            perception += piece.Perception;
        }

        return new RetainerGearStats(levels / SlotsCounted, gathering, perception);
    }
}

/// <summary>
/// Chooses the best set a retainer can wear from what it has on plus what the player holds. Pure.
///
/// A gatherer's choice is a real trade-off, not a per-slot maximum: some pieces carry Gathering, some
/// Perception, and the venture needs ENOUGH of the first before any of the second counts. So the search
/// is over whole sets — "the most Perception among the sets that reach the Gathering requirement" — by
/// dynamic programming across slot groups (the two hands are one group because a two-handed tool empties
/// the off-hand, the two rings are one group because one physical ring cannot fill both).
///
/// It proposes NOTHING when the best reachable set lands on the same tier as the worn one: Perception
/// between two gates buys no extra items, and shuffling gear for no gain is churn. A combat retainer is
/// the simple case — highest average item level.
/// </summary>
public static class RetainerGearOptimizer
{
    /// <summary>One way to fill a slot group: the pieces, where they go, and what they add.</summary>
    private sealed record Option(IReadOnlyList<(GearSlot Slot, RetainerPiece Piece)> Fill, int Gathering, int Perception, int Levels, int Changes);

    public static GearPlan Plan(IReadOnlyList<RetainerPiece> worn, IReadOnlyList<RetainerPiece> candidates, GearGoal goal)
    {
        var before = RetainerGearMath.Stats(worn);
        var groups = Groups(worn, candidates);
        var chosen = goal.Gatherer ? BestForGatherer(groups, goal.RequiredGathering) : BestForCombat(groups);

        var fill = chosen.SelectMany(o => o.Fill).ToList();
        var after = RetainerGearMath.Stats(fill.Select(f => f.Piece with { Slot = f.Slot }));
        var swaps = fill
            .Where(f => !(f.Piece.Worn && f.Piece.Slot == f.Slot))
            .Select(f => new GearSwap(f.Slot, worn.FirstOrDefault(w => w.Slot == f.Slot), f.Piece))
            .OrderBy(s => s.Slot)
            .ToList();

        bool meetsBefore = goal.Meets(before), meetsAfter = goal.Meets(after);
        int tierBefore = goal.Tier(before), tierAfter = goal.Tier(after);

        // Worth doing only if it unlocks the venture, raises the tier, or (still short of the gate)
        // gets closer to it. Anything else is moving gear around for nothing.
        var closer = !meetsAfter && (goal.Gatherer ? after.Gathering > before.Gathering : after.ItemLevel > before.ItemLevel);
        var worthIt = swaps.Count > 0 && ((meetsAfter && !meetsBefore) || (meetsAfter && tierAfter > tierBefore) || closer);
        if (!worthIt)
            return new GearPlan([], before, before, meetsBefore, meetsBefore, tierBefore, tierBefore, NothingBetter(goal, before, meetsBefore));

        return new GearPlan(swaps, before, after, meetsBefore, meetsAfter, tierBefore, tierAfter, Describe(goal, after, meetsAfter));
    }

    private static string NothingBetter(GearGoal goal, RetainerGearStats stats, bool meets)
    {
        if (!meets)
        {
            return goal.Gatherer
                ? $"short of Gathering {goal.RequiredGathering} by {goal.RequiredGathering - stats.Gathering}, and nothing you hold closes it"
                : $"short of item level {goal.RequiredItemLevel} by {goal.RequiredItemLevel - stats.ItemLevel}, and nothing you hold closes it";
        }

        var stat = goal.Gatherer ? stats.Perception : stats.ItemLevel;
        var label = goal.Gatherer ? "Perception" : "item level";
        return goal.NextGate(stats) is { } gate
            ? $"already the best you hold — the next tier needs {label} {gate} (+{gate - stat})"
            : "already at the top tier";
    }

    private static string Describe(GearGoal goal, RetainerGearStats after, bool meets)
    {
        if (!meets)
        {
            return goal.Gatherer
                ? $"closer, but still short of Gathering {goal.RequiredGathering} by {goal.RequiredGathering - after.Gathering}"
                : $"closer, but still short of item level {goal.RequiredItemLevel} by {goal.RequiredItemLevel - after.ItemLevel}";
        }

        return goal.NextGate(after) is { } gate
            ? $"next tier at {(goal.Gatherer ? "Perception" : "item level")} {gate}"
            : "reaches the top tier";
    }

    // --- The search ---

    private static List<Option> BestForCombat(List<List<Option>> groups) =>
        groups.Select(g => g.OrderByDescending(o => o.Levels).ThenBy(o => o.Changes).First()).ToList();

    /// <summary>
    /// Most Perception among the sets that reach <paramref name="required"/> Gathering; if none can,
    /// the set with the most Gathering. Gathering is tracked capped at the requirement, since surplus
    /// buys nothing and capping keeps the table small.
    /// </summary>
    private static List<Option> BestForGatherer(List<List<Option>> groups, int required)
    {
        var cap = Math.Max(0, required);

        // capped gathering → the best partial set reaching it.
        var states = new Dictionary<int, (int Perception, int Changes, int Gathering, List<Option> Picks)>
        {
            [0] = (0, 0, 0, new List<Option>()),
        };

        foreach (var group in groups)
        {
            var next = new Dictionary<int, (int Perception, int Changes, int Gathering, List<Option> Picks)>();
            foreach (var (_, state) in states)
            {
                foreach (var option in group)
                {
                    var gathering = state.Gathering + option.Gathering;
                    var key = Math.Min(gathering, cap);
                    var candidate = (state.Perception + option.Perception, state.Changes + option.Changes, gathering,
                        new List<Option>(state.Picks) { option });

                    if (!next.TryGetValue(key, out var held) || Better(candidate, held))
                        next[key] = candidate;
                }
            }

            states = next;
        }

        // Reaching the requirement wins outright; otherwise as close to it as the gear allows.
        return states[states.Keys.Max()].Picks;

        static bool Better(
            (int Perception, int Changes, int Gathering, List<Option> Picks) a,
            (int Perception, int Changes, int Gathering, List<Option> Picks) b) =>
            a.Perception != b.Perception ? a.Perception > b.Perception
            : a.Changes != b.Changes ? a.Changes < b.Changes
            : a.Gathering > b.Gathering;
    }

    // --- Slot groups ---

    private static readonly GearSlot[] SimpleSlots =
    [
        GearSlot.Head, GearSlot.Body, GearSlot.Hands, GearSlot.Legs, GearSlot.Feet,
        GearSlot.Ears, GearSlot.Neck, GearSlot.Wrists,
    ];

    private static List<List<Option>> Groups(IReadOnlyList<RetainerPiece> worn, IReadOnlyList<RetainerPiece> candidates)
    {
        var groups = new List<List<Option>> { HandOptions(worn, candidates), RingOptions(worn, candidates) };

        foreach (var slot in SimpleSlots)
        {
            var pool = worn.Where(p => p.Slot == slot).Concat(candidates.Where(p => p.Slot == slot)).ToList();
            groups.Add(pool.Count == 0
                ? [Empty]
                : pool.Select(p => Make((slot, p))).ToList());
        }

        return groups;
    }

    private static readonly Option Empty = new([], 0, 0, 0, 0);

    private static Option Make(params (GearSlot Slot, RetainerPiece Piece)[] fill)
    {
        int gathering = 0, perception = 0, levels = 0, changes = 0;
        foreach (var (slot, piece) in fill)
        {
            gathering += piece.Gathering;
            perception += piece.Perception;
            levels += piece.ItemLevel * (slot == GearSlot.MainHand && piece.TwoHanded ? 2 : 1);
            if (!(piece.Worn && piece.Slot == slot))
                changes++;
        }

        return new Option(fill, gathering, perception, levels, changes);
    }

    private static List<Option> HandOptions(IReadOnlyList<RetainerPiece> worn, IReadOnlyList<RetainerPiece> candidates)
    {
        var mains = worn.Concat(candidates).Where(p => p.Slot == GearSlot.MainHand).ToList();
        var offs = worn.Concat(candidates).Where(p => p.Slot == GearSlot.OffHand).ToList();
        var options = new List<Option>();

        foreach (var main in mains)
        {
            if (main.TwoHanded)
            {
                options.Add(Make((GearSlot.MainHand, main)));
                continue;
            }

            if (offs.Count == 0)
                options.Add(Make((GearSlot.MainHand, main)));
            foreach (var off in offs)
                options.Add(Make((GearSlot.MainHand, main), (GearSlot.OffHand, off)));
        }

        // No main hand anywhere: whatever off-hand there is still counts.
        if (mains.Count == 0)
            options.AddRange(offs.Select(off => Make((GearSlot.OffHand, off))));

        return options.Count == 0 ? [Empty] : options;
    }

    private static List<Option> RingOptions(IReadOnlyList<RetainerPiece> worn, IReadOnlyList<RetainerPiece> candidates)
    {
        var rings = worn.Concat(candidates).Where(p => p.IsRing).ToList();
        var options = new List<Option>();

        for (var a = 0; a < rings.Count; a++)
        {
            for (var b = a + 1; b < rings.Count; b++)
            {
                // The same Unique ring cannot be worn on both hands.
                if (rings[a].Unique && rings[b].Unique && rings[a].ItemId == rings[b].ItemId)
                    continue;

                options.Add(Make(Seat(rings[a], rings[b])));
            }
        }

        // Fewer than two rings to choose from: wear what there is.
        if (options.Count == 0)
            options.AddRange(rings.Select(r => Make(Seat(r))));

        return options.Count == 0 ? [Empty] : options;
    }

    /// <summary>Which hand each chosen ring goes on: a worn ring stays put, new ones take what is left.</summary>
    private static (GearSlot Slot, RetainerPiece Piece)[] Seat(params RetainerPiece[] rings)
    {
        var free = new List<GearSlot> { GearSlot.RingRight, GearSlot.RingLeft };
        var seats = new List<(GearSlot, RetainerPiece)>();

        foreach (var ring in rings.Where(r => r.Worn))
        {
            if (free.Remove(ring.Slot))
                seats.Add((ring.Slot, ring));
        }

        foreach (var ring in rings)
        {
            if (free.Count == 0 || seats.Any(s => ReferenceEquals(s.Item2, ring)))
                continue;

            seats.Add((free[0], ring));
            free.RemoveAt(0);
        }

        return seats.ToArray();
    }
}
