using Charon.Features.Fleet;

namespace Charon.Tests.Features.Fleet;

public sealed class DutyExitPolicyTests
{
    private const string Leader = "Fleet Leader";
    private const string Me = "Alt Toon";

    private static readonly string[] FleetParty = [Leader, Me, "Other Alt"];

    // Case-insensitive to match production: the real trust set is an OrdinalIgnoreCase HashSet.
    private static readonly HashSet<string> TrustedNames =
        new([Leader, Me, "Other Alt", "Third Alt"], StringComparer.OrdinalIgnoreCase);

    private static bool Trusted(string name) => TrustedNames.Contains(name);

    private static DutyExitDecision Evaluate(
        bool enabled = true,
        bool boundByDuty = true,
        string commandLeader = Leader,
        string fleetLeader = Leader,
        string localName = Me,
        IReadOnlyList<string>? party = null) =>
        DutyExitPolicy.Evaluate(enabled, boundByDuty, commandLeader, fleetLeader, localName,
            party ?? FleetParty, Trusted);

    [Fact]
    public void FleetLeaderCommand_InAnAllFleetDuty_Leaves()
    {
        var d = Evaluate();
        Assert.True(d.Leave);
    }

    [Fact]
    public void Disabled_DoesNothing()
    {
        Assert.False(Evaluate(enabled: false).Leave);
    }

    [Fact]
    public void NoFleetLeaderConfigured_DoesNothing()
    {
        var d = Evaluate(fleetLeader: string.Empty);
        Assert.False(d.Leave);
        Assert.Contains("no fleet leader", d.Reason);
    }

    [Fact]
    public void CommandFromAnyoneElse_IsIgnored()
    {
        // The whole point of the designation: one stray broadcast must not empty a duty.
        var d = Evaluate(commandLeader: "Random Alt");
        Assert.False(d.Leave);
        Assert.Contains("not the fleet leader", d.Reason);
    }

    [Fact]
    public void LeaderNameIsCaseInsensitive()
    {
        Assert.True(Evaluate(commandLeader: "fleet leader").Leave);
    }

    [Fact]
    public void OurOwnCommand_IsIgnored_TheLeaderLeavesLocally()
    {
        // The relay never delivers to its publisher; a frame naming us is a loop or misconfig.
        var d = Evaluate(commandLeader: Me, fleetLeader: Me, localName: Me);
        Assert.False(d.Leave);
        Assert.Contains("that is us", d.Reason);
    }

    [Fact]
    public void NotInADuty_DoesNothing()
    {
        var d = Evaluate(boundByDuty: false);
        Assert.False(d.Leave);
        Assert.Contains("not in a duty", d.Reason);
    }

    // --- The stranger gate ---

    [Fact]
    public void StrangerInTheParty_HoldsPosition()
    {
        // A matched duty: walking out on strangers earns a penalty, so the relay is overruled.
        var d = Evaluate(party: [Leader, Me, "Random Sprout"]);
        Assert.False(d.Leave);
        Assert.Contains("non-fleet", d.Reason);
    }

    [Fact]
    public void SeveralStrangers_AreCounted()
    {
        var d = Evaluate(party: [Leader, Me, "Stranger A", "Stranger B"]);
        Assert.False(d.Leave);
        Assert.Contains("2 non-fleet", d.Reason);
    }

    [Fact]
    public void EmptySlotsInThePartyList_AreNotStrangers()
    {
        Assert.True(Evaluate(party: [Leader, Me, "", ""]).Leave);
    }

    // --- Party scope: only the leader's OWN group leaves ---

    [Fact]
    public void ToonInADifferentGroup_StaysInItsDungeon()
    {
        // The relay reaches every box on the LAN. A separate group of bots running its own dungeon
        // passes every other gate (all trusted, same configured leader) — being grouped with the
        // leader is what makes the command ours.
        var d = Evaluate(party: [Me, "Other Alt", "Third Alt"]);

        Assert.False(d.Leave);
        Assert.Contains("not in", d.Reason);
        Assert.Contains(Leader, d.Reason);
    }

    [Fact]
    public void SoloInADuty_StaysPut()
    {
        // Solo means not grouped with the leader, so the command is not for us.
        Assert.False(Evaluate(party: [Me]).Leave);
        Assert.False(Evaluate(party: []).Leave);
    }

    [Fact]
    public void GroupedWithTheLeader_Leaves()
    {
        Assert.True(Evaluate(party: [Leader, Me]).Leave);
    }

    [Fact]
    public void PartyMembershipIsCaseInsensitive()
    {
        Assert.True(Evaluate(party: ["fleet leader", Me]).Leave);
    }

    [Fact]
    public void OurOwnNameNeverCountsAsAStranger()
    {
        // Even if the trust check would reject us, we are not a stranger to ourselves.
        var d = DutyExitPolicy.Evaluate(true, true, Leader, Leader, "Untrusted Me",
            [Leader, "Untrusted Me"], name => name == Leader);
        Assert.True(d.Leave);
    }
}
