using System;
using System.Numerics;

namespace Charon.Features.Tweaks;

/// <summary>
/// "Shake the mouse to find it": every bit of cursor movement adds its distance to a counter that keeps
/// draining away, so only a lot of movement in a short time — a shake — pushes it over the top threshold
/// and turns the indicator on; once the shaking stops it drains below the bottom threshold and turns off
/// again. <see cref="Power"/> (0-1) sizes and fades the indicator as it drains. Pure — no Dalamud types.
///
/// Ported from Where's Mouse? by Paissa Heavy Industries (MIT, paissaheavyindustries/WheresMouse —
/// <c>Wherenator</c>), with its defaults: thresholds 100 / 1500 pixels, drain ×0.95 per frame while off and
/// ×0.9 while on. ONE CHANGE: the original drains once per FRAME, so how hard you must shake depended on
/// the frame rate (at 144 fps the counter drains more than twice as fast as at 60). Here the drain is per
/// elapsed time, scaled so it matches the original exactly at 60 fps.
/// </summary>
public sealed class CursorShake
{
    public const double OffBelow = 100.0;
    public const double OnAbove = 1500.0;

    /// <summary>The original's per-frame factors, applied as if every frame were 1/60 s.</summary>
    public const double DrainWhileOff = 0.95;
    public const double DrainWhileOn = 0.9;
    private const double ReferenceFrameSeconds = 1.0 / 60.0;

    private Vector2? _last;
    private double _accumulated;

    public bool Active { get; private set; }

    /// <summary>0 when the indicator is just turning off, 1 at full strength.</summary>
    public double Power { get; private set; }

    public void Update(Vector2 cursor, double elapsedSeconds)
    {
        if (_last is not { } last)
        {
            _last = cursor;
            return;
        }

        var frames = Math.Max(0.0, elapsedSeconds) / ReferenceFrameSeconds;

        if (Active)
        {
            if (_accumulated < OffBelow)
                Active = false;
        }
        else
        {
            _accumulated += Vector2.Distance(cursor, last);
            if (_accumulated >= OnAbove)
                Active = true;
        }

        if (Active)
        {
            var capped = Math.Min(_accumulated, OnAbove);
            Power = Math.Clamp((capped - OffBelow) / (OnAbove - OffBelow), 0.0, 1.0);
            _accumulated *= Math.Pow(DrainWhileOn, frames);
        }
        else
        {
            Power = 0;
            _accumulated *= Math.Pow(DrainWhileOff, frames);
        }

        _last = cursor;
    }
}
