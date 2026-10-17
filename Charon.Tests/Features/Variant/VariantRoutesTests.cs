using System.Linq;
using Charon.Features.Variant;

namespace Charon.Tests.Features.Variant;

public sealed class VariantRoutesTests
{
    [Fact]
    public void NotebookRows_MatchTheGamesSeriesSheet()
    {
        // VVDNotebookSeries, read from the game files: 1-12, 13-24, 25-36, 37-49.
        Assert.Equal((1u, 12u), Span(VariantRoutes.Sildihn));
        Assert.Equal((13u, 24u), Span(VariantRoutes.Rokkon));
        Assert.Equal((25u, 36u), Span(VariantRoutes.Aloalo));
        Assert.Equal((37u, 49u), Span(VariantRoutes.MerchantsTale));

        static (uint, uint) Span(VariantDungeon d) =>
            (d.NotebookRow(d.Routes.Min(r => r.Number)), d.NotebookRow(d.Routes.Max(r => r.Number)));
    }

    [Fact]
    public void EveryDungeon_NumbersItsRoutesWithoutGaps_AndHasOneSecret()
    {
        foreach (var dungeon in VariantRoutes.All)
        {
            var numbers = dungeon.Routes.Select(r => r.Number).ToList();
            Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
            Assert.Single(dungeon.Routes, r => r.Secret);
            Assert.All(dungeon.Routes, r => Assert.NotEmpty(r.Steps));
        }
    }

    [Fact]
    public void EveryMarker_PointsAtARouteThatExists()
    {
        foreach (var dungeon in VariantRoutes.All)
        foreach (var marker in dungeon.Markers)
        {
            Assert.NotEmpty(marker.Routes);
            Assert.All(marker.Routes, n => Assert.NotNull(dungeon.Route(n)));
        }
    }

    [Fact]
    public void Territories_AreTheDutiesOwn()
    {
        // ContentFinderCondition 868 / 945 / 961 / 1066 -> TerritoryType, read from the game files.
        Assert.Same(VariantRoutes.Sildihn, VariantRoutes.ByTerritory(1069));
        Assert.Same(VariantRoutes.Rokkon, VariantRoutes.ByTerritory(1137));
        Assert.Same(VariantRoutes.Aloalo, VariantRoutes.ByTerritory(1176));
        Assert.Same(VariantRoutes.MerchantsTale, VariantRoutes.ByTerritory(1315));
        Assert.Null(VariantRoutes.ByTerritory(129));
    }

    [Fact]
    public void MarkersFor_OnlyReturnsThatRoutesMarkers()
    {
        var levers = VariantRoutes.Sildihn.MarkersFor(8).Where(m => m.Id == 2013143).ToList();
        Assert.Equal("pull the RIGHT lever", Assert.Single(levers).Label);
        Assert.DoesNotContain(VariantRoutes.Sildihn.MarkersFor(8), m => m.Id == 2013145);
    }
}

public sealed class VariantRecordTests
{
    [Fact]
    public void RowN_IsBitNMinusOne_LeastSignificantFirst()
    {
        byte[] bits = [0b0000_0001, 0, 0, 0, 0, 0, 0b0000_0001];
        Assert.True(VariantRecord.IsDone(bits, 1));
        Assert.False(VariantRecord.IsDone(bits, 2));
        Assert.True(VariantRecord.IsDone(bits, 49));
    }

    [Fact]
    public void RowZero_AndRowsPastTheArray_AreNeverDone()
    {
        byte[] bits = [0xFF];
        Assert.False(VariantRecord.IsDone(bits, 0));
        Assert.False(VariantRecord.IsDone(bits, 9));
    }

    [Fact]
    public void Progress_SplitsDoneFromLeft()
    {
        // Rokkon is rows 13-24: set rows 13 and 15 (bits 12 and 14, byte 1).
        byte[] bits = [0, 0b0101_0000, 0, 0, 0, 0, 0];
        var progress = VariantRecord.Progress(VariantRoutes.Rokkon, bits);

        Assert.Equal([1, 3], progress.Done);
        Assert.Equal(10, progress.Left.Count);
        Assert.False(progress.Complete);
    }

    [Fact]
    public void Suggest_TriesTheSecretRouteFirst_BecauseAFailedAttemptStillEarnsASibling()
    {
        var progress = VariantRecord.Progress(VariantRoutes.Aloalo, new byte[7]);
        Assert.Equal(12, VariantRecord.Suggest(progress));
    }

    [Fact]
    public void Suggest_ThenTakesTheLowestRouteLeft()
    {
        // Aloalo rows 25-36; mark route 12 (row 36, bit 35 = byte 4 bit 3) and route 1 (row 25, bit 24 = byte 3 bit 0).
        byte[] bits = [0, 0, 0, 0b0000_0001, 0b0000_1000, 0, 0];
        var progress = VariantRecord.Progress(VariantRoutes.Aloalo, bits);
        Assert.Equal(2, VariantRecord.Suggest(progress));
    }

    [Fact]
    public void Suggest_IsNullOnceEveryRouteIsDone()
    {
        byte[] bits = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        var progress = VariantRecord.Progress(VariantRoutes.MerchantsTale, bits);
        Assert.True(progress.Complete);
        Assert.Null(VariantRecord.Suggest(progress));
    }
}
