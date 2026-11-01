using Charon.Features.DeepDungeon;

namespace Charon.Tests.Features.DeepDungeon;

public sealed class SilverCofferPolicyTests
{
    [Fact]
    public void AFullParty_WithNoThreats_MayOpen()
    {
        var (may, _) = SilverCofferPolicy.Decide([new("Korha", 5000, 5000), new("Saar", 4800, 4800)], threats: 0);
        Assert.True(may);
    }

    [Fact]
    public void APartnerBelowFull_HoldsIt_AndIsNamed()
    {
        // The old rule only looked at our own HP: a low partner never held a silver coffer.
        var (may, reason) = SilverCofferPolicy.Decide([new("Korha", 5000, 5000), new("Saar", 4700, 4800)], threats: 0);
        Assert.False(may);
        Assert.Contains("Saar", reason);
    }

    [Fact]
    public void ADeadPartner_HoldsIt()
    {
        var (may, _) = SilverCofferPolicy.Decide([new("Korha", 5000, 5000), new("Saar", 0, 4800)], threats: 0);
        Assert.False(may);
    }

    [Fact]
    public void AnyThreat_HoldsIt_EvenAtFullHp()
    {
        var (may, reason) = SilverCofferPolicy.Decide([new("Korha", 5000, 5000)], threats: 1);
        Assert.False(may);
        Assert.Contains("hostile", reason);
    }

    [Fact]
    public void UnreadableHp_IsNotFull()
    {
        var (may, _) = SilverCofferPolicy.Decide([new("Korha", 0, 0)], threats: 0);
        Assert.False(may);
    }
}
