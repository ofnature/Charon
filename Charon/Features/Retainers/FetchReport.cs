using System;
using System.Text.Json;

namespace Charon.Features.Retainers;

/// <summary>
/// What a fetch is doing, as a VALUE a caller can branch on. Pure — no Dalamud types.
///
/// The prose status line stays (a person reads it), but a consumer parsing sentences breaks the
/// moment the wording improves. Hephaestus's planner needs exactly one distinction that prose
/// cannot carry safely: "this is progressing" versus "this is waiting for a human to walk to a
/// bell", because the second is when it should pause its queue and say so.
/// </summary>
public enum FetchState
{
    /// <summary>Nothing armed.</summary>
    Idle,

    /// <summary>Armed, but the retainer it needs is not open — somebody has to go to a bell.</summary>
    WaitingForPerson,

    /// <summary>The retainer is open and stacks are moving.</summary>
    Moving,

    /// <summary>The last pass finished. <see cref="FetchReport.MovedNq"/>/Hq say what it landed.</summary>
    Done,

    /// <summary>The store said it was not possible, or the pass gave up. Reason says why.</summary>
    Refused,
}

/// <summary>One fetch's state, in the shape the IPC publishes.</summary>
public sealed record FetchReport(
    FetchState State,
    string Retainer,
    uint ItemId,
    int Wanted,
    int MovedNq,
    int MovedHq,
    int Queued,
    string Reason)
{
    public static readonly FetchReport Idle =
        new(FetchState.Idle, string.Empty, 0, 0, 0, 0, 0, "idle");

    /// <summary>Total units actually delivered — the only number that says whether it worked.</summary>
    public int Moved => MovedNq + MovedHq;

    /// <summary>
    /// The wire form. <c>state</c> is camelCase so it reads the same as every other Charon payload,
    /// and it is the ONLY field a caller should branch on; the rest is detail for logs and UI.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        state = State switch
        {
            FetchState.WaitingForPerson => "waitingForPerson",
            FetchState.Moving => "moving",
            FetchState.Done => "done",
            FetchState.Refused => "refused",
            _ => "idle",
        },
        retainer = Retainer,
        itemId = ItemId,
        wanted = Wanted,
        movedNq = MovedNq,
        movedHq = MovedHq,
        moved = Moved,
        queued = Queued,
        reason = Reason,
    });
}
