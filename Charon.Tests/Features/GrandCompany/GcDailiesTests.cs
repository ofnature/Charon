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
    public void ASupplyRowsFlagAnnotatesTheVerdictInsteadOfOverridingIt()
    {
        // The flag's meaning for supply rows is documented NOWHERE, so it must not decide the verdict: a
        // "closed" reading that is really "you do not hold the item" would hide a day's work that is still
        // there, and the reverse would send someone crafting something already delivered. Facts decide; the
        // byte rides along beside them.
        var plan = GcDailies.Plan(Mission(requested: 1, available: false) with { AvailabilityRaw = 1 }, 0, 0);

        Assert.Contains("short 1 — a craft (Supply)", plan.Status);
        Assert.Contains("game flag 1", plan.Status);
    }

    [Fact]
    public void AnUnflaggedSupplyRow_CarriesNoFlagNoise()
    {
        var plan = GcDailies.Plan(Mission(requested: 1), inBags: 0, inRetainers: 0);

        Assert.DoesNotContain("game flag", plan.Status);
    }

    [Fact]
    public void AReadySupplyRow_AlsoCarriesTheFlagWhenItIsSet()
    {
        var plan = GcDailies.Plan(Mission(requested: 1, available: false) with { AvailabilityRaw = 7 }, 5, 0);

        Assert.True(plan.Ready);
        Assert.Contains("ready in the bags", plan.Status);
        Assert.Contains("game flag 7", plan.Status);
    }

    [Fact]
    public void AnExpertDeliveryRowTheGameRefuses_SaysSoWithoutMentioningDailyState()
    {
        // The flag IS documented for gear, so it can be stated plainly there — and must not borrow the
        // supply wording, which hedges because its meaning for supply rows is not documented.
        var plan = GcDailies.Plan(Mission(position: 12, requested: 1, available: false), inBags: 9, inRetainers: 0);

        Assert.False(plan.Ready);
        Assert.Equal("the game will not take this one", plan.Status);
    }

    [Fact]
    public void TheRawAvailabilityByteRidesAlongForALaterLook()
    {
        var plan = GcDailies.Plan(Mission(requested: 1, available: false) with { AvailabilityRaw = 3 }, 0, 0);

        Assert.Contains("game flag 3", plan.Status);
    }

    [Fact]
    public void CountsSplitTheBoardByItsOwnThreeTabs()
    {
        var counts = GcDailies.Counts(
        [
            Mission(position: 0), Mission(position: 7),
            Mission(position: 8), Mission(position: 9), Mission(position: 10),
            Mission(position: 11), Mission(position: 40),
        ]);

        Assert.Equal((2, 3, 2), counts);
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

    /// <summary>
    /// "Are today's hand-ins done?" is answered by the REQUEST LIST, not by the Timers window's countdown.
    /// A countdown says when the list rolls over; rows say work remains; no rows say there is none — and only
    /// while the list is actually open, because an unread list is unknown rather than empty.
    /// </summary>
    [Fact]
    public void RowsStillRequestedAreWorkRemaining()
    {
        var plans = GcDailies.PlanAll(
        [
            Mission(position: 0, itemId: 1, requested: 1),
            Mission(position: 1, itemId: 2, requested: 1),
        ], id => id == 2 ? 4 : 0, _ => 0);

        var summary = GcDailies.RequestSummary(plans, boardOpen: true);

        Assert.Contains("2 item(s) still requested", summary);
        Assert.Contains("1 handable now", summary);
    }

    [Fact]
    public void AnEmptyRequestListIsTheThingThatMeansDone()
    {
        Assert.Equal("nothing left to hand in — the request list is empty",
            GcDailies.RequestSummary([], boardOpen: true));
    }

    /// <summary>An unopened list is unknown, never done — that claim is what the countdown was misused for.</summary>
    [Fact]
    public void AListNobodyCouldReadIsNotAnEmptyList()
    {
        var summary = GcDailies.RequestSummary([], boardOpen: false);

        Assert.Contains("unknown", summary);
        Assert.DoesNotContain("nothing left", summary);
    }

    /// <summary>
    /// The delivery window states the day's state in words, and those words are the answer. Both strings below
    /// are verbatim from a live dump of GrandCompanySupplyList (they arrive as a value pair, not text nodes).
    /// </summary>
    [Fact]
    public void TheDeliveryWindowsOwnSentenceSaysTodayIsDone()
    {
        string[] window =
        [
            "Grand Company Delivery Missions",
            "Next Mission Allowance in 13 hours and 52 minutes at 3:00 p.m. on 9/26 (Earth time).",
            "You possess no applicable items.",
            "No more deliveries are being accepted today.",
            "Qty.",
            "Seals",
        ];

        Assert.True(GcDailies.DeliveriesClosed(window));
        Assert.Equal("You possess no applicable items.", GcDailies.Notice(window));
    }

    /// <summary>
    /// Item names arrive from the window with icon payload glyphs baked in, so they must not be mistaken for a
    /// sentence — the names this repo uses come from the item sheet by id.
    /// </summary>
    [Fact]
    public void GlyphLadenItemNamesAreNotNotices()
    {
        string[] window =
        [
            "\uFFFDH\uFFFD\uFFFD%\uFFFD I\uFFFD\uFFFD&Claro Walnut Sandals of Crafting.",
            "Nothing to report here at all.",
        ];

        Assert.Null(GcDailies.Notice(window));
        Assert.False(GcDailies.DeliveriesClosed(window));
    }
}
