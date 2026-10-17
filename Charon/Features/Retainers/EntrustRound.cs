using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Retainers;

/// <summary>Who an entrust round would visit, and why.</summary>
/// <param name="Matches">Items in the player's bags this retainer already holds; -1 when her contents were never read.</param>
public sealed record EntrustVisit(string Name, int Matches);

/// <summary>
/// "Entrust duplicates to every retainer": who is worth opening. Pure — no Dalamud types.
///
/// The moving is the GAME'S OWN Entrust Duplicates (her bags' button, then its confirm list), which
/// only ever entrusts items she already holds a stack of — exactly the rule wanted, decided by the game.
/// This only chooses the visits: a retainer whose last snapshot shares an item with the bags, and a
/// retainer whose contents were never read (she might, and opening her also fills the snapshot). One
/// whose snapshot shares nothing is skipped — a summon that could only end in a greyed-out button.
///
/// Like a venture round, the next retainer is asked of live state each time, never a list decided up
/// front; a retainer is visited at most once per round.
/// </summary>
public static class EntrustRound
{
    /// <param name="retainers">This character's retainers, in list order.</param>
    /// <param name="snapshots">Item ids each retainer held when last read; a missing name means never read.</param>
    /// <param name="bagItems">Item ids in the player's bags right now.</param>
    public static IReadOnlyList<EntrustVisit> Plan(
        IReadOnlyList<string> retainers,
        IReadOnlyDictionary<string, IReadOnlyCollection<uint>> snapshots,
        IReadOnlyCollection<uint> bagItems)
    {
        var bags = bagItems as ISet<uint> ?? bagItems.ToHashSet();
        var visits = new List<EntrustVisit>();
        foreach (var name in retainers)
        {
            if (!snapshots.TryGetValue(name, out var held))
            {
                visits.Add(new EntrustVisit(name, -1));
                continue;
            }

            var matches = held.Distinct().Count(bags.Contains);
            if (matches > 0)
                visits.Add(new EntrustVisit(name, matches));
        }

        return visits;
    }

    /// <summary>The next retainer to open: the first planned visit not yet made this round, or null when done.</summary>
    public static string? Next(IReadOnlyList<EntrustVisit> plan, IReadOnlyCollection<string> visited)
    {
        foreach (var visit in plan)
        {
            if (!visited.Contains(visit.Name))
                return visit.Name;
        }

        return null;
    }
}
