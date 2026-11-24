using System;
using System.Linq;
using Charon.Features.Containers;
using Charon.Features.Retainers;

namespace Charon.Tests.Features.Containers;

/// <summary>
/// The free company chest store. The rules here are the ones a consumer depends on: an item is found across pages
/// with HQ counted separately, and "nobody has opened the chest" is never answered as "the chest holds nothing".
/// </summary>
public class ChestContentsTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private static ChestSnapshot Snapshot(params ChestPage[] pages) => new(Now.AddMinutes(-5), pages);

    private static ChestPage Page(int page, params (uint ItemId, int Qty, bool Hq)[] stacks) =>
        new(page, stacks.Select(s => new RetainerStackCount(s.ItemId, s.Qty, s.Hq)).ToList());

    [Fact]
    public void APageKeptFromAnEarlierVisit_KeepsItsAge_AndTheSnapshotIsAsOldAsItsOldestPage()
    {
        var weekAgo = Now.AddDays(-7);
        var stored = new[]
        {
            new ChestPage(1, [new RetainerStackCount(36165, 20, false)], weekAgo),
            new ChestPage(3, [new RetainerStackCount(5825, 5, false)], weekAgo),
        };
        // This visit only page 1 has arrived.
        var read = new[]
        {
            new ChestPage(1, [new RetainerStackCount(36165, 18, false)], Now),
            new ChestPage(3, [], Now),
        };

        var (pages, changed) = ChestContents.Merge(stored, read);

        Assert.True(changed);
        Assert.Equal(Now, pages.Single(p => p.Page == 1).SeenUtc);
        Assert.Equal(weekAgo, pages.Single(p => p.Page == 3).SeenUtc);
        Assert.Equal(weekAgo, ChestContents.OldestSeen(pages, Now));
    }

    [Fact]
    public void APageWithoutItsOwnTime_CountsAsTheChestsTime()
    {
        var chestTime = Now.AddHours(-2);
        var pages = new[] { new ChestPage(1, [new RetainerStackCount(1, 1, false)]), new ChestPage(2, [], Now) };

        Assert.Equal(chestTime, ChestContents.OldestSeen(pages, chestTime));
        Assert.Equal(chestTime, ChestContents.OldestSeen([], chestTime));
    }

    [Fact]
    public void AnItemIsFoundAcrossPagesWithHqCountedSeparately()
    {
        var snapshot = Snapshot(
            Page(1, (36165, 20, false), (5825, 5, true)),
            Page(3, (36165, 12, false), (36165, 3, true)),
            Page(5, (9999, 1, false)));

        var total = ChestContents.Total(snapshot, 36165);
        var pages = ChestContents.Find(snapshot, 36165);

        Assert.Equal(32, total.Nq);
        Assert.Equal(3, total.Hq);
        Assert.Equal(2, pages.Count);
        Assert.Equal(1, pages[0].Page);
        Assert.Equal(3, pages[1].Page);
    }

    /// <summary>An empty page that WAS read is empty: a real answer, and not the same as an unread one.</summary>
    [Fact]
    public void AReadEmptyPageIsEmpty()
    {
        var snapshot = Snapshot(Page(1), Page(2, (36165, 4, false)));

        Assert.Equal(0, ChestContents.Total(snapshot, 1234).Nq);
        Assert.Empty(ChestContents.Find(snapshot, 1234));
        Assert.Equal(2, snapshot.Pages.Count);
        Assert.Equal(4, snapshot.Units);
    }

    /// <summary>
    /// The distinction the whole store exists for: unknown is not empty, and the payload says so explicitly so a
    /// caller cannot read "no chest materials" out of "nobody has looked".
    /// </summary>
    [Fact]
    public void NoSnapshotIsUnknownRatherThanAnEmptyChest()
    {
        var json = ChestContents.ToJson(null, Now);

        Assert.Contains("\"known\":false", json);
        Assert.DoesNotContain("\"pages\"", json);
        Assert.Contains("no chest contents have been captured yet", ChestContents.Describe(null, Now));
        Assert.Equal((0, 0), ChestContents.Total(null, 36165));
    }

    [Fact]
    public void ThePayloadCarriesWhatACrafterNeeds()
    {
        var snapshot = Snapshot(Page(1, (36165, 20, false), (36165, 3, true)), Page(2, (5825, 1, false)));

        var json = ChestContents.ToJson(snapshot, Now);

        Assert.Contains("\"known\":true", json);
        Assert.Contains("\"pageCount\":2", json);
        Assert.Contains("\"expectedPageCount\":5", json);
        Assert.Contains("\"slots\":3", json);
        Assert.Contains("\"units\":24", json);
        Assert.Contains("\"itemId\":36165", json);
        Assert.Contains("\"qty\":3,\"hq\":true", json);
        Assert.Contains("\"ageMinutes\":5", json);
    }

    /// <summary>
    /// The live bug (2026-11-03): the chest was read the instant it opened, every page came back empty, and that
    /// overwrote a full chest. An empty read must never replace a page that has stacks.
    /// </summary>
    [Fact]
    public void AnEmptyReadNeverWipesAStoredPage()
    {
        var stored = new[] { Page(1, (36165, 20, false)), Page(2, (5825, 5, true)) };
        var read = new[] { Page(1), Page(2), Page(3), Page(4), Page(5) };

        var (pages, changed) = ChestContents.Merge(stored, read);

        Assert.False(changed);
        Assert.Equal(2, pages.Count);
        Assert.Equal(20, pages[0].Units);
        Assert.Equal(5, pages[1].Units);
    }

    [Fact]
    public void AReadWithStacksReplacesThatPageOnly()
    {
        var stored = new[] { Page(1, (36165, 20, false)), Page(2, (5825, 5, true)) };
        var read = new[] { Page(1, (36165, 7, false)), Page(2) };

        var (pages, changed) = ChestContents.Merge(stored, read);

        Assert.True(changed);
        Assert.Equal(7, pages.Single(p => p.Page == 1).Units);
        Assert.Equal(5, pages.Single(p => p.Page == 2).Units);
    }

    /// <summary>Tabs fill one by one as their data arrives (or as they are clicked): each one joins the store.</summary>
    [Fact]
    public void PagesFillInAsTheyArrive()
    {
        var (first, firstChanged) = ChestContents.Merge(null, [Page(1, (36165, 20, false)), Page(2), Page(3)]);
        var (second, secondChanged) = ChestContents.Merge(first, [Page(1, (36165, 20, false)), Page(2), Page(3, (9999, 1, false))]);

        Assert.True(firstChanged);
        Assert.True(secondChanged);
        Assert.Equal([1, 3], second.Select(p => p.Page));
    }

    /// <summary>An empty read creates no page: unknown stays unknown, never "empty".</summary>
    [Fact]
    public void AnAllEmptyReadOfANewChestRecordsNothing()
    {
        var (pages, changed) = ChestContents.Merge(null, [Page(1), Page(2), Page(3), Page(4), Page(5)]);

        Assert.Empty(pages);
        Assert.False(changed);
    }

    /// <summary>Empty pages saved by older builds are the bug's output, so they are dropped as unknown.</summary>
    [Fact]
    public void StoredEmptyPagesAreDroppedAsUnknown()
    {
        var (pages, changed) = ChestContents.Merge([Page(1), Page(2, (5825, 1, false))], []);

        Assert.True(changed);
        Assert.Equal(2, pages.Single().Page);
    }

    /// <summary>Re-reading every couple of seconds must not save every couple of seconds: same stacks, no change.</summary>
    [Fact]
    public void TheSameContentsInAnotherSlotOrderAreNoChange()
    {
        var stored = new[] { Page(1, (36165, 20, false), (5825, 5, true)) };
        var read = new[] { Page(1, (5825, 5, true), (36165, 20, false)), Page(2) };

        Assert.False(ChestContents.Merge(stored, read).Changed);
    }

    /// <summary>
    /// The config shapes survive being written and read back by NEWTONSOFT, which is what Dalamud's config save
    /// uses. A store that cannot round-trip loses a chest silently on the next reload.
    /// </summary>
    [Fact]
    public void TheStoredChestSurvivesTheConfigSerializer()
    {
        var stored = new Charon.CharonConfig.ChestState
        {
            CapturedUtc = Now,
            Pages =
            [
                new Charon.CharonConfig.ChestPageState
                {
                    Page = 1,
                    Stacks = [new Charon.CharonConfig.RetainerStack { ItemId = 36165, Qty = 20, Hq = true }],
                },
            ],
        };

        var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Charon.CharonConfig.ChestState>(
            Newtonsoft.Json.JsonConvert.SerializeObject(stored));

        Assert.NotNull(back);
        Assert.Equal(Now, back!.CapturedUtc);
        Assert.Single(back.Pages);
        Assert.Equal(1, back.Pages[0].Page);
        Assert.Equal(36165u, back.Pages[0].Stacks[0].ItemId);
        Assert.Equal(20, back.Pages[0].Stacks[0].Qty);
        Assert.True(back.Pages[0].Stacks[0].Hq);
    }
}
