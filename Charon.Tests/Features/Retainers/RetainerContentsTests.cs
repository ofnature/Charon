using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

/// <summary>
/// The retainer contents store. The whole design rests on one distinction, so it is tested by name: an
/// UNKNOWN retainer (never opened) is not an EMPTY one, and a caller that cannot tell them apart will give
/// up on materials it actually has.
/// </summary>
public class RetainerContentsTests
{
    private const uint Manganese = 36165;
    private const uint Sykon = 36096;

    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private static RetainerBag Bag(string name, DateTime captured, params (uint Id, int Qty, bool Hq)[] stacks) =>
        new($"1:{name}", name, captured, stacks.Select(s => new RetainerStackCount(s.Id, s.Qty, s.Hq)).ToList());

    private static readonly List<(string Key, string Name)> All =
    [
        ("1:T'sala", "T'sala"),
        ("1:T'sola", "T'sola"),
    ];

    [Fact]
    public void HqIsCountedSeparately()
    {
        var bags = new List<RetainerBag>
        {
            Bag("T'sala", Now.AddMinutes(-5), (Manganese, 250, false), (Manganese, 12, true)),
        };

        var (nq, hq) = RetainerContents.Total(bags, Manganese);

        Assert.Equal(250, nq);
        Assert.Equal(12, hq);
    }

    [Fact]
    public void HqIsNotFoldedIntoThePerRetainerAnswer()
    {
        var bags = new List<RetainerBag>
        {
            Bag("T'sala", Now.AddMinutes(-5), (Manganese, 250, false), (Manganese, 12, true)),
            Bag("T'sola", Now.AddMinutes(-5), (Manganese, 5, true)),
        };

        var holdings = RetainerContents.Find(bags, Manganese);

        var tsala = holdings.Single(h => h.Retainer == "T'sala");
        Assert.Equal(250, tsala.Nq);
        Assert.Equal(12, tsala.Hq);
        Assert.Equal(262, tsala.Total);

        var tsola = holdings.Single(h => h.Retainer == "T'sola");
        Assert.Equal(0, tsola.Nq);
        Assert.Equal(5, tsola.Hq);
    }

    [Fact]
    public void ARetainerThatHasNoneIsReportedAtZero()
    {
        var bags = new List<RetainerBag> { Bag("T'sala", Now.AddMinutes(-5), (Manganese, 10, false)) };

        var holdings = RetainerContents.Find(bags, Manganese);

        // T'sola is absent — the store does not know it holds none, only that we never looked, which is
        // why the Audit (not this list) is what a caller asks about coverage.
        Assert.Single(holdings);
        Assert.Equal(0, holdings[0].Hq);
    }

    [Fact]
    public void NeverOpenedIsNotTheSameAsEmpty()
    {
        var bags = new List<RetainerBag> { Bag("T'sala", Now.AddMinutes(-5), (Manganese, 10, false)) };

        var (fresh, stale, unknown) = RetainerContents.Audit(bags, All, Now);

        Assert.Equal(1, fresh);
        Assert.Equal(0, stale);
        Assert.Equal(1, unknown); // T'sola has never been opened: unknown, NOT zero
    }

    [Fact]
    public void ARefreshPassAsksForTheUnopenedOnesFirst()
    {
        var bags = new List<RetainerBag> { Bag("T'sola", Now.AddMinutes(-5)) };

        var plan = RetainerContents.PlanRefresh(bags, All, Now);

        Assert.Single(plan);
        Assert.Equal("T'sala", plan[0].Name);
        Assert.Equal("never opened", plan[0].Reason);
    }

    [Fact]
    public void ARefreshPassSkipsWhatIsStillFresh()
    {
        var bags = new List<RetainerBag>
        {
            Bag("T'sala", Now.AddMinutes(-5)),
            Bag("T'sola", Now.AddMinutes(-10)),
        };

        Assert.Empty(RetainerContents.PlanRefresh(bags, All, Now));
    }

