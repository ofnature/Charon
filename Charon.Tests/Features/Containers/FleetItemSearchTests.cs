using Charon.Features.Containers;

namespace Charon.Tests.Features.Containers;

public sealed class FleetItemSearchTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private static FleetItemSearch Started(uint itemId = 5106)
    {
        var search = new FleetItemSearch();
        search.Start("abc", itemId, Now, Window);
        return search;
    }

    private static FleetHolding[] Bags(int nq, int hq = 0) => [new("bags", nq, hq)];

    [Fact]
    public void SilenceIsNeverZero()
    {
        // The relay is a broadcast with no delivery guarantee: a box may be zoning, loading or off.
        // Reading silence as "they have none" is how a planner crafts what the fleet already owns.
        var search = Started();
        search.Record("abc", "Saar", true, Now, Bags(9));

        var mid = search.Report(Now.AddSeconds(1));
        Assert.False(mid.Complete);
        Assert.Equal(1, mid.Answered);
        Assert.Contains("so far", mid.Summarize());
    }

    [Fact]
    public void TheWindowClosingIsWhatMakesItFinal()
    {
        var search = Started();
        search.Record("abc", "Saar", true, Now, Bags(9));

        Assert.True(search.Running(Now.AddSeconds(1)));
        Assert.False(search.Running(Now.AddSeconds(4)));
        Assert.True(search.Report(Now.AddSeconds(4)).Complete);
    }

    [Fact]
    public void LateAnswersToAnOldQuestionAreDropped()
    {
        var search = Started();
        Assert.False(search.Record("stale-id", "Saar", true, Now, Bags(99)));
        Assert.Empty(search.Report(Now).Answers);
    }

    [Fact]
    public void ABoxAnsweringTwice_Corrects_RatherThanDoubling()
    {
        var search = Started();
        search.Record("abc", "Saar", true, Now, Bags(9));
        search.Record("abc", "Saar", true, Now, Bags(4));

        var report = search.Report(Now.AddSeconds(4));
        Assert.Equal(1, report.Answered);
        Assert.Equal(4, report.TotalNq);
    }

    [Fact]
    public void FreeTrialStacksAreCounted_ButNotReachable()
    {
        // A free trial toon cannot trade, use the market board or join an FC. Its items exist and
        // are worth showing, but no plan can ever collect them.
        var search = Started();
        search.Record("abc", "Saar", canTrade: true, Now, Bags(9));
        search.Record("abc", "Trial", canTrade: false, Now, Bags(30));

        var report = search.Report(Now.AddSeconds(4));
        Assert.Equal(39, report.TotalNq);
        Assert.Equal(9, report.ReachableNq);
        Assert.Contains("30 on a free trial toon, unreachable", report.Summarize());
    }

    [Fact]
    public void HqIsCountedSeparately_Everywhere()
    {
        var search = Started();
        search.Record("abc", "Korha", true, Now, [new("retainer:T'sola", 12, 3)]);

        var report = search.Report(Now.AddSeconds(4));
        Assert.Equal(12, report.TotalNq);
        Assert.Equal(3, report.TotalHq);
        Assert.Equal(15, Assert.Single(report.Answers).Total);
    }

    [Fact]
    public void TheOldestCaptureIsReported_SoStalenessIsVisible()
    {
        var search = Started();
        search.Record("abc", "Korha", true, Now, Bags(1));
        search.Record("abc", "Saar", true, Now.AddDays(-112), Bags(1));

        Assert.Equal(Now.AddDays(-112), search.Report(Now.AddSeconds(4)).OldestSeenUtc);
    }

    [Fact]
    public void BiggestHolderFirst()
    {
        var search = Started();
        search.Record("abc", "Small", true, Now, Bags(1));
        search.Record("abc", "Big", true, Now, Bags(40));

        Assert.Equal("Big", search.Report(Now.AddSeconds(4)).Answers[0].Character);
    }

    [Fact]
    public void NobodyAnswering_SaysSo_RatherThanReportingNone()
    {
        var search = Started();
        Assert.Equal("no box answered", search.Report(Now.AddSeconds(4)).Summarize());
    }

    [Fact]
    public void StartingAgainClearsTheLastSearch()
    {
        var search = Started();
        search.Record("abc", "Saar", true, Now, Bags(9));
        search.Start("def", 5107, Now.AddSeconds(10), Window);

        var report = search.Report(Now.AddSeconds(10));
        Assert.Empty(report.Answers);
        Assert.Equal(5107u, report.ItemId);
    }
}
