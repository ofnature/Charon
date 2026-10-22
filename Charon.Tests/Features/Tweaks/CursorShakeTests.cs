using System.Numerics;
using Charon.Features.Tweaks;

namespace Charon.Tests.Features.Tweaks;

public sealed class CursorShakeTests
{
    private const double Frame60 = 1.0 / 60.0;

    /// <summary>Back and forth by <paramref name="stroke"/> pixels, <paramref name="frames"/> times.</summary>
    private static void Shake(CursorShake shake, int frames, float stroke, double frameSeconds = Frame60)
    {
        for (var i = 0; i < frames; i++)
            shake.Update(new Vector2(i % 2 == 0 ? 500 : 500 + stroke, 400), frameSeconds);
    }

    [Fact]
    public void AVigorousShake_TurnsItOn()
    {
        var shake = new CursorShake();
        Shake(shake, 30, 200);
        Assert.True(shake.Active);
        // Once on, more shaking adds nothing (the original's rule) — it fades from full strength.
        Assert.True(shake.Power > 0);
    }

    [Fact]
    public void OrdinaryMovement_NeverTurnsItOn()
    {
        // A slow drag across the screen: 5 pixels a frame for ten seconds.
        var shake = new CursorShake();
        for (var i = 0; i < 600; i++)
            shake.Update(new Vector2(i * 5, 400), Frame60);
        Assert.False(shake.Active);
    }

    [Fact]
    public void ItTurnsOffAgain_OnceTheShakingStops()
    {
        var shake = new CursorShake();
        Shake(shake, 30, 200);
        Assert.True(shake.Active);

        for (var i = 0; i < 120; i++)
            shake.Update(new Vector2(500, 400), Frame60);
        Assert.False(shake.Active);
        Assert.Equal(0, shake.Power);
    }

    [Fact]
    public void TheSameShake_TriggersAtAnyFrameRate()
    {
        // Half a second of shaking at 200 px strokes, at 60 and at 144 fps. Per-frame decay (the original)
        // made the 144 fps case drain more than twice as fast; per-time decay treats them alike.
        var at60 = new CursorShake();
        Shake(at60, 30, 200);

        var at144 = new CursorShake();
        Shake(at144, 72, 200 * 60f / 144f, 1.0 / 144.0); // same speed of hand, smaller steps

        Assert.True(at60.Active);
        Assert.True(at144.Active);
    }

    [Fact]
    public void TheFirstSample_OnlySetsTheStart()
    {
        var shake = new CursorShake();
        shake.Update(new Vector2(0, 0), Frame60);
        shake.Update(new Vector2(0, 0), Frame60);
        Assert.False(shake.Active);
    }
}
