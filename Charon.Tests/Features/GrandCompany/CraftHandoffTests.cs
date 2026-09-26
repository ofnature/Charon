using System;
using System.Linq;
using System.Text.Json;
using Charon.Features.GrandCompany;

namespace Charon.Tests.Features.GrandCompany;

/// <summary>
/// The craft hand-off to Hephaestus. Its contract drives every rule here: entries are item ids (the board knows
/// those and not recipes), the request is ALL OR NOTHING, and it takes crafts only — a gathered item resolves to
/// no recipe and would reject the entire list.
/// </summary>
public class CraftHandoffTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private static GcRequestSnapshot Snapshot(params GcRequestEntry[] entries) =>
        GcRequests.Build("123", Now, Now.AddHours(3), entries);

    private static GcRequestEntry Craft(uint id, int requested, string job = "CRP") =>
        new(id, $"item {id}", requested, GcMissionKind.Supply, job);

    private static GcRequestEntry Gather(uint id, int requested, string job = "BTN") =>
        new(id, $"item {id}", requested, GcMissionKind.Provisioning, job);

    /// <summary>Quantities are the shortfall, because asking for what is already in the bag is busywork.</summary>
    [Fact]
    public void OnlyWhatIsMissingGoesOver()
    {
        var handoff = CraftHandoff.Build(Snapshot(Craft(1, 3), Craft(2, 20)), id => id == 2 ? 8 : 0);

        Assert.Equal(2, handoff.Crafts);
        Assert.Equal(15, handoff.Units);
        Assert.Contains("\"itemId\":1", handoff.Json);
        Assert.Contains("\"quantity\":3", handoff.Json);
        Assert.Contains("\"quantity\":12", handoff.Json);
    }

    [Fact]
    public void AnItemAlreadyCoveredIsLeftOut()
    {
        var handoff = CraftHandoff.Build(Snapshot(Craft(1, 3), Craft(2, 20)), id => id == 2 ? 20 : 0);

        Assert.Equal(1, handoff.Crafts);
        Assert.DoesNotContain("\"itemId\":2", handoff.Json);
        Assert.Contains(handoff.Excluded, line => line.Contains("already covered"));
    }

    /// <summary>
    /// The rule that keeps the hand-off from silently sending nothing: provisioning rows are gathered, and
    /// Hephaestus rejects a whole list containing an entry it cannot make — so they are excluded here, and the
    /// exclusion is reported rather than passed off as an empty request.
    /// </summary>
    [Fact]
    public void GatheringIsLeftOutAndSaidSo()
    {
        var handoff = CraftHandoff.Build(Snapshot(Craft(1, 3), Gather(2, 20), Gather(3, 2)), _ => 0);

        Assert.Equal(1, handoff.Crafts);
        Assert.DoesNotContain("\"itemId\":2", handoff.Json);
        Assert.DoesNotContain("\"itemId\":3", handoff.Json);
        Assert.Contains(handoff.Excluded, line => line.Contains("gathered, not crafted"));
        Assert.Contains(handoff.Excluded, line => line.Contains("all-or-nothing"));
    }

    /// <summary>The wire format Hephaestus's ListRequest parses: keepOrder, items, itemId, quantity.</summary>
    [Fact]
    public void ThePayloadIsHephaestusWireFormat()
    {
        var handoff = CraftHandoff.Build(Snapshot(Craft(36165, 2)), _ => 0);

        using var document = JsonDocument.Parse(handoff.Json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("keepOrder").GetBoolean());
        var first = root.GetProperty("items")[0];
        Assert.Equal(36165u, first.GetProperty("itemId").GetUInt32());
        Assert.Equal(2, first.GetProperty("quantity").GetInt32());
        Assert.Equal(4, CraftHandoff.WireFieldNames.Count);
    }

    [Fact]
    public void NothingToSendIsNotAnEmptyRequest()
    {
        var nothingHeld = CraftHandoff.Build(Snapshot(Craft(1, 3)), _ => 3);
        var noSnapshot = CraftHandoff.Build(null, _ => 0);

        Assert.False(nothingHeld.Any);
        Assert.Equal(string.Empty, nothingHeld.Json);
        Assert.Contains("nothing to hand over", CraftHandoff.Describe(nothingHeld));

        Assert.False(noSnapshot.Any);
        Assert.Contains(noSnapshot.Excluded, line => line.Contains("no request list has been captured"));
    }
}
