using Charon.Features.AutoAccept;

namespace Charon.Tests.Features.AutoAccept;

public sealed class DutyAcceptPolicyTests
{
    private const string Me = "Korha Ishere";

    private static readonly HashSet<string> Fleet =
        new(new[] { Me, "Styx", "Acheron", "Lethe" }, StringComparer.OrdinalIgnoreCase);

    private static bool Trusted(string name) => Fleet.Contains(name);

    [Fact]
    public void AllLanParty_Commences()
    {
        var party = new[] { Me, "Styx", "Acheron", "Lethe" };
        Assert.True(DutyAcceptPolicy.ShouldAutoCommence(true, party, Me, Trusted));
    }

    [Fact]
    public void StrangerInParty_NeverCommences()
    {
        var party = new[] { Me, "Styx", "Random Stranger" };
        Assert.False(DutyAcceptPolicy.ShouldAutoCommence(true, party, Me, Trusted));
    }

    [Fact]
    public void SoloQueue_NeverCommences()
    {
        // A matched/roulette pop arrives before the party forms — always the player's call.
        Assert.False(DutyAcceptPolicy.ShouldAutoCommence(true, new[] { Me }, Me, Trusted));
        Assert.False(DutyAcceptPolicy.ShouldAutoCommence(true, Array.Empty<string>(), Me, Trusted));
    }

    [Fact]
    public void Disabled_NeverCommences()
    {
        var party = new[] { Me, "Styx" };
        Assert.False(DutyAcceptPolicy.ShouldAutoCommence(false, party, Me, Trusted));
    }

    [Fact]
    public void UnreadableMemberName_Refuses()
    {
        var party = new[] { Me, "Styx", "" };
        Assert.False(DutyAcceptPolicy.ShouldAutoCommence(true, party, Me, Trusted));
    }

    [Fact]
    public void NameMatchingIsCaseInsensitive()
    {
        var party = new[] { "korha ishere", "STYX" };
        Assert.True(DutyAcceptPolicy.ShouldAutoCommence(true, party, Me, Trusted));
    }

    [Fact]
    public void PartyOfOnlySelfDuplicated_HasNoOthers_Refuses()
    {
        // Defensive: a list that contains only us (however many times) is not a group.
        var party = new[] { Me, Me };
        Assert.False(DutyAcceptPolicy.ShouldAutoCommence(true, party, Me, Trusted));
    }
}
