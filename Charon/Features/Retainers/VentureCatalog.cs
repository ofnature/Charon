using System;
using System.Collections.Generic;
using System.Globalization;

namespace Charon.Features.Retainers;

/// <summary>Which kind of venture a task is. The game only ever has these two for retainers.</summary>
public enum VentureKind
{
    /// <summary>A 60-minute hunt: fixed item, one venture token, deterministic quantity.</summary>
    Hunting,

    /// <summary>An 18-hour field exploration: a random pool of items, two venture tokens.</summary>
    Exploration,
}

/// <summary>
/// One venture as the game defines it, freed of Lumina types so the choice can be reasoned about and
/// tested without a client. Built by <see cref="Services.Game.VentureSheetReader"/> from
/// <c>RetainerTask</c> + <c>RetainerTaskNormal</c> + <c>RetainerTaskParameter</c>.
/// </summary>
/// <param name="TaskId">The <c>RetainerTask</c> row id — this is what gets handed back to the game to assign it.</param>
/// <param name="Name">Display name: the returned item for a hunt, the pool name for an exploration.</param>
/// <param name="Quantities">The five-step quantity ladder for this venture, low tier first.</param>
/// <param name="Thresholds">The gate ladder that decides which step you land on, same length as <see cref="Quantities"/>.</param>
/// <param name="UsesItemLevel">True when <see cref="Thresholds"/> are item level, false when they are gathering/perception.</param>
public sealed record VentureDef(
    uint TaskId,
    string Name,
    VentureKind Kind,
    uint ItemId,
    string ItemName,
    IReadOnlyList<int> Quantities,
    IReadOnlyList<int> Thresholds,
    bool UsesItemLevel,
    int RetainerLevel,
    int RequiredItemLevel,
    int RequiredGathering,
    string JobCategory,
    int MaxMinutes,
    int VentureCost,
    long Experience,
    bool IsRandom)

{
    /// <summary>Exp per hour on the clock, which is the honest way to compare a hunt with an exploration.</summary>
    public long ExpPerHour => MaxMinutes <= 0 ? Experience : Experience * 60 / MaxMinutes;

    /// <summary>Venture tokens per hour, for the same reason.</summary>
    public double CostPerHour => MaxMinutes <= 0 ? VentureCost : VentureCost * 60.0 / MaxMinutes;
}

/// <summary>
/// What a retainer brings to the table. The gear stats pick the quantity tier, so an unknown stat
/// (0 or -1) means the tier is a guess — which is reported rather than hidden.
/// </summary>
public sealed record RetainerProfile(
    string Name,
    string Job,
    int Level,
    int ItemLevel = 0,
    int Gathering = 0)
{
    /// <summary>True when the stats that decide a tier are known.</summary>
    public bool StatsKnown => ItemLevel > 0 || Gathering > 0;
}

/// <summary>One venture ranked for one retainer: what it would return, what that is worth, and why not.</summary>
/// <param name="Blocker">Null when the retainer can run it; otherwise the reason it cannot, in operator words.</param>
/// <param name="TierKnown">False when the quantity is an assumption because the retainer's stats are unknown.</param>
public sealed record VentureOption(
    VentureDef Venture,
    int TierIndex,
    bool TierKnown,
    int QuantityPerRun,
    long PriceEach,
    long ValuePerRun,
    long ValuePerHour,
    long ExpPerHour,
    string? Blocker)
{
    /// <summary>Can this retainer actually be sent on it?</summary>
    public bool Runnable => Blocker == null;
}

/// <summary>
/// Choosing a venture: what a retainer qualifies for, what quantity it would land, and what that is
/// worth per hour. Pure — the ranking is the whole point of the feature and it is easier to test than
/// to eyeball in game.
///
/// THE RULE THAT MATTERS: rank by market value per hour, never by venture level or by the sheet's own
/// experience number. On Maduin a level-84 hunt (Eblan Alumen, 500 gil) pays several times what the
/// level-86 hunt (Manganese Ore, 114 gil) does for the same hour and the same venture token, so a
/// level-ordered pick is simply wrong money. Experience is the tiebreak, for a retainer still levelling.
/// </summary>
public static class VentureCatalog
{
    /// <summary>Quantity tiers are decided by gear; with no gear reading we assume the middle step.</summary>
    public const bool AssumeMiddleTierWhenUnknown = true;

