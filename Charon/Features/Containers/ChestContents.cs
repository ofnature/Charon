using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Charon.Features.Retainers;

namespace Charon.Features.Containers;

/// <summary>One page of the free company chest as last SEEN. HQ is its own stack, never folded in.</summary>
public sealed record ChestPage(int Page, IReadOnlyList<RetainerStackCount> Stacks)
{
    public int Slots => Stacks.Count;

    public int Units => Stacks.Sum(s => s.Qty);

    public int HqStacks => Stacks.Count(s => s.Hq);
}

/// <summary>
/// The free company chest as a SNAPSHOT, with the time it was taken.
///
/// The client hands over a chest page's contents only once that chest has been opened on this machine, so this is
/// a question about LAST SEEN, never about now: a page nobody has looked at is UNKNOWN rather than empty, and
/// "the guild's chest holds no crystals" is a very different answer from "nobody has opened the chest here".
/// Empty pages that WERE read are empty and are recorded as such.
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
