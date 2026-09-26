using System;
using System.Linq;
using Charon.Features.Dailies;
using Charon.Features.GrandCompany;

namespace Charon.Tests.Features.GrandCompany;

/// <summary>
/// The request list as a snapshot: what the company wants, what is still short, and — the part that matters most
/// to anything downstream — the difference between "nobody has looked" and "nothing is wanted".
/// </summary>
public class GcRequestsTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private static GcRequestEntry Entry(uint id, int requested, GcMissionKind kind = GcMissionKind.Supply, string job = "CRP") =>
        new(id, $"item {id}", requested, kind, job);

    [Fact]
    public void ASnapshotKeepsTheRowsInTheOrderACrafterWouldWorkThem()
    {
        var snapshot = GcRequests.Build("123", Now, null,
        [
            Entry(2, 3, GcMissionKind.Provisioning, "BTN"),
            Entry(1, 1),
            Entry(3, 2, GcMissionKind.Provisioning, "FSH"),
        ]);

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Equal(6, snapshot.TotalUnits());
        Assert.Single(snapshot.SupplyRows());
        Assert.Equal(2, snapshot.ProvisioningRows().Count);
    }

    /// <summary>
    /// The same item can be asked for under supply and provisioning; the demand is the larger of the two, not
    /// whichever row happened to be read first.
    /// </summary>
    [Fact]
    public void ADuplicateItemKeepsTheLargerRequest()
    {
        var snapshot = GcRequests.Build("123", Now, null, [Entry(1, 1), Entry(1, 20, GcMissionKind.Provisioning, "BTN")]);

        Assert.Single(snapshot.Entries);
        Assert.Equal(20, snapshot.Entries[0].Requested);
    }

    [Fact]
    public void RowsWithNothingRequestedAreNotDemand()
    {
        var snapshot = GcRequests.Build("123", Now, null, [Entry(1, 0), Entry(0, 5), Entry(2, 1)]);

        Assert.Single(snapshot.Entries);
        Assert.Equal(2u, snapshot.Entries[0].ItemId);
    }

    /// <summary>
    /// A number the company could not have asked for is a bad read of the agent's rows (they are only valid once
    /// the window has finished setting up), so it is dropped instead of persisted as demand — which is also what
    /// overflowed a Total during a config save.
    /// </summary>
    [Fact]
    public void AnImpossibleRequestIsNotDemand()
    {
        var snapshot = GcRequests.Build("123", Now, null, [Entry(1, 1), Entry(2, int.MaxValue), Entry(3, -5)]);

        Assert.Single(snapshot.Entries);
        Assert.Equal(1u, snapshot.Entries[0].ItemId);
        Assert.Equal(1, snapshot.TotalUnits());
    }

    /// <summary>What still has to be made or gathered — the number the crafter side actually needs.</summary>
    [Fact]
    public void DemandIsWhatIsAskedForMinusWhatIsHeld()
    {
        var snapshot = GcRequests.Build("123", Now, null, [Entry(1, 20, GcMissionKind.Provisioning, "BTN"), Entry(2, 1)]);

        var demand = GcRequests.Demand(snapshot, id => id == 1 ? 8 : 5);

        Assert.Equal(12, demand.First(d => d.Entry.ItemId == 1).Shortfall);
        Assert.Equal(0, demand.First(d => d.Entry.ItemId == 2).Shortfall);
    }

    /// <summary>
    /// A snapshot is stale once the list rolls over, and also once it is old enough to be from a previous day on
    /// its own — a character who never opens the board still has an old snapshot sitting there.
    /// </summary>
    [Fact]
    public void ASnapshotKnowsWhenItIsNoLongerTodaysList()
    {
        var rolled = GcRequests.Build("123", Now, Now.AddHours(-1), [Entry(1, 1)]);
        var old = GcRequests.Build("123", Now.AddHours(-23), null, [Entry(1, 1)]);
        var fresh = GcRequests.Build("123", Now.AddMinutes(-5), Now.AddHours(3), [Entry(1, 1)]);

        Assert.True(GcRequests.IsStale(rolled, Now));
        Assert.True(GcRequests.IsStale(old, Now));
        Assert.False(GcRequests.IsStale(fresh, Now));
    }

    /// <summary>
    /// "Nobody has looked" is not "the company wants nothing". The payload says so explicitly, because a caller
    /// that reads an empty list as "nothing to do" would quietly skip a day's hand-ins.
    /// </summary>
    [Fact]
    public void NoSnapshotIsUnknownRatherThanAnEmptyList()
    {
        var json = GcRequests.ToJson(null, _ => 0, Now);

        Assert.Contains("\"known\":false", json);
        Assert.DoesNotContain("\"items\"", json);
        Assert.Contains("no request list has been captured yet", GcRequests.Describe(null, Now));
    }

    [Fact]
    public void ThePayloadCarriesWhatACrafterNeeds()
    {
        var snapshot = GcRequests.Build("123", Now, Now.AddHours(3),
            [Entry(36165, 20, GcMissionKind.Provisioning, "BTN")]);

        var json = GcRequests.ToJson(snapshot, _ => 8, Now);

        Assert.Contains("\"known\":true", json);
        Assert.Contains("\"itemId\":36165", json);
        Assert.Contains("\"requested\":20", json);
        Assert.Contains("\"shortfall\":12", json);
        Assert.Contains("\"kind\":\"Provisioning\"", json);
        Assert.Contains("\"job\":\"BTN\"", json);
        Assert.Contains("\"stale\":false", json);
    }

    /// <summary>
    /// A snapshot is persisted in the plugin config, so it has to survive being written and read back — and by
    /// NEWTONSOFT, which is what Dalamud's config save uses (System.Text.Json is only for the IPC payload).
    /// Testing the wrong serializer is how a throwing computed property reached a live config save.
    /// </summary>
    [Fact]
    public void ASnapshotSurvivesTheSerializersTheConfigActuallyUses()
    {
        var before = GcRequests.Build("123", Now, Now.AddHours(3),
        [
            Entry(36165, 20, GcMissionKind.Provisioning, "BTN"),
            Entry(5825, 1),
        ]);

        // Newtonsoft: the config path. It serializes public GETTERS, so any derived property on this type gets
        // evaluated mid-save — which is why there are none.
        var fromConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<GcRequestSnapshot>(
            Newtonsoft.Json.JsonConvert.SerializeObject(before));

        Assert.NotNull(fromConfig);
        Assert.Equal(before.Entries.Count, fromConfig!.Entries.Count);
        Assert.Equal(before.Entries[0].ItemId, fromConfig.Entries[0].ItemId);
        Assert.Equal(before.TotalUnits(), fromConfig.TotalUnits());

        var after = GcRequests.FromJson(System.Text.Json.JsonSerializer.Serialize(before));

        Assert.NotNull(after);
        Assert.Equal(before.Character, after!.Character);
        Assert.Equal(before.CapturedUtc, after.CapturedUtc);
        Assert.Equal(before.RolloverUtc, after.RolloverUtc);
        Assert.Equal(before.Entries.Count, after.Entries.Count);
        Assert.Equal(before.Entries[0].ItemId, after.Entries[0].ItemId);
        Assert.Equal(before.Entries[0].Kind, after.Entries[0].Kind);
        Assert.Equal(before.Entries[0].Job, after.Entries[0].Job);
    }

    /// <summary>
    /// The rollover stamp the window prints: "(9/26 15:00)" in "14:02 Remaining  (9/26 15:00)". No year is
    /// printed, so the current one is assumed — and a stamp that would be in the past is next year's, because a
    /// countdown does not end in the past.
    /// </summary>
    [Fact]
    public void TheRolloverStampIsReadFromTheWindowsOwnText()
    {
        var now = new DateTime(2026, 9, 26, 11, 0, 0, DateTimeKind.Local);

        var sameDay = Allowances.Rollover("14:02 Remaining  (9/26 15:00)", now);
        var nextYear = Allowances.Rollover("74:02 Remaining  (1/02 15:00)", now);

        Assert.Equal(new DateTime(2026, 9, 26, 15, 0, 0, DateTimeKind.Local), sameDay);
        Assert.Equal(2027, nextYear!.Value.Year);
        Assert.Null(Allowances.Rollover("Available Now", now));
        Assert.Null(Allowances.Rollover("(2/30 15:00)", now));
    }
}