    /// <summary>Why this retainer cannot run this venture, or null when it can.</summary>
    public static string? Blocker(RetainerProfile retainer, VentureDef venture)
    {
        if (retainer.Level < venture.RetainerLevel)
            return $"needs level {venture.RetainerLevel}";

        if (!JobFits(retainer.Job, venture.JobCategory))
            return $"needs {venture.JobCategory}";

        // Requirement fields are only a blocker when we actually know the retainer's number: an unknown
        // stat must not read as "fails", or every retainer with unread gear would come back blocked.
        if (venture.RequiredItemLevel > 0 && retainer.ItemLevel > 0 && retainer.ItemLevel < venture.RequiredItemLevel)
            return $"needs ilvl {venture.RequiredItemLevel}";

        if (venture.RequiredGathering > 0 && retainer.Gathering > 0 && retainer.Gathering < venture.RequiredGathering)
            return $"needs gathering {venture.RequiredGathering}";

        return null;
    }

    /// <summary>
    /// Does this retainer's job satisfy the venture's <c>ClassJobCategory</c>? The sheet lists the jobs a
    /// venture accepts, so a token match is the whole test; an empty category accepts anyone.
    /// </summary>
    public static bool JobFits(string retainerJob, string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return true;

        if (string.IsNullOrWhiteSpace(retainerJob))
            return false;

        foreach (var token in category.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals(retainerJob, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Which step of the quantity ladder this retainer lands on, and whether that is known. -1 means the
    /// ladder is unreadable; the tier index is clamped to the shortest of the two ladders.
    /// </summary>
    public static (int Index, bool Known) Tier(RetainerProfile retainer, VentureDef venture)
    {
        if (venture.Quantities.Count == 0)
            return (0, false);

        var stat = venture.UsesItemLevel ? retainer.ItemLevel : retainer.Gathering;
        var known = stat > 0 && venture.Thresholds.Count > 0;

        if (!known)
            return (AssumeMiddleTierWhenUnknown ? venture.Quantities.Count / 2 : 0, false);

        var index = 0;
        for (var i = 0; i < venture.Thresholds.Count && i < venture.Quantities.Count; i++)
        {
            if (stat >= venture.Thresholds[i])
                index = i;
        }

        return (index, true);
    }

    /// <summary>Everything this retainer could be sent on, best first. Blocked ventures are included, with the reason.</summary>
    public static List<VentureOption> Rank(
        RetainerProfile retainer,
        IEnumerable<VentureDef> ventures,
        IReadOnlyDictionary<uint, long> prices)
    {
        var options = new List<VentureOption>();
        foreach (var venture in ventures)
        {
            var (tier, known) = Tier(retainer, venture);
            var quantity = venture.Quantities.Count == 0 ? 0 : venture.Quantities[Math.Clamp(tier, 0, venture.Quantities.Count - 1)];
            var price = venture.ItemId != 0 && prices.TryGetValue(venture.ItemId, out var p) ? p : 0L;
            var valueRun = quantity * price;
            var valueHour = venture.MaxMinutes <= 0 ? valueRun : valueRun * 60 / venture.MaxMinutes;

            options.Add(new VentureOption(
                venture,
                tier,
                known,
                quantity,
                price,
                valueRun,
                valueHour,
                venture.ExpPerHour,
                Blocker(retainer, venture)));
        }

        // Value per hour first; experience breaks ties (and carries the whole order when no prices are
        // known yet, which is what "prices off" has to degrade to).
        options.Sort((a, b) =>
        {
            var byValue = b.ValuePerHour.CompareTo(a.ValuePerHour);
            if (byValue != 0)
                return byValue;

            var byExp = b.ExpPerHour.CompareTo(a.ExpPerHour);
            return byExp != 0 ? byExp : a.Venture.TaskId.CompareTo(b.Venture.TaskId);
        });

        return options;
    }

    /// <summary>
    /// The venture to run automatically: the best-paying one the retainer can actually complete.
    /// Random-pool explorations are never auto-picked — their yield is not in the sheet, so "best" would
    /// be a guess dressed up as a decision.
    /// </summary>
    public static VentureOption? Best(
        RetainerProfile retainer,
        IEnumerable<VentureDef> ventures,
        IReadOnlyDictionary<uint, long> prices)
    {
        foreach (var option in Rank(retainer, ventures, prices))
        {
            if (!option.Runnable || option.Venture.IsRandom)
                continue;

            return option;
        }

        return null;
    }

    /// <summary>A one-line explanation of a ranked option, for the window's status line.</summary>
    public static string Describe(VentureOption option)
    {
        var quantity = option.QuantityPerRun > 0 ? $"x{option.QuantityPerRun.ToString(CultureInfo.InvariantCulture)}" : "—";
        var worth = option.ValuePerHour > 0 ? $"{option.ValuePerHour:N0} gil/h" : $"{option.ExpPerHour:N0} exp/h";
        var tier = option.TierKnown ? string.Empty : " (tier assumed — gear not read)";
        return $"{option.Venture.Name} — {quantity} every {option.Venture.MaxMinutes}m, {worth}{tier}";
    }
}
