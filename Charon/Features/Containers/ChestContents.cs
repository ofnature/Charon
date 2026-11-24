using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Charon.Features.Retainers;

namespace Charon.Features.Containers;

/// <summary>
/// One page of the free company chest as last SEEN, and when. HQ is its own stack, never folded in. SeenUtc is per
/// page because pages are read at different times (a tab nobody clicked keeps what an earlier visit saw).
/// </summary>
public sealed record ChestPage(int Page, IReadOnlyList<RetainerStackCount> Stacks, DateTime SeenUtc = default)
{
    public int Slots => Stacks.Count;

    public int Units => Stacks.Sum(s => s.Qty);

    public int HqStacks => Stacks.Count(s => s.Hq);
}

/// <summary>
/// The free company chest as a SNAPSHOT. CapturedUtc is the time its OLDEST page was seen (see
/// <see cref="ChestContents.OldestSeen"/>), so a snapshot is never reported fresher than any page in it.
///
/// The client hands over a chest page's contents only once that chest has been opened on this machine, so this is
/// a question about LAST SEEN, never about now: a page nobody has looked at is UNKNOWN rather than empty, and
/// "the guild's chest holds no crystals" is a very different answer from "nobody has opened the chest here".
/// Only pages read WITH stacks are recorded: the client shows a page as empty until its data arrives, so an empty
/// read is not evidence of an empty page (see <see cref="ChestContents.Merge"/>).
/// </summary>
public sealed record ChestSnapshot(DateTime CapturedUtc, IReadOnlyList<ChestPage> Pages)
{
    public int Slots => Pages.Sum(p => p.Slots);

    public int Units => Pages.Sum(p => p.Units);
}

/// <summary>
/// The chest store's logic. Pure — a caller hands in the pages it read, so every rule here is tested without a
/// client, including the one that matters to anything downstream: unknown is not empty.
/// </summary>
public static class ChestContents
{
    /// <summary>Pages the chest has. Five in the client; a sixth would be a patch, not a guess.</summary>
    public const int PageCount = 5;

    /// <summary>Per page: how many of this item that page holds, HQ counted separately.</summary>
    public static IReadOnlyList<(int Page, int Nq, int Hq)> Find(ChestSnapshot? snapshot, uint itemId)
    {
        if (snapshot == null || itemId == 0)
            return [];

        return snapshot.Pages
            .Select(p => (
                p.Page,
                p.Stacks.Where(s => s.ItemId == itemId && !s.Hq).Sum(s => s.Qty),
                p.Stacks.Where(s => s.ItemId == itemId && s.Hq).Sum(s => s.Qty)))
            .Where(x => x.Item2 > 0 || x.Item3 > 0)
            .ToList();
    }

    /// <summary>
    /// Folds a fresh read of the chest into the stored pages. A chest page reads EMPTY until its data arrives —
    /// the container already says it is loaded — and a tab nobody has clicked stays empty for as long as the chest
    /// is open, so an empty read cannot be told apart from "not here yet". Hence, per page: a read WITH stacks
    /// replaces that page; an empty read leaves the stored page alone and never creates one. A stored page with no
    /// stacks is dropped, since it can only have come from such a read (older builds saved them), so it is unknown,
    /// not empty. Changed says whether the result differs from what was stored, which is what decides a save.
    /// The cost: a page the player empties completely keeps its last contents until a read finds something there.
    /// </summary>
    public static (IReadOnlyList<ChestPage> Pages, bool Changed) Merge(
        IReadOnlyList<ChestPage>? stored,
        IReadOnlyList<ChestPage> read)
    {
        var merged = new SortedDictionary<int, ChestPage>();

        foreach (var page in stored ?? [])
        {
            if (page.Stacks.Count > 0)
                merged[page.Page] = page;
        }

        foreach (var page in read)
        {
            if (page.Stacks.Count > 0)
                merged[page.Page] = page;
        }

        var pages = merged.Values.ToList();
        return (pages, !SamePages(stored ?? [], pages));
    }

