using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.GrandCompany;

/// <summary>Which board a delivery mission came from — the game's own three tabs.</summary>
public enum GcMissionKind
{
    /// <summary>DoH: crafted hand-ins, one per crafting class per day.</summary>
    Supply,

    /// <summary>DoL: gathered hand-ins, one per gathering class per day.</summary>
    Provisioning,

    /// <summary>Unlimited gear hand-ins for seals — the loop SealBreaker runs.</summary>
    ExpertDelivery,
}

/// <param name="Position">The row's own index on the board: 0-7 supply, 8-10 provisioning, 11+ expert.</param>
/// <param name="Possessed">What the GAME counts as held — inventory only, which is why it reads 0/0 while the item sits in a retainer.</param>
/// <param name="TurnInAvailable">The game's flag, already inverted to "yes you can" (it stores 0 for available).</param>
/// <param name="AvailabilityRaw">
/// The byte that flag was read from, kept because its meaning for SUPPLY and PROVISIONING rows is not
/// documented the way the expert-delivery one is — so the UI shows it rather than pretending to know.
/// </param>
public sealed record GcDailyMission(
    int Position,
    GcMissionKind Kind,
    string Job,
    uint ItemId,
    string ItemName,
    int Requested,
    int ExpReward,
    int SealReward,
    int Possessed,
    bool BonusReward,
    bool TurnInAvailable,
    byte AvailabilityRaw = 0);

/// <summary>One row's readiness, with the numbers already worked out.</summary>
public sealed record GcMissionPlan(GcDailyMission Mission, int InBags, int InRetainers, string Status)
{
    public int Held => InBags + InRetainers;

    /// <summary>
    /// True when the item is where it needs to be for a hand-in at the officer. Gear also needs the game's
    /// own availability flag, which is documented for gear; a supply or provisioning row is judged on the
    /// fact that you hold it, because the flag beside those rows is not explained anywhere.
    /// </summary>
    public bool Ready => Mission.Requested > 0
                         && InBags >= Mission.Requested
                         && (Mission.Kind != GcMissionKind.ExpertDelivery || Mission.TurnInAvailable);

    /// <summary>True when it exists but is not in the bags — the retainer-fetch case.</summary>
    public bool NeedsFetch => !Ready
                              && Held >= Mission.Requested
                              && Mission.Requested > 0
                              && (Mission.Kind != GcMissionKind.ExpertDelivery || Mission.TurnInAvailable);
}

