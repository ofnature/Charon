using Charon.Features.GrandCompany;

namespace Charon.Tests.Features.GrandCompany;

public class GcDailiesTests
{
    private static GcDailyMission Mission(
        int position = 0,
        uint itemId = 36165,
        string name = "Manganese Ore",
        int requested = 1,
        int exp = 7255750,
        int seals = 930,
        bool bonus = false,
        bool available = true) =>
        new(position, GcDailies.KindFor(position), GcDailies.JobFor(position), itemId, name,
            requested, exp, seals, 0, bonus, available);

    [Fact]
    public void PositionsAreTheGamesOwnLayout()
    {
        Assert.Equal(GcMissionKind.Supply, GcDailies.KindFor(0));
        Assert.Equal("CRP", GcDailies.JobFor(0));
        Assert.Equal(GcMissionKind.Supply, GcDailies.KindFor(7));
        Assert.Equal("CUL", GcDailies.JobFor(7));
        Assert.Equal(GcMissionKind.Provisioning, GcDailies.KindFor(8));
        Assert.Equal("MIN", GcDailies.JobFor(8));
        Assert.Equal("FSH", GcDailies.JobFor(10));
        Assert.Equal(GcMissionKind.ExpertDelivery, GcDailies.KindFor(11));
        Assert.Equal(string.Empty, GcDailies.JobFor(11));
    }

    [Fact]
    public void GrandCompaniesAreNamedAsTheClientIdsThem()
    {
        Assert.Equal("Maelstrom", GcDailies.GrandCompanyName(1));
        Assert.Equal("Order of the Twin Adder", GcDailies.GrandCompanyName(2));
        Assert.Equal("Immortal Flames", GcDailies.GrandCompanyName(3));
        Assert.Contains("no Grand Company", GcDailies.GrandCompanyName(0));
    }

    [Fact]
    public void AnItemInTheBags_IsReady()
    {
        var plan = GcDailies.Plan(Mission(requested: 1, bonus: true), inBags: 3, inRetainers: 0);

        Assert.True(plan.Ready);
        Assert.Contains("ready in the bags", plan.Status);
        Assert.Contains("bonus", plan.Status);
    }

    [Fact]
    public void AnItemOnlyInARetainer_SaysFetchItRatherThanShort()
    {
        // The game's own column reads 0/0 here, because it only counts the inventory. This is the whole
        // reason the retainer contents store is worth having.
        var plan = GcDailies.Plan(Mission(requested: 1), inBags: 0, inRetainers: 4);

        Assert.False(plan.Ready);
        Assert.True(plan.NeedsFetch);
        Assert.Equal("in a retainer — fetch it first", plan.Status);
    }

    [Fact]
    public void ASupplyShortfall_NamesTheCraft()
    {
        var plan = GcDailies.Plan(Mission(position: 3, requested: 3), inBags: 1, inRetainers: 0);

        Assert.Contains("a craft (Supply)", plan.Status);
        Assert.Contains("short 2", plan.Status);
        Assert.False(plan.NeedsFetch);
    }

    [Fact]
    public void AProvisioningShortfall_NamesTheGather()
    {
        var plan = GcDailies.Plan(Mission(position: 9, requested: 3), inBags: 0, inRetainers: 1);

        Assert.Contains("a gather (Provisioning)", plan.Status);
    }

    [Fact]
    public void AnExpertDeliveryRow_IsNotACraftOrAGather()
    {
        var plan = GcDailies.Plan(Mission(position: 12, requested: 5), inBags: 0, inRetainers: 0);

        Assert.Contains("gear hand-in", plan.Status);
    }

    [Fact]
    public void SomethingAlreadyHandedIn_IsNotReportedAsShort()
    {
        // The game flips this flag once the daily delivery is made; telling the player to craft it again
        // would be the most expensive kind of wrong.
        var plan = GcDailies.Plan(Mission(requested: 1, available: false), inBags: 0, inRetainers: 0);

        Assert.False(plan.Ready);
        Assert.False(plan.NeedsFetch);
        Assert.Equal("already handed in", plan.Status);
    }

    [Fact]
    public void ARowWithNothingRequested_IsIgnoredRatherThanCalledShort()
    {
        var plan = GcDailies.Plan(Mission(requested: 0), inBags: 0, inRetainers: 0);

        Assert.Equal("nothing requested", plan.Status);
    }

    [Fact]
    public void PlanAll_SkipsEmptyRowsAndKeepsTheBoardsOrder()
    {
        var plans = GcDailies.PlanAll(
            [Mission(position: 4, itemId: 111), Mission(position: 1, itemId: 0), Mission(position: 9, itemId: 222)],
            _ => 5,
            _ => 0);

        Assert.Equal(2, plans.Count);
        Assert.Equal(4, plans[0].Mission.Position);
        Assert.Equal(9, plans[1].Mission.Position);
    }

    [Fact]
    public void TheSummary_CountsWhatIsWorthTheTrip()
    {
        var plans = GcDailies.PlanAll(
            [
                Mission(position: 0, itemId: 1, requested: 1, exp: 100, seals: 10), // short: nothing held
                Mission(position: 1, itemId: 2, requested: 1, exp: 200, seals: 20), // in a retainer
                Mission(position: 8, itemId: 3, requested: 1, exp: 300, seals: 30), // ready in the bags
            ],
            id => id == 3 ? 5 : 0,
            id => id == 2 ? 4 : 0);

        var summary = GcDailies.Summarise(plans);

        // Only the READY row's rewards count towards the "worth the trip" line.
        Assert.Contains("1 handable now", summary);
        Assert.Contains("300 exp", summary);
        Assert.Contains("30 seals", summary);
        Assert.Contains("1 in retainers", summary);
    }

    [Fact]
    public void TheSummary_WithNothingToDo_SaysSo()
    {
        var plans = GcDailies.PlanAll([Mission(position: 0, itemId: 1, requested: 1)], _ => 0, _ => 0);

        Assert.Equal("nothing is handable right now", GcDailies.Summarise(plans));
    }
}
