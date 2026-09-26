using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Tweaks;

/// <summary>What the AFK guard wants to do this tick.</summary>
public enum AfkAction
{
    /// <summary>Nothing — the reason says why, in operator words.</summary>
    Idle,

    /// <summary>Send one keystroke to the game window, so the client counts input again.</summary>
    Nudge,
}

/// <param name="Reason">Always filled, including when idle: "idle 412s of 600s" is an answer, "waiting" is not.</param>
public sealed record AfkDecision(AfkAction Action, string Reason);

/// <summary>
/// Whether this client needs a nudge to stay logged in.
///
/// The client keeps its own idle accumulators — three of them, the AFK timer, the in-content input timer
/// and the general input timer — and logs you out when one runs too long. They reset on INPUT, which is
/// why a toon can be kicked while it looks busy: plugin-driven movement (a navmesh follow, a rotation)
/// writes to the game's own state and never touches the input path, so a box that has been following the
/// fleet for half an hour is, as far as the client is concerned, still idle.
///
/// Reading the client's own timers is what keeps this honest instead of being our own timer guessing:
/// we nudge when the CLIENT says it has been idle, and the guard reports the number it saw.
/// </summary>
public static class AfkGuardPolicy
{
    /// <summary>
    /// Don't nudge more often than this. One keystroke per idle stretch is the whole point; a burst of
    /// them means the keystroke is not landing, which is a thing to say out loud rather than paper over.
    /// </summary>
    public const double CooldownSeconds = 20;

    /// <summary>
    /// After this many nudges with the timer still climbing, stop and report it: a nudge that does not
    /// reset the timer means the keystroke is not reaching the client, and repeating it forever would
    /// hide the one symptom worth acting on.
    /// </summary>
    public const int NudgesBeforeGivingUp = 3;

    /// <summary>
    /// A floor on the configured threshold, because a hand-edited 0 or a negative would otherwise mean
    /// "nudge the moment any timer ticks over" — safe, but a keystroke every cooldown forever, and the
    /// settings slider's own range starts here anyway.
    /// </summary>
    public const int MinimumThresholdSeconds = 60;

    public static AfkDecision Decide(
        bool enabled,
        bool loggedIn,
        IReadOnlyList<double> timers,
        int thresholdSeconds,
        double? secondsSinceNudge,
        int stuckNudges)
    {
        if (!enabled)
            return new AfkDecision(AfkAction.Idle, "off");

        if (!loggedIn)
            return new AfkDecision(AfkAction.Idle, "not logged in");

        // A negative or NaN timer is a read that cannot be trusted, and nudging off it would be a guess.
        var usable = timers.Where(t => !double.IsNaN(t) && t >= 0).ToList();
        if (usable.Count == 0)
            return new AfkDecision(AfkAction.Idle, "the client's idle timers are not readable");

        var worst = usable.Max();
        var threshold = Math.Max(MinimumThresholdSeconds, thresholdSeconds);

        if (worst < threshold)
            return new AfkDecision(AfkAction.Idle, $"idle {worst:0}s of {threshold}s");

        if (stuckNudges >= NudgesBeforeGivingUp)
        {
            return new AfkDecision(AfkAction.Idle,
                $"idle {worst:0}s — {stuckNudges} nudges did not reset it, so the game window is not "
                + "taking keystrokes");
        }

        if (secondsSinceNudge is { } since && since < CooldownSeconds)
            return new AfkDecision(AfkAction.Idle, $"nudged {since:0}s ago, idle {worst:0}s");

        return new AfkDecision(AfkAction.Nudge, $"idle {worst:0}s — sending a keystroke");
    }
}
