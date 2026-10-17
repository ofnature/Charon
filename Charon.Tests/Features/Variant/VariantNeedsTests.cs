using System;
using System.Linq;
using Charon.Features.Variant;
using Charon.Ipc;

namespace Charon.Tests.Features.Variant;

public sealed class VariantNeedsTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A record with the given Aloalo routes done (rows 25-36 = bits 24-35).</summary>
    private static ToonRecord Aloalo(string name, params int[] done)
    {
        var bits = new byte[7];
        foreach (var route in done)
        {
            var bit = (int)VariantRoutes.Aloalo.NotebookRow(route) - 1;
            bits[bit >> 3] |= (byte)(1 << (bit & 7));
        }

        return new ToonRecord(name, bits, T0, Local: false);
    }

    [Fact]
    public void NeededBy_ListsOnlyTheToonsWithoutTheRoute()
    {
        var group = new[] { Aloalo("Korha", 1, 2), Aloalo("Saar", 2), Aloalo("Grakur") };
        Assert.Equal(["Saar", "Grakur"], VariantNeeds.NeededBy(VariantRoutes.Aloalo, 1, group));
        Assert.Equal(["Grakur"], VariantNeeds.NeededBy(VariantRoutes.Aloalo, 2, group));
    }

    [Fact]
    public void GroupSuggest_PicksTheRouteMostOfTheGroupStillNeeds()
    {
        // Everyone has 1 and the secret 12; route 2 is needed by two, route 3 by two, route 4 onwards by all three.
        var group = new[] { Aloalo("Korha", 1, 2, 12), Aloalo("Saar", 1, 12, 3), Aloalo("Grakur", 1, 12) };
        Assert.Equal(4, VariantNeeds.GroupSuggest(VariantRoutes.Aloalo, group));
    }

    [Fact]
    public void GroupSuggest_BreaksATieOnTheSecretRoute()
    {
        var group = new[] { Aloalo("Korha"), Aloalo("Saar") };
        Assert.Equal(12, VariantNeeds.GroupSuggest(VariantRoutes.Aloalo, group));
    }

    [Fact]
    public void GroupSuggest_IsNullWhenNobodyNeedsAnything()
    {
        var all = Enumerable.Range(1, 12).ToArray();
        Assert.Null(VariantNeeds.GroupSuggest(VariantRoutes.Aloalo, [Aloalo("Korha", all)]));
        Assert.Null(VariantNeeds.GroupSuggest(VariantRoutes.Aloalo, []));
    }

    [Fact]
    public void ByToon_GroupsWhatIsLeftByTheFork()
    {
        // Aloalo: left 1-4, middle 5-8, right 9-12.
        var needs = Assert.Single(VariantNeeds.ByToon(VariantRoutes.Aloalo, [Aloalo("Korha", 1, 2, 3, 4, 5, 9, 12)]));
        Assert.Empty(needs.ByPath[VariantPath.Left]);
        Assert.Equal([6, 7, 8], needs.ByPath[VariantPath.Middle]);
        Assert.Equal([10, 11], needs.ByPath[VariantPath.Right]);
        Assert.Equal(5, needs.Total);
    }

    [Fact]
    public void Store_ReportsAChangeOnlyWhenTheRecordDiffers_AndMatchesNamesCaseInsensitively()
    {
        var store = new FleetVariantRecords();
        Assert.True(store.Upsert("Korha", [1, 0], T0));
        Assert.False(store.Upsert("korha", [1, 0], T0.AddMinutes(1)));
        Assert.True(store.Upsert("Korha", [3, 0], T0.AddMinutes(2)));
        Assert.Equal(1, store.Count);
        Assert.Equal(T0.AddMinutes(2), store.Get("KORHA")!.SeenUtc);
        Assert.False(store.Upsert("", [1], T0));
    }
}

public sealed class VariantRelayTests
{
    [Fact]
    public void State_RoundTripsTheBits()
    {
        byte[] bits = [0x01, 0xF0, 0, 0, 0, 0, 0x01];
        var message = VariantRelay.Parse(VariantRelay.SerializeState("Korha", bits));

        Assert.NotNull(message);
        Assert.Equal(VariantRelay.ActState, message.Act);
        Assert.Equal("Korha", message.Name);
        Assert.Equal(bits, VariantRelay.DecodeBits(message.Bits));
    }

    [Fact]
    public void Ask_Parses()
    {
        var message = VariantRelay.Parse(VariantRelay.SerializeAsk("Saar"));
        Assert.Equal(VariantRelay.ActAsk, message!.Act);
        Assert.Equal("Saar", message.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"act\":\"state\",\"name\":\"\",\"bits\":\"01\"}")]
    [InlineData("{\"act\":\"state\",\"name\":\"Korha\",\"bits\":\"zz\"}")]
    [InlineData("{\"act\":\"state\",\"name\":\"Korha\",\"bits\":\"0\"}")]
    [InlineData("{\"act\":\"state\",\"name\":\"Korha\",\"bits\":\"\"}")]
    [InlineData("{\"act\":\"gossip\",\"name\":\"Korha\"}")]
    public void JunkIsRefused(string json) => Assert.Null(VariantRelay.Parse(json));

    [Fact]
    public void ImplausiblyLongBitsAreRefused() =>
        Assert.Null(VariantRelay.DecodeBits(new string('0', (VariantRelay.MaxBytes + 1) * 2)));
}