    [Fact]
    public void StaleEntriesAreNamedWithTheirAgeOldestFirst()
    {
        var bags = new List<RetainerBag>
        {
            Bag("T'sala", Now.AddHours(-3)),
            Bag("T'sola", Now.AddHours(-30)),
        };

        var plan = RetainerContents.PlanRefresh(bags, All, Now);
        var (fresh, stale, unknown) = RetainerContents.Audit(bags, All, Now);

        Assert.Equal(2, plan.Count);
        Assert.Equal("T'sola", plan[0].Name);
        Assert.Equal("last seen 30 h ago", plan[0].Reason);
        Assert.Equal("T'sala", plan[1].Name);
        Assert.Equal(0, fresh);
        Assert.Equal(2, stale);
        Assert.Equal(0, unknown);
    }

    [Fact]
    public void FetchingHighQualityTakesOnlyHighQuality()
    {
        var bags = new List<RetainerBag>
        {
            Bag("T'sala", Now.AddMinutes(-5), (Manganese, 250, false), (Manganese, 12, true)),
            Bag("T'sola", Now.AddMinutes(-5), (Manganese, 40, true)),
        };

        var plan = RetainerContents.PlanFetch(bags, Manganese, 10, highQuality: true);

        Assert.True(plan.Possible);
        Assert.Equal("T'sola", plan.Retainer); // most HQ of the two
        Assert.Equal(10, plan.Hq);
        Assert.Equal(0, plan.Nq);
    }

    [Fact]
    public void FetchingNormalQualityFillsFromNqThenTopsUpWithHq()
    {
        var bags = new List<RetainerBag>
        {
            Bag("T'sala", Now.AddMinutes(-5), (Manganese, 4, false), (Manganese, 20, true)),
        };

        var plan = RetainerContents.PlanFetch(bags, Manganese, 10, highQuality: false);

        Assert.True(plan.Possible);
        Assert.Equal("T'sala", plan.Retainer);
        Assert.Equal(4, plan.Nq);
        Assert.Equal(6, plan.Hq); // the shortfall comes out of the HQ stack rather than refusing
        Assert.Equal(10, plan.Total);
    }

    [Fact]
    public void FetchingPrefersTheRetainerHoldingTheMost()
    {
        var bags = new List<RetainerBag>
        {
            Bag("T'sala", Now.AddMinutes(-5), (Manganese, 250, false)),
            Bag("T'sola", Now.AddMinutes(-5), (Manganese, 40, false)),
        };

        Assert.Equal("T'sala", RetainerContents.PlanFetch(bags, Manganese, 100, false).Retainer);
    }

    [Fact]
    public void AFetchTheStoreCannotCoverIsRefusedWithTheNumbers()
    {
        var bags = new List<RetainerBag> { Bag("T'sala", Now.AddMinutes(-5), (Manganese, 4, false)) };

        var plan = RetainerContents.PlanFetch(bags, Manganese, 100, highQuality: false);

        Assert.False(plan.Possible);
        Assert.Contains("holds 4 of the 100 wanted", plan.Refusal);
        Assert.Contains("last time it was seen", plan.Refusal); // the answer is a snapshot, and says so
    }

    [Fact]
    public void AskingForHighQualityNobodyHas_IsRefusedSeparately()
    {
        var bags = new List<RetainerBag> { Bag("T'sala", Now.AddMinutes(-5), (Manganese, 250, false)) };

        var plan = RetainerContents.PlanFetch(bags, Manganese, 1, highQuality: true);

        Assert.False(plan.Possible);
        Assert.Contains("high-quality", plan.Refusal);
    }

    [Fact]
    public void WithNothingEverOpened_TheRefusalSaysSo()
    {
        var plan = RetainerContents.PlanFetch([], Manganese, 10, highQuality: false);

        Assert.False(plan.Possible);
        Assert.Contains("no retainer contents are known yet", plan.Refusal);
    }

    [Fact]
    public void AskingForNothingIsRefused()
    {
        var bags = new List<RetainerBag> { Bag("T'sala", Now.AddMinutes(-5), (Manganese, 250, false)) };

        Assert.False(RetainerContents.PlanFetch(bags, Manganese, 0, false).Possible);
    }

    [Fact]
    public void AStackForAnotherItemIsIgnored()
    {
        var bags = new List<RetainerBag>
        {
            Bag("T'sala", Now.AddMinutes(-5), (Manganese, 250, false), (Sykon, 96, false)),
        };

        var (nq, hq) = RetainerContents.Total(bags, Manganese);

        Assert.Equal(250, nq);
        Assert.Equal(0, hq);
    }
}
