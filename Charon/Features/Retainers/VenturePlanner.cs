using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Charon.Features.Retainers;

/// <summary>How a retainer's next venture is chosen.</summary>
public enum VentureAssignment
{
    /// <summary>Leave it alone — you will pick for this one yourself.</summary>
    Off,

    /// <summary>Whatever pays best per hour that it can actually complete (the "best it can do" setting).</summary>
    BestValue,

    /// <summary>One venture you picked by hand for this retainer.</summary>
    Picked,

    /// <summary>Whatever brings back an item on the farm list.</summary>
    FromFarm,
}

/// <summary>One item you want brought back, with an optional target count.</summary>
public sealed record FarmTarget(uint ItemId, string Name, int? Wanted)
{
    /// <summary>
    /// Read one farm-list line. Accepts an item id, a name, and an optional count: <c>36165</c>,
    /// <c>Manganese Ore</c>, <c>36165 x500</c>, <c>Manganese Ore x500</c>.
    /// </summary>
    public static bool TryParse(string line, out FarmTarget target)
    {
        target = new FarmTarget(0, string.Empty, null);
        var text = (line ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        int? wanted = null;
        var marker = text.LastIndexOf('x');
        if (marker > 0 && int.TryParse(text[(marker + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            wanted = count;
            text = text[..marker].Trim();
        }

        if (text.Length == 0)
            return false;

        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            target = new FarmTarget(id, string.Empty, wanted);
            return true;
        }

        target = new FarmTarget(0, text, wanted);
        return true;
    }
}

/// <summary>One farm-list line resolved as far as Charon can take it: who supplies it, on what, and why not.</summary>
/// <param name="Blocker">Null when someone can supply it; otherwise the reason nobody can.</param>
public sealed record FarmPlan(
    FarmTarget Target,
    string Retainer,
    VentureDef? Venture,
    int QuantityPerRun,
    long PriceEach,
    string? Blocker)
{
    /// <summary>Runs still needed to reach the wanted count, when a count and a per-run quantity are both known.</summary>
    public int? RunsNeeded =>
        Target.Wanted is > 0 && QuantityPerRun > 0
            ? (int)Math.Ceiling(Target.Wanted.Value / (double)QuantityPerRun)
            : null;
}

/// <summary>
/// Turning settings into a decision: which venture each retainer runs, and how the farm list maps onto
/// the retainers that can serve it. Pure, so "why is T'sala running that" has an answer that can be
/// tested instead of argued about.
/// </summary>
public static class VenturePlanner
{
    /// <summary>
    /// The venture a retainer should run next, or null when nothing should be assigned.
    /// </summary>
    public static VentureOption? Resolve(
        RetainerProfile retainer,
        VentureAssignment mode,
        uint pickedTaskId,
        IReadOnlyList<FarmTarget> farm,
        IReadOnlyList<VentureDef> ventures,
        IReadOnlyDictionary<uint, long> prices)
    {
        switch (mode)
        {
            case VentureAssignment.Off:
                return null;

            case VentureAssignment.Picked:
            {
                foreach (var option in VentureCatalog.Rank(retainer, ventures, prices))
                {
                    if (option.Venture.TaskId == pickedTaskId)
                        return option.Runnable ? option : null;
                }

                return null;
            }

            case VentureAssignment.FromFarm:
                return BestForFarm(retainer, farm, ventures, prices);

            default:
                return VentureCatalog.Best(retainer, ventures, prices);
        }
    }

    /// <summary>
    /// The best venture that returns any farmed item this retainer can run. Highest value per hour wins,
    /// so a list of six items does not turn into six arbitrary picks.
    /// </summary>
    public static VentureOption? BestForFarm(
        RetainerProfile retainer,
        IReadOnlyList<FarmTarget> farm,
        IReadOnlyList<VentureDef> ventures,
        IReadOnlyDictionary<uint, long> prices)
    {
        if (farm.Count == 0)
            return null;

        var wanted = farm.Select(f => f.ItemId).Where(id => id != 0).ToHashSet();
        var names = farm.Select(f => f.Name).Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        VentureOption? best = null;
        foreach (var option in VentureCatalog.Rank(retainer, ventures, prices))
        {
            if (!option.Runnable || option.Venture.IsRandom || option.Venture.ItemId == 0)
                continue;

            var supplies = wanted.Contains(option.Venture.ItemId) ||
                           (option.Venture.ItemName.Length > 0 && names.Contains(option.Venture.ItemName));
            if (!supplies)
                continue;

            if (best == null || option.ValuePerHour > best.ValuePerHour)
                best = option;
        }

        return best;
    }

    /// <summary>
    /// Map the farm list onto the retainers, one target at a time, without double-booking a retainer while
    /// another one could take the work. Targets nobody can serve keep their blocker so the list cannot
    /// silently pretend it is being farmed.
    /// </summary>
    public static List<FarmPlan> PlanFarm(
        IReadOnlyList<FarmTarget> farm,
        IReadOnlyList<RetainerProfile> retainers,
        IReadOnlyList<VentureDef> ventures,
        IReadOnlyDictionary<uint, long> prices)
    {
        var plans = new List<FarmPlan>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Highest-value targets first: they are the ones worth spending a retainer slot on.
        foreach (var target in farm.OrderByDescending(t => PriceOf(t, ventures, prices)))
        {
            var candidates = new List<(VentureOption Option, RetainerProfile Retainer)>();
            foreach (var retainer in retainers.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var option in VentureCatalog.Rank(retainer, ventures, prices))
                {
                    if (!option.Runnable || option.Venture.IsRandom || option.Venture.ItemId == 0)
                        continue;

                    if (option.Venture.ItemId != target.ItemId &&
                        !option.Venture.ItemName.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    candidates.Add((option, retainer));
                }
            }

            if (candidates.Count == 0)
            {
                plans.Add(new FarmPlan(target, string.Empty, null, 0, 0, "no retainer can bring this back yet"));
                continue;
            }

            // Prefer a free retainer; otherwise take the best value and note nothing — plans are advisory.
            var pick = candidates.FirstOrDefault(c => !taken.Contains(c.Retainer.Name));
            if (pick.Option == null)
                pick = candidates[0];

            taken.Add(pick.Retainer.Name);
            plans.Add(new FarmPlan(
                target,
                pick.Retainer.Name,
                pick.Option.Venture,
                pick.Option.QuantityPerRun,
                pick.Option.PriceEach,
                null));
        }

        return plans;
    }

    private static long PriceOf(FarmTarget target, IReadOnlyList<VentureDef> ventures, IReadOnlyDictionary<uint, long> prices)
    {
        if (target.ItemId != 0 && prices.TryGetValue(target.ItemId, out var price))
            return price;

        foreach (var venture in ventures)
        {
            if (venture.ItemId != 0 &&
                (venture.ItemId == target.ItemId ||
                 venture.ItemName.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) &&
                prices.TryGetValue(venture.ItemId, out var p))
            {
                return p;
            }
        }

        return 0;
    }
}
