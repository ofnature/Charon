namespace Charon.Features.Tweaks;

/// <summary>
/// Frames per second for the server info bar: frames counted over about a second of frame time,
/// so the number is steady enough to read and still follows a change within a second.
/// (<see cref="FrameMeter"/> averages over five seconds and keeps a history — right for the Debug
/// line, too slow for a live readout.)
/// </summary>
public sealed class FpsCounter
{
    public const double WindowMs = 1000;

    private int _frames;
    private double _elapsedMs;

    /// <summary>The last finished window's rate, or null before the first one.</summary>
    public double? Fps { get; private set; }

    /// <summary>Adds one frame. True when a window just finished and <see cref="Fps"/> changed.</summary>
    public bool Record(double frameMs)
    {
        if (double.IsNaN(frameMs) || frameMs <= 0)
            return false;

        _frames++;
        _elapsedMs += frameMs;
        if (_elapsedMs < WindowMs)
            return false;

        Fps = _frames * 1000.0 / _elapsedMs;
        _frames = 0;
        _elapsedMs = 0;
        return true;
    }
}
