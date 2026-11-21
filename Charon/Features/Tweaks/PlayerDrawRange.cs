using System;

namespace Charon.Features.Tweaks;

/// <summary>
/// The rule behind "hide other players beyond a distance": who is hidden, and when a hidden one
/// comes back. Coming back takes a little less distance than going away, because showing a
/// character again makes the game load its model — a player standing on the line would otherwise
/// be loaded and dropped several times a second.
/// </summary>
public static class PlayerDrawRange
{
    public const float MinRange = 5f;
    public const float MaxRange = 100f;
    public const float DefaultRange = 30f;

    /// <summary>A hidden player is shown again once this far inside the range.</summary>
    public const float Hysteresis = 3f;

    public static float Clamp(float range)
        => float.IsNaN(range) ? DefaultRange : Math.Clamp(range, MinRange, MaxRange);

    /// <summary>
    /// Whether a player this far away should be hidden. <paramref name="exempt"/> players (party,
    /// fleet, friends, the current target) are never hidden, and an unreadable distance hides nobody.
    /// </summary>
    public static bool ShouldHide(float distance, float range, bool hiddenNow, bool exempt)
    {
        if (exempt || float.IsNaN(distance))
            return false;

        range = Clamp(range);
        return hiddenNow ? distance > range - Hysteresis : distance > range;
    }
}
