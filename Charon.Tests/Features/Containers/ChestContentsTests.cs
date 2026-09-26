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
