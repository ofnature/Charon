using System.Collections.Generic;

namespace Charon.Features.Retainers;

public enum RoundKind
{
    /// <summary>Open this retainer and run her cycle.</summary>
    Serve,

    /// <summary>The roster cannot be read right now — ask again, do not conclude anything.</summary>
    Wait,

    /// <summary>Nobody is left who needs collecting or sending.</summary>
    Done,
}

/// <summary>What a round does next. <paramref name="TaskId"/> is the venture to send her on (0 = as before).</summary>
public sealed record RoundStep(RoundKind Kind, string Name = "", uint TaskId = 0);

/// <summary>
/// "Collect and send every retainer": who is next. Pure — no Dalamud types.
///
/// A round is NOT a stored list of retainers to work through. That is the shape that makes
/// AutoRetainer lock a client until it has drained: a queue decided up front, with nothing asking
/// whether it still applies. Here the next retainer is chosen from the LIVE board every time the bell
/// is back at its list, so a retainer the player handled by hand is simply no longer due, and Stop has
/// nothing queued behind it to cancel.
///
/// Two rules keep that honest. A retainer is served at most ONCE per round (<c>served</c>): one the
/// game would not send stays due on the board, and without this the round would open her forever. And
/// an EMPTY board is never "done": the game reloads its retainer roster while one is being dismissed,
/// which reads as no retainers at all for a moment — the transient nothing this bell code has already
/// mistaken for a definitive none five times.
/// </summary>
public static class VentureRound
{
    /// <param name="includeIdle">Also send retainers who are sitting idle, not just collect the finished ones.</param>
    public static RoundStep Next(IReadOnlyList<VentureRow> board, IReadOnlyCollection<string> served, bool includeIdle)
    {
        if (board.Count == 0)
            return new RoundStep(RoundKind.Wait);

        foreach (var row in board)
        {
            var due = row.State == VentureState.Ready || (includeIdle && row.State == VentureState.Idle);
            if (due && !Contains(served, row.Name))
                return new RoundStep(RoundKind.Serve, row.Name);
        }

        return new RoundStep(RoundKind.Done);
    }

    private static bool Contains(IReadOnlyCollection<string> served, string name)
    {
        foreach (var done in served)
        {
            if (done == name)
                return true;
        }

        return false;
    }
}
