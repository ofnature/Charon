using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Retainers;

/// <summary>One stack as seen in a retainer's bags. HQ is its own stack, never folded into the count.</summary>
public sealed record RetainerStackCount(uint ItemId, int Qty, bool Hq);

/// <summary>One retainer's bags as last SEEN, with the time they were seen.</summary>
public sealed record RetainerBag(string Key, string Name, DateTime CapturedUtc, IReadOnlyList<RetainerStackCount> Stacks);

/// <summary>Where an item is, per retainer, with HQ counted separately — the shape callers ask for.</summary>
public sealed record ItemHolding(string Retainer, int Nq, int Hq, DateTime CapturedUtc)
{
    public int Total => Nq + Hq;
}

/// <summary>
/// What a fetch of (item, quantity, quality) would actually do, or why it cannot be done.
/// </summary>
/// <param name="Retainer">The retainer to take from — the one holding the most of the wanted quality.</param>
/// <param name="Nq">How many normal-quality units to take.</param>
/// <param name="Hq">How many high-quality units to take.</param>
/// <param name="Refusal">Null when the fetch is possible; otherwise the reason, in operator words.</param>
public sealed record FetchPlan(string? Retainer, int Nq, int Hq, string? Refusal)
{
    public bool Possible => Refusal == null;

    public int Total => Nq + Hq;
}

/// <summary>
/// Reading a store of retainer contents that is only ever as fresh as the last time that retainer was
/// opened.
///
/// The client has no retainer inventory until that retainer's window has been opened at a bell, so this is
/// a question about LAST SEEN, never about NOW: every answer carries the timestamp it rests on, a retainer
/// nobody has opened reads UNKNOWN rather than empty, and a fetch is planned against what the store says
/// while being honest that the bags may have moved since. (An unopened retainer and an empty one are very
/// different answers, and only one of them should make a caller give up.)
///
/// Pure: the caller hands in the snapshots it has, so the arithmetic is testable without a client.
/// </summary>
public static class RetainerContents
{
    public const int DefaultStaleAfterMinutes = 60;

