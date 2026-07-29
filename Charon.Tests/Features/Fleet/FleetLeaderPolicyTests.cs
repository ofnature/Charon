using Charon.Features.Fleet;

namespace Charon.Tests.Features.Fleet;

public sealed class FleetLeaderPolicyTests
{
    private const string Me = "Alt Toon";
    private const string Main = "My Main";
    private const string Roommate = "Roommate Main";

    private static bool Trusted(string name) => name is Me or Main or Roommate;

    private static LeaderChangeDecision Evaluate(
        string sender = Main,
        string nominee = Main,
        string localName = Me,
        string currentLeader = "") =>
        FleetLeaderPolicy.Evaluate(sender, nominee, localName, currentLeader, Trusted);

    [Fact]
    public void FleetToonNominatingItself_IsAccepted()
    {
        var d = Evaluate();
        Assert.True(d.Accept);
        Assert.Contains(Main, d.Reason);
    }

    [Fact]
    public void EitherHumanDrivenToon_CanBeLeader()
    {
        // Two PCs, one driver each — the leader may be on either machine.
        Assert.True(Evaluate(sender: Main, nominee: Main).Accept);
        Assert.True(Evaluate(sender: Roommate, nominee: Roommate).Accept);
    }

    [Fact]
    public void OneBoxCanNominateADifferentToon()
    {
        // e.g. setting the roommate's main as leader from this box.
        var d = Evaluate(sender: Main, nominee: Roommate);
        Assert.True(d.Accept);
    }

    [Fact]
    public void UntrustedSender_IsIgnored()
    {
        var d = Evaluate(sender: "Random Stranger");
        Assert.False(d.Accept);
        Assert.Contains("not a fleet toon", d.Reason);
    }

    [Fact]
    public void UntrustedNominee_IsIgnored()
    {
        // Adopting a name no toon has would leave this box silently ignoring fleet commands.
        var d = Evaluate(sender: Main, nominee: "Nobody At All");
        Assert.False(d.Accept);
        Assert.Contains("not a fleet toon", d.Reason);
    }

    [Fact]
    public void SelfNomination_IsAllowedEvenIfNotOnTheRoster()
    {
        // The roster may not list us yet (Daedalus still starting up).
        var d = FleetLeaderPolicy.Evaluate(Main, "Fresh Toon", "Fresh Toon", "", Trusted);
        Assert.True(d.Accept);
    }

    [Fact]
    public void AlreadyTheLeader_IsANoOp()
    {
        var d = Evaluate(nominee: Main, currentLeader: Main);
        Assert.False(d.Accept);
        Assert.Contains("already", d.Reason);
    }

    [Fact]
    public void LeaderComparisonIsCaseInsensitive()
    {
        Assert.False(Evaluate(nominee: "my main", currentLeader: Main).Accept);
    }

    [Theory]
    [InlineData("", Main)]
    [InlineData(Main, "")]
    public void MalformedDesignations_AreIgnored(string sender, string nominee)
    {
        var d = FleetLeaderPolicy.Evaluate(sender, nominee, Me, "", Trusted);
        Assert.False(d.Accept);
        Assert.Contains("malformed", d.Reason);
    }
}