/// <summary>
/// The daily Grand Company board, read as data.
///
/// Three quarters of the value here is that Charon already knows things the board does not: the game's own
/// "possessed" column counts your INVENTORY only, so a row reads 0/0 while the item sits in a retainer's
/// bags — which is exactly the case the retainer contents store answers. And a short Supply row is a crafting
/// job (Hephaestus) while a short Provisioning row is a gathering job (Odysseus), so the verdict says which
/// rather than leaving "short" as a dead end.
///
/// Pure: the caller hands in what it read.
/// </summary>
public static class GcDailies
{
    /// <summary>The board's own order — positions 0-7, one per crafting class.</summary>
    public static readonly string[] SupplyJobs = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];

    /// <summary>Positions 8-10, one per gathering class.</summary>
    public static readonly string[] ProvisioningJobs = ["MIN", "BTN", "FSH"];

    public const int SupplyCount = 8;
    public const int ProvisioningOffset = 8;
    public const int ProvisioningCount = 3;

    /// <summary>Grand Company ids as the client stores them.</summary>
    public static string GrandCompanyName(byte id) => id switch
    {
        1 => "Maelstrom",
        2 => "Order of the Twin Adder",
        3 => "Immortal Flames",
        _ => "no Grand Company",
    };

    public static GcMissionKind KindFor(int position) => position switch
    {
        < ProvisioningOffset => GcMissionKind.Supply,
        < ProvisioningOffset + ProvisioningCount => GcMissionKind.Provisioning,
        _ => GcMissionKind.ExpertDelivery,
    };

    public static string JobFor(int position) => position switch
    {
        < SupplyCount => SupplyJobs[position],
        < ProvisioningOffset + ProvisioningCount => ProvisioningJobs[position - ProvisioningOffset],
        _ => string.Empty,
    };

    /// <summary>
    /// One row's verdict. The order matters: what the game has already taken, then what is in the bags,
    /// then what is in a retainer (a trip, but a known one), then what has to be made or gathered.
    /// </summary>
    public static GcMissionPlan Plan(GcDailyMission mission, int inBags, int inRetainers)
    {
        var held = inBags + inRetainers;

        if (mission.Requested <= 0)
            return new GcMissionPlan(mission, inBags, inRetainers, "nothing requested");

        // Gear's flag IS documented (0 = the game will take it), so it can decide there.
        if (mission.Kind == GcMissionKind.ExpertDelivery && !mission.TurnInAvailable)
            return new GcMissionPlan(mission, inBags, inRetainers, "the game will not take this one");

        if (inBags >= mission.Requested)
        {
            return new GcMissionPlan(mission, inBags, inRetainers,
                Flagged(mission, $"ready in the bags — hand in {mission.Requested}"
                                 + (mission.BonusReward ? " (bonus)" : string.Empty)));
        }

        if (held >= mission.Requested)
            return new GcMissionPlan(mission, inBags, inRetainers, Flagged(mission, "in a retainer — fetch it first"));

        // Short is a fact (you do not hold it), and it stays a fact whatever the flag says: the daily state of
        // a supply row is the game's business, and a verdict built on a byte nobody has explained would either
        // send you crafting something already delivered or hide work that is still there.
        var short_ = mission.Requested - held;
        return new GcMissionPlan(mission, inBags, inRetainers, Flagged(mission, mission.Kind switch
        {
            GcMissionKind.Supply => $"short {short_} — a craft (Supply)",
            GcMissionKind.Provisioning => $"short {short_} — a gather (Provisioning)",
            _ => $"gear hand-in: {short_} more for a full delivery",
        }));
    }

    /// <summary>How many rows of each kind the board carries — the tiles and the status line both want this.</summary>
    public static (int Supply, int Provisioning, int Expert) Counts(IEnumerable<GcDailyMission> missions)
    {
        var supply = 0;
        var provisioning = 0;
        var expert = 0;
        foreach (var mission in missions)
        {
            switch (mission.Kind)
            {
                case GcMissionKind.Supply: supply++; break;
                case GcMissionKind.Provisioning: provisioning++; break;
                default: expert++; break;
            }
        }

        return (supply, provisioning, expert);
    }

    /// <summary>
    /// Add the game's own availability flag to a verdict WITHOUT letting it change the verdict.
    ///
    /// For a supply or provisioning row the flag is read but not explained anywhere, and it is not ours to
    /// interpret: showing it beside the facts means no claim is made and the evidence still reaches the player
    /// (and the session log) instead of being thrown away or trusted.
    /// </summary>
    private static string Flagged(GcDailyMission mission, string verdict) =>
        mission.TurnInAvailable
            ? verdict
            : mission.Kind == GcMissionKind.ExpertDelivery
                ? verdict
                : $"{verdict}  ·  game flag {mission.AvailabilityRaw}";

    /// <summary>Per-row plans for a whole board, with the bag and retainer counts resolved by the caller.</summary>
    public static IReadOnlyList<GcMissionPlan> PlanAll(
        IEnumerable<GcDailyMission> missions,
        Func<uint, int> inBags,
        Func<uint, int> inRetainers) =>
        missions
            .Where(m => m.ItemId != 0)
            .Select(m => Plan(m, inBags(m.ItemId), inRetainers(m.ItemId)))
            .OrderBy(p => p.Mission.Position)
            .ToList();

    /// <summary>The one-line summary for the section header: what is worth going to the officer for.</summary>
    public static string Summarise(IReadOnlyList<GcMissionPlan> plans)
    {
        var ready = plans.Count(p => p.Ready);
        var fetch = plans.Count(p => p.NeedsFetch);
        var exp = plans.Where(p => p.Ready).Sum(p => p.Mission.ExpReward);
        var seals = plans.Where(p => p.Ready).Sum(p => p.Mission.SealReward);

        if (ready == 0 && fetch == 0)
            return "nothing is handable right now";

        var parts = new List<string>();
        if (ready > 0)
            parts.Add($"{ready} handable now ({exp:N0} exp, {seals:N0} seals)");
        if (fetch > 0)
            parts.Add($"{fetch} in retainers");

        return string.Join(" · ", parts);
    }
}
