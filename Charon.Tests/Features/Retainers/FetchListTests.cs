using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class FetchListTests
{
    [Fact]
    public void ParsesTheShapeHephaestusSends()
    {
        var items = FetchList.Parse("""{"items":[{"itemId":5106,"quantity":12},{"itemId":5107,"quantity":3,"hq":true}]}""", out var refusal);

        Assert.Null(refusal);
        Assert.Equal(2, items.Count);
        Assert.Equal(new FetchRequest(5106, 12, false), items[0]);
        Assert.Equal(new FetchRequest(5107, 3, true), items[1]);
    }

    [Fact]
    public void HqDefaultsToFalse_MeaningAnyQuality()
    {
        var items = FetchList.Parse("""{"items":[{"itemId":5106,"quantity":1}]}""", out _);
        Assert.False(Assert.Single(items).HighQuality);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingGiven_IsRefused(string? json)
    {
        Assert.Empty(FetchList.Parse(json, out var refusal));
        Assert.Equal("no list given", refusal);
    }

    [Fact]
    public void MalformedJson_IsRefusedNotThrown()
    {
        Assert.Empty(FetchList.Parse("{not json", out var refusal));
        Assert.Contains("not valid JSON", refusal);
    }

    [Fact]
    public void MissingItemsArray_IsRefused()
    {
        Assert.Empty(FetchList.Parse("""{"stuff":[]}""", out var refusal));
        Assert.Contains("items", refusal);
    }

    [Fact]
    public void BadEntriesAreDropped_ButTheGoodOnesStillRun()
    {
        var items = FetchList.Parse(
            """{"items":[{"itemId":0,"quantity":5},{"itemId":5106,"quantity":0},{"itemId":5106,"quantity":7},"nonsense"]}""",
            out var refusal);

        Assert.Null(refusal);
        Assert.Equal(new FetchRequest(5106, 7, false), Assert.Single(items));
    }

    [Fact]
    public void AListThatAsksForNothing_IsRefused()
    {
        // Answering "fine" to a request that will never do anything is how a caller waits forever.
        Assert.Empty(FetchList.Parse("""{"items":[{"itemId":5106,"quantity":0}]}""", out var refusal));
        Assert.Equal("the list asked for nothing", refusal);
    }

    [Fact]
    public void AnAbsurdlyLongList_IsCappedAndSaysSo()
    {
        var entries = string.Join(",", Enumerable.Range(1, 80).Select(i => $$"""{"itemId":{{i + 1000}},"quantity":1}"""));
        var items = FetchList.Parse($$"""{"items":[{{entries}}]}""", out var refusal);

        Assert.Equal(FetchList.MaxItems, items.Count);
        Assert.Contains("more than", refusal);
    }
}
