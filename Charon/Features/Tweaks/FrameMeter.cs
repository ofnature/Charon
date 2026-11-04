using System;

namespace Charon.Features.Tweaks;

/// <summary>
/// Frame rate and hitches, measured from the time between game frames. Every plugin runs once per
/// frame, so this is also how often the bots get to decide anything.
///
/// Figures are kept in one-second buckets and read relative to the LAST frame recorded, not to the
/// clock: the plugin keeps one meter for frames drawn while the window was focused and one for
/// frames drawn in the background, and whoever reads the Debug line has just focused the window —
/// the background meter has to keep showing what it saw before that.
/// </summary>
public sealed class FrameMeter
{
    /// <summary>A frame this long or longer can count as a hitch…</summary>
    public const double HitchMinMs = 100;

    /// <summary>…if it is also this many times longer than the frames before it, so a client capped
    /// at a low rate does not count every frame.</summary>
    public const double HitchFactor = 3;

    public const int RateSeconds = 5;
    public const int HistorySeconds = 60;

    private struct Bucket
    {
        public long Second;
        public int Frames;
        public double TotalMs;
        public double WorstMs;
        public int Hitches;
    }

    private readonly Bucket[] _buckets = new Bucket[HistorySeconds];
    private long _lastSecond = long.MinValue;

    public DateTime? LastSampleUtc { get; private set; }

    public void Record(double frameMs, DateTime nowUtc)
    {
        if (double.IsNaN(frameMs) || double.IsInfinity(frameMs) || frameMs <= 0)
            return;

        var second = nowUtc.Ticks / TimeSpan.TicksPerSecond;
        var average = AverageMs();
        // No recent frames = nothing to stall against, so the first frame is never a hitch.
        var hitch = frameMs >= HitchMinMs && average is { } avg && frameMs >= HitchFactor * avg;

        ref var bucket = ref _buckets[(int)(second % HistorySeconds)];
        if (bucket.Second != second)
            bucket = new Bucket { Second = second };

        bucket.Frames++;
        bucket.TotalMs += frameMs;
        bucket.WorstMs = Math.Max(bucket.WorstMs, frameMs);
        if (hitch)
            bucket.Hitches++;

        _lastSecond = second;
        LastSampleUtc = nowUtc;
    }

    /// <summary>Average frames per second over the last <see cref="RateSeconds"/> seconds recorded.</summary>
    public double? Fps() => AverageMs() is { } ms ? 1000.0 / ms : null;

    /// <summary>Longest frame in the last <see cref="HistorySeconds"/> seconds recorded.</summary>
    public double? WorstMs()
    {
        double? worst = null;
        foreach (var bucket in Live(HistorySeconds))
            worst = Math.Max(worst ?? 0, bucket.WorstMs);
        return worst;
    }

    /// <summary>Hitches in the last <see cref="HistorySeconds"/> seconds recorded.</summary>
    public int Hitches()
    {
        var count = 0;
        foreach (var bucket in Live(HistorySeconds))
            count += bucket.Hitches;
        return count;
    }

    /// <summary>"19 fps, worst 63 ms, 0 hitches/min", or null before any frame was recorded.</summary>
    public string? Describe() =>
        Fps() is { } fps
            ? $"{fps:0} fps, worst {WorstMs() ?? 0:0} ms, {Hitches()} hitch{(Hitches() == 1 ? "" : "es")}/min"
            : null;

    private double? AverageMs()
    {
        var frames = 0;
        var total = 0.0;
        foreach (var bucket in Live(RateSeconds))
        {
            frames += bucket.Frames;
            total += bucket.TotalMs;
        }

        return frames == 0 ? null : total / frames;
    }

    private System.Collections.Generic.IEnumerable<Bucket> Live(int seconds)
    {
        if (_lastSecond == long.MinValue)
            yield break;

        foreach (var bucket in _buckets)
            if (bucket.Frames > 0 && bucket.Second > _lastSecond - seconds && bucket.Second <= _lastSecond)
                yield return bucket;
    }
}