    /// <summary>
    /// When the oldest of these pages was seen: the honest age of a snapshot whose pages were read at different
    /// times. A page without its own time (saved by a build that kept one time for the whole chest) counts as
    /// <paramref name="fallback"/>. No pages = the fallback.
    /// </summary>
    public static DateTime OldestSeen(IEnumerable<ChestPage> pages, DateTime fallback)
    {
        var oldest = DateTime.MaxValue;
        foreach (var page in pages)
        {
            var seen = page.SeenUtc == default ? fallback : page.SeenUtc;
            if (seen < oldest)
                oldest = seen;
        }

        return oldest == DateTime.MaxValue ? fallback : oldest;
    }

    /// <summary>Same pages holding the same stacks. Slot order is ignored: sorting the chest changes nothing.</summary>
    private static bool SamePages(IReadOnlyList<ChestPage> a, IReadOnlyList<ChestPage> b)
    {
        if (a.Count != b.Count)
            return false;

        var left = a.OrderBy(p => p.Page).ToList();
        var right = b.OrderBy(p => p.Page).ToList();

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].Page != right[i].Page || !SameStacks(left[i].Stacks, right[i].Stacks))
                return false;
        }

        return true;
    }

    private static bool SameStacks(IReadOnlyList<RetainerStackCount> a, IReadOnlyList<RetainerStackCount> b) =>
        a.Count == b.Count
        && a.OrderBy(s => s.ItemId).ThenBy(s => s.Hq).ThenBy(s => s.Qty)
            .SequenceEqual(b.OrderBy(s => s.ItemId).ThenBy(s => s.Hq).ThenBy(s => s.Qty));

    /// <summary>Everything the chest holds of this item, HQ counted separately.</summary>
    public static (int Nq, int Hq) Total(ChestSnapshot? snapshot, uint itemId)
    {
        var found = Find(snapshot, itemId);
        return (found.Sum(f => f.Nq), found.Sum(f => f.Hq));
    }

    /// <summary>
    /// One line for the UI: how much is known and how old it is — or that nothing is known, which is a different
    /// statement and is worded as one.
    /// </summary>
    public static string Describe(ChestSnapshot? snapshot, DateTime nowUtc)
    {
        if (snapshot == null)
            return "no chest contents have been captured yet — open the free company chest once and Charon records it";

        var age = nowUtc - snapshot.CapturedUtc;
        var when = age.TotalMinutes switch
        {
            < 1 => "just now",
            < 90 => $"{age.TotalMinutes:0} min ago",
            _ => $"{age.TotalHours:0} h ago",
        };

        return $"{snapshot.Pages.Count} page(s), {snapshot.Slots} stack(s), {snapshot.Units} item(s), captured {when}";
    }

    /// <summary>
    /// The IPC payload. EXTEND-ONLY: fields are added, never renamed or removed, because a caller that cannot read
    /// this simply has no chest materials and must not break when the shape grows.
    /// </summary>
    public static string ToJson(ChestSnapshot? snapshot, DateTime nowUtc)
    {
        if (snapshot == null)
        {
            // Explicitly unknown, not empty: "nobody has opened the chest" and "the chest holds nothing" are
            // different answers, and only one of them means there is nothing to draw from.
            return JsonSerializer.Serialize(new
            {
                known = false,
                note = "no free company chest has been captured on this machine yet",
            });
        }

        return JsonSerializer.Serialize(new
        {
            known = true,
            capturedUtc = snapshot.CapturedUtc.ToString("O"),
            ageMinutes = Math.Round((nowUtc - snapshot.CapturedUtc).TotalMinutes, 1),
            pageCount = snapshot.Pages.Count,
            expectedPageCount = PageCount,
            slots = snapshot.Slots,
            units = snapshot.Units,
            pages = snapshot.Pages
                .OrderBy(p => p.Page)
                .Select(p => new
                {
                    page = p.Page,
                    seenUtc = (p.SeenUtc == default ? snapshot.CapturedUtc : p.SeenUtc).ToString("O"),
                    slots = p.Slots,
                    units = p.Units,
                    items = p.Stacks
                        .OrderBy(s => s.ItemId)
                        .Select(s => new { itemId = s.ItemId, qty = s.Qty, hq = s.Hq })
                        .ToList(),
                })
                .ToList(),
        });
    }
}