    /// <summary>Per retainer: how many of this item it holds, HQ counted separately.</summary>
    public static IReadOnlyList<ItemHolding> Find(IEnumerable<RetainerBag> bags, uint itemId)
    {
        var holdings = new List<ItemHolding>();
        foreach (var bag in bags)
        {
            var nq = 0;
            var hq = 0;
            foreach (var stack in bag.Stacks.Where(s => s.ItemId == itemId))
            {
                if (stack.Hq)
                    hq += stack.Qty;
                else
                    nq += stack.Qty;
            }

            // Reported even at zero: "T'sala has none" and "nobody has ever opened T'sala" are different
            // answers, and the caller needs to be able to tell them apart.
            holdings.Add(new ItemHolding(bag.Name, nq, hq, bag.CapturedUtc));
        }

        return holdings
            .OrderByDescending(h => h.Total)
            .ThenBy(h => h.Retainer, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Totals across every known retainer, HQ separate.</summary>
    public static (int Nq, int Hq) Total(IEnumerable<RetainerBag> bags, uint itemId)
    {
        var nq = 0;
        var hq = 0;
        foreach (var bag in bags)
        {
            foreach (var stack in bag.Stacks.Where(s => s.ItemId == itemId))
            {
                if (stack.Hq)
                    hq += stack.Qty;
                else
                    nq += stack.Qty;
            }
        }

        return (nq, hq);
    }

    /// <summary>
    /// Which retainer a fetch should come from, and how much of each quality — NQ is filled first, then HQ,
    /// so a caller wanting 100 units is not refused because they happen to be split across qualities.
    /// </summary>
    public static FetchPlan PlanFetch(IReadOnlyList<RetainerBag> bags, uint itemId, int quantity, bool highQuality)
    {
        if (quantity <= 0)
            return new FetchPlan(null, 0, 0, "nothing was asked for");

        if (bags.Count == 0)
            return new FetchPlan(null, 0, 0, "no retainer contents are known yet — open a retainer at a bell");

        var wanted = highQuality
            ? bags
                .Select(b => (Bag: b, Held: Count(b, itemId, hq: true)))
                .Where(x => x.Held > 0)
                .OrderByDescending(x => x.Held)
                .ThenBy(x => x.Bag.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [.. bags
                .Select(b => (Bag: b, Held: Count(b, itemId, hq: false) + Count(b, itemId, hq: true)))
                .Where(x => x.Held > 0)
                .OrderByDescending(x => x.Held)
                .ThenBy(x => x.Bag.Name, StringComparer.OrdinalIgnoreCase)];

        if (wanted.Count == 0)
        {
            return new FetchPlan(null, 0, 0, highQuality
                ? "no known retainer holds a high-quality stack of that"
                : "no known retainer holds that item");
        }

        var best = wanted[0];
        if (best.Held < quantity)
        {
            return new FetchPlan(null, 0, 0,
                $"{best.Bag.Name} holds {best.Held} of the {quantity} wanted — the store is short"
                + " (and this is the last time it was seen, not live)");
        }

        if (highQuality)
            return new FetchPlan(best.Bag.Name, 0, quantity, null);

        var fromNq = Math.Min(quantity, Count(best.Bag, itemId, hq: false));
        return new FetchPlan(best.Bag.Name, fromNq, quantity - fromNq, null);
    }

    /// <summary>
    /// Which retainers a refresh pass still needs, oldest first, with the reason the pass should give.
    /// A retainer nobody has opened sorts first: it is the one whose absence makes an answer "unknown".
    /// </summary>
    public static IReadOnlyList<(string Key, string Name, string Reason)> PlanRefresh(
        IReadOnlyList<RetainerBag> known,
        IReadOnlyList<(string Key, string Name)> all,
        DateTime nowUtc,
        int staleAfterMinutes = DefaultStaleAfterMinutes)
    {
        var byKey = known.ToDictionary(b => b.Key, b => b, StringComparer.OrdinalIgnoreCase);
        var plan = new List<(string Key, string Name, string Reason, DateTime Sort)>();

        foreach (var (key, name) in all)
        {
            if (!byKey.TryGetValue(key, out var bag))
            {
                plan.Add((key, name, "never opened", DateTime.MinValue));
                continue;
            }

            var age = nowUtc - bag.CapturedUtc;
            if (age.TotalMinutes >= staleAfterMinutes)
                plan.Add((key, name, $"last seen {Describe(age)} ago", bag.CapturedUtc));
        }

        return plan
            .OrderBy(x => x.Sort)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Key, x.Name, x.Reason))
            .ToList();
    }

    /// <summary>How much of the store is current, for a status line and for a caller deciding to trust it.</summary>
    public static (int Fresh, int Stale, int Unknown) Audit(
        IReadOnlyList<RetainerBag> known,
        IReadOnlyList<(string Key, string Name)> all,
        DateTime nowUtc,
        int staleAfterMinutes = DefaultStaleAfterMinutes)
    {
        var staleKeys = PlanRefresh(known, all, nowUtc, staleAfterMinutes).Select(p => p.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fresh = known.Count(b => !staleKeys.Contains(b.Key));
        var unknown = all.Count(r => !known.Any(b => string.Equals(b.Key, r.Key, StringComparison.OrdinalIgnoreCase)));
        return (fresh, staleKeys.Count - unknown, unknown);
    }

    private static int Count(RetainerBag bag, uint itemId, bool hq) =>
        bag.Stacks.Where(s => s.ItemId == itemId && s.Hq == hq).Sum(s => s.Qty);

    private static string Describe(TimeSpan age) => age.TotalMinutes switch
    {
        < 90 => $"{age.TotalMinutes:0} min",
        < 60 * 36 => $"{age.TotalHours:0} h",
        _ => $"{age.TotalDays:0} days",
    };
}
