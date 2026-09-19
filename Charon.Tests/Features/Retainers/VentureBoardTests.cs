using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class VentureBoardTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private static RetainerVenture Retainer(
        string name = "Bob", uint venture = 1, DateTime? complete = null,
        byte level = 100, byte items = 0, uint gil = 0) =>
        new(name, venture, complete, level, items, gil);

    [Fact]
    public void NoVentureAssigned_IsIdle()
    {
        var rows = VentureBoard.Compose(Now, [Retainer(venture: 0)]);
        Assert.Equal(VentureState.Idle, Assert.Single(rows).State);
    }

    [Fact]
    public void VentureWithoutATimer_IsUnknown_NotReady()
    {
        // The client fetches timers lazily. Calling this "ready" would send the player to a bell
        // for nothing, and calling it "running" would invent a duration we were never given.
        var rows = VentureBoard.Compose(Now, [Retainer(complete: null)]);
        Assert.Equal(VentureState.Unknown, Assert.Single(rows).State);
        Assert.Null(rows[0].Remaining);
    }

    [Fact]
    public void FinishedVenture_IsReady()
    {
        var rows = VentureBoard.Compose(Now, [Retainer(complete: Now.AddMinutes(-1))]);
        Assert.Equal(VentureState.Ready, Assert.Single(rows).State);
    }

    [Fact]
    public void UnfinishedVenture_ReportsTimeLeft()
    {
        var rows = VentureBoard.Compose(Now, [Retainer(complete: Now.AddMinutes(20))]);
        Assert.Equal(VentureState.Running, Assert.Single(rows).State);
        Assert.Equal(TimeSpan.FromMinutes(20), rows[0].Remaining);
    }

    [Fact]
    public void UnloadedData_NeverReadsAsEmpty()
    {
        Assert.Equal("retainer data not loaded", VentureBoard.Summarize(false, []));
        Assert.Equal("no retainers", VentureBoard.Summarize(true, []));
    }

    [Fact]
    public void Summary_LeadsWithWhatIsActionable()
    {
        var rows = VentureBoard.Compose(Now, [
            Retainer("A", complete: Now.AddMinutes(-5)),
            Retainer("B", venture: 0),
            Retainer("C", complete: Now.AddMinutes(12)),
        ]);

        Assert.Equal("1 ready - 1 idle - next in 12m", VentureBoard.Summarize(true, rows));
    }

    [Fact]
    public void Summary_AdmitsUnknownTimers_RatherThanDroppingThem()
    {
        var rows = VentureBoard.Compose(Now, [Retainer("A", complete: null), Retainer("B", complete: null)]);
        Assert.Equal("2 timers unknown", VentureBoard.Summarize(true, rows));
    }

    [Fact]
    public void AnythingToDo_IsTrueOnlyForReadyOrIdle()
    {
        var busy = VentureBoard.Compose(Now, [Retainer(complete: Now.AddMinutes(30))]);
        Assert.False(VentureBoard.AnythingToDo(busy));

        var ready = VentureBoard.Compose(Now, [Retainer(complete: Now.AddMinutes(-30))]);
        Assert.True(VentureBoard.AnythingToDo(ready));

        var idle = VentureBoard.Compose(Now, [Retainer(venture: 0)]);
        Assert.True(VentureBoard.AnythingToDo(idle));
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(90, "1m")]
    [InlineData(3600, "1h 0m")]
    [InlineData(5400, "1h 30m")]
    public void Durations_AreGlanceable(int seconds, string expected) =>
        Assert.Equal(expected, VentureBoard.Describe(TimeSpan.FromSeconds(seconds)));
}
