using Charon.Features.Fleet;

namespace Charon.Tests.Features.Fleet;

public sealed class PartyLeaderPolicyTests
{
    private const string Leader = "My Main";
    private const string Bot = "Bot Toon";

    private static FleetPartyMember Member(string name, int slot, bool sameZone = true) =>
        new(name, slot, sameZone);

    private static readonly FleetPartyMember[] Party =
    [
        Member(Bot, 1),
        Member(Leader, 2),
        Member("Second Bot", 3),
    ];

    private static PromoteDecision Evaluate(
        bool enabled = true,
        bool localIsPartyLeader = true,
        string fleetLeader = Leader,
        string localName = Bot,
        IReadOnlyList<FleetPartyMember>? party = null,
        bool leaderOnline = true) =>
        PartyLeaderPolicy.Evaluate(enabled, localIsPartyLeader, fleetLeader, localName,
            party ?? Party, _ => leaderOnline);

    [Fact]
    public void BotHoldingLead_WithLeaderBackAndInZone_PromotesTheirSlot()
    {
        // The whole point: a disconnect parked lead on a bot and it never returns on its own.
        var d = Evaluate();

        Assert.True(d.Promote);
        Assert.Equal(2, d.Slot); // /leader addresses party slots, never names
        Assert.Contains(Leader, d.Reason);
    }

    [Fact]
    public void Disabled_DoesNothing()
    {
        Assert.False(Evaluate(enabled: false).Promote);
    }

    [Fact]
    public void NoFleetLeaderSet_DoesNothing()
    {
        var d = Evaluate(fleetLeader: string.Empty);
        Assert.False(d.Promote);
        Assert.Contains("no fleet leader", d.Reason);
    }

    [Fact]
    public void NotHoldingPartyLead_CannotPromote()
    {
        var d = Evaluate(localIsPartyLeader: false);
        Assert.False(d.Promote);
        Assert.Contains("not party leader", d.Reason);
    }

    [Fact]
    public void LeaderStillOffline_Waits()
    {
        // A disconnected toon stays in the party, so membership alone doesn't mean they're back.
        var d = Evaluate(leaderOnline: false);
        Assert.False(d.Promote);
        Assert.Contains("offline", d.Reason);
    }

    [Fact]
    public void LeaderNotInThisParty_DoesNothing()
    {
        var d = Evaluate(party: [Member(Bot, 1), Member("Second Bot", 2)]);
        Assert.False(d.Promote);
        Assert.Contains("not in this party", d.Reason);
    }

    // --- Same zone: the game refuses /leader for a member who isn't here ---

    [Fact]
    public void LeaderInAnotherZone_Waits()
    {
        // Verified in-game: cross-zone attempts just spam "unavailable at this time".
        var d = Evaluate(party: [Member(Bot, 1), Member(Leader, 2, sameZone: false)]);

        Assert.False(d.Promote);
        Assert.Contains("another zone", d.Reason);
    }

    [Fact]
    public void LeaderComesBackToOurZone_ThenPromotes()
    {
        var away = Evaluate(party: [Member(Bot, 1), Member(Leader, 2, sameZone: false)]);
        var here = Evaluate(party: [Member(Bot, 1), Member(Leader, 2, sameZone: true)]);

        Assert.False(away.Promote);
        Assert.True(here.Promote);
    }

    [Fact]
    public void SlotOutsideOneToEight_IsRefused()
    {
        var d = Evaluate(party: [Member(Leader, 0)]);
        Assert.False(d.Promote);
        Assert.Contains("no usable party slot", d.Reason);
    }

    // --- Nobody promotes themselves ---

    [Fact]
    public void OnTheLeadersOwnBox_HoldingLead_IsTheCorrectState()
    {
        var d = Evaluate(fleetLeader: Leader, localName: Leader, localIsPartyLeader: true);
        Assert.False(d.Promote);
        Assert.Contains("hold party lead", d.Reason);
    }

    [Fact]
    public void OnTheLeadersOwnBox_WithoutLead_WaitsForABoxToHandItBack()
    {
        var d = Evaluate(fleetLeader: Leader, localName: Leader, localIsPartyLeader: false);
        Assert.False(d.Promote);
        Assert.Contains("waiting", d.Reason);
    }

    [Fact]
    public void NameMatchingIsCaseInsensitive()
    {
        var d = Evaluate(party: [Member(Bot, 1), Member("MY MAIN", 2)]);
        Assert.True(d.Promote);
        Assert.Equal(2, d.Slot);
    }

    [Fact]
    public void NoSlotIsReportedWhenNotPromoting()
    {
        Assert.Equal(0, Evaluate(enabled: false).Slot);
    }
}
