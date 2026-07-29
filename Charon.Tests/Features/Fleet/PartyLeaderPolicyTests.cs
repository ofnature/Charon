using Charon.Features.Fleet;
using Charon.Services.Game;

namespace Charon.Tests.Features.Fleet;

public sealed class PartyLeaderPolicyTests
{
    private const string Leader = "My Main";
    private const string Bot = "Bot Toon";

    private static readonly string[] Party = [Leader, Bot, "Second Bot"];

    private static PromoteDecision Evaluate(
        bool enabled = true,
        bool localIsPartyLeader = true,
        string fleetLeader = Leader,
        string localName = Bot,
        IReadOnlyList<string>? party = null,
        bool leaderOnline = true) =>
        PartyLeaderPolicy.Evaluate(enabled, localIsPartyLeader, fleetLeader, localName,
            party ?? Party, _ => leaderOnline);

    [Fact]
    public void BotHoldingLead_WithLeaderBackOnline_PromotesThem()
    {
        // The whole point: a disconnect parked lead on a bot and it never returns on its own.
        var d = Evaluate();
        Assert.True(d.Promote);
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
        var d = Evaluate(party: [Bot, "Second Bot"]);
        Assert.False(d.Promote);
        Assert.Contains("not in this party", d.Reason);
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
        Assert.False(Evaluate(fleetLeader: "my main", localName: Leader).Promote);
        Assert.True(Evaluate(party: ["MY MAIN", Bot]).Promote);
    }

    // --- Name safety: the name goes into a chat command ---

    [Theory]
    [InlineData("Korha Ishere")]
    [InlineData("Y'shtola Rhul")]
    [InlineData("Jean-Luc Picard")]
    public void RealCharacterNames_AreAccepted(string name)
    {
        Assert.True(PartyLeaderHelper.IsSafeName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad\nName")]              // newline could chain a second command
    [InlineData("Name /leave")]            // embedded slash command
    [InlineData("Name; /shutdown")]
    [InlineData("Toon<t>")]                // chat placeholder
    [InlineData("Way too long a name to ever be a real character name")]
    public void UnsafeNames_AreRefused(string name)
    {
        Assert.False(PartyLeaderHelper.IsSafeName(name));
    }
}
