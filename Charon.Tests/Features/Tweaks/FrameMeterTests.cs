using System;
using Charon.Features.Tweaks;

namespace Charon.Tests.Features.Tweaks;

public sealed class FrameMeterTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Records frames of <paramref name="ms"/> for <paramref name="seconds"/> of game time.</summary>
    private static DateTime Run(FrameMeter meter, DateTime from, double ms, double seconds)
    {
        var at = from;
        var end = from.AddSeconds(seconds);
        while (at < end)
        {
            at = at.AddMilliseconds(ms);
            meter.Record(ms, at);
        }

        return at;
    }

    [Fact]
    public void NothingRecorded_SaysNothing()
    {
        var meter = new FrameMeter();
        Assert.Null(meter.Fps());
        Assert.Null(meter.Describe());
        Assert.Equal(0, meter.Hitches());
    }

    [Fact]
    public void SteadyFrames_GiveTheirRate()
    {
        var meter = new FrameMeter();
        Run(meter, Start, 1000.0 / 60, 10);
        Assert.InRange(meter.Fps()!.Value, 59.5, 60.5);
        Assert.Equal(0, meter.Hitches());
    }

    [Fact]
    public void OneLongFrame_IsAHitch_AndTheWorstFrame()
    {
        var meter = new FrameMeter();
        var at = Run(meter, Start, 1000.0 / 60, 10);
        meter.Record(250, at.AddMilliseconds(250));

        Assert.Equal(1, meter.Hitches());
        Assert.Equal(250, meter.WorstMs());
    }

    [Fact]
    public void AClientCappedLow_DoesNotCountEveryFrameAsAHitch()
    {
        // 8 fps = 125 ms frames: every one is over the hitch floor, none is a stall.
        var meter = new FrameMeter();
        Run(meter, Start, 125, 20);
        Assert.Equal(0, meter.Hitches());
        Assert.InRange(meter.Fps()!.Value, 7.9, 8.1);
    }

    [Fact]
    public void AShortStutter_IsNotAHitch()
    {
        var meter = new FrameMeter();
        var at = Run(meter, Start, 1000.0 / 60, 10);
        meter.Record(60, at.AddMilliseconds(60)); // under the floor
        Assert.Equal(0, meter.Hitches());
    }

    [Fact]
    public void HitchesOlderThanAMinute_AreForgotten()
    {
        var meter = new FrameMeter();
        var at = Run(meter, Start, 1000.0 / 60, 5);
        meter.Record(300, at = at.AddMilliseconds(300));
        Assert.Equal(1, meter.Hitches());

        Run(meter, at, 1000.0 / 60, FrameMeter.HistorySeconds + 2);
        Assert.Equal(0, meter.Hitches());
        Assert.True(meter.WorstMs() < 20);
    }

    [Fact]
    public void FiguresHold_WhenFramesStopArriving()
    {
        // The background meter stops recording the moment someone focuses the window to read the
        // Debug line; it must still show what it saw, not decay to nothing.
        var meter = new FrameMeter();
        Run(meter, Start, 50, 10);
        var before = meter.Fps();
        Assert.Equal(before, meter.Fps());
        Assert.InRange(before!.Value, 19.9, 20.1);
    }

    [Fact]
    public void RateFollowsTheLastFewSeconds()
    {
        var meter = new FrameMeter();
        var at = Run(meter, Start, 1000.0 / 144, 10);
        Run(meter, at, 50, FrameMeter.RateSeconds + 1);
        Assert.InRange(meter.Fps()!.Value, 19.5, 20.5);
    }

    [Fact]
    public void BadSamples_AreIgnored()
    {
        var meter = new FrameMeter();
        meter.Record(0, Start);
        meter.Record(-5, Start);
        meter.Record(double.NaN, Start);
        Assert.Null(meter.Fps());
    }

    [Fact]
    public void Describe_ReadsPlainly()
    {
        var meter = new FrameMeter();
        Run(meter, Start, 50, 10);
        Assert.Equal("20 fps, worst 50 ms, 0 hitches/min", meter.Describe());
    }
}
