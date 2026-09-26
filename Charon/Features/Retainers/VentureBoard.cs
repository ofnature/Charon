using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Retainers;

/// <summary>
/// One retainer's venture state as the game reports it. <paramref name="VentureId"/> 0 means no
/// venture is assigned; <paramref name="CompleteUtc"/> null means the game has not handed over a
/// timer yet, which is NOT the same as "ready" and must never be rendered as one.
/// </summary>
/// <param name="JobId">
/// The retainer's ClassJob row id — what decides which ventures it may run. Defaulted so the record
/// stays constructible in tests without a client.
/// </param>
public sealed record RetainerVenture(
    string Name,
    uint VentureId,
    DateTime? CompleteUtc,
    byte Level,
    byte ItemCount,
    uint Gil,
    byte JobId = 0);

/// <summary>What a retainer is doing right now. Unknown is a real answer, not a failure.</summary>
public enum VentureState
{
    /// <summary>No venture assigned — this one is sitting idle.</summary>
    Idle,

    /// <summary>Out on a venture that has not finished yet.</summary>
    Running,

    /// <summary>Venture finished and waiting to be collected.</summary>
    Ready,

    /// <summary>A venture is assigned but the game has not given us its timer.</summary>
    Unknown,
}

public sealed record VentureRow(string Name, VentureState State, TimeSpan? Remaining);

/// <summary>
/// Turns raw retainer reads into the venture board. Pure logic — no Dalamud types.
///
/// The point of this layer is that "I don't know" survives all the way to the UI. Retainer venture
/// timers are fetched lazily by the client, so a freshly logged-in box legitimately holds retainers
/// whose completion time is simply not known yet. Reporting those as ready would send you to a bell
/// for nothing; reporting them as running would invent a duration. They stay Unknown.
/// </summary>
public static class VentureBoard
{
    public static List<VentureRow> Compose(DateTime nowUtc, IEnumerable<RetainerVenture> retainers) =>
        retainers.Select(r =>
        {
            if (r.VentureId == 0)
                return new VentureRow(r.Name, VentureState.Idle, null);
            if (r.CompleteUtc == null)
                return new VentureRow(r.Name, VentureState.Unknown, null);

            var left = r.CompleteUtc.Value - nowUtc;
            return left <= TimeSpan.Zero
                ? new VentureRow(r.Name, VentureState.Ready, TimeSpan.Zero)
                : new VentureRow(r.Name, VentureState.Running, left);
        }).ToList();

    /// <summary>
    /// The one-line summary for the Debug line and the fleet board. Says what is actionable first
    /// (ready, then idle), then when the next one lands, and finally admits to any unknown timers
    /// instead of quietly leaving them out of the counts.
    /// </summary>
    public static string Summarize(bool loaded, IReadOnlyList<VentureRow> rows)
    {
        if (!loaded)
            return "retainer data not loaded";
        if (rows.Count == 0)
            return "no retainers";

        var parts = new List<string>();

        var ready = rows.Count(r => r.State == VentureState.Ready);
        if (ready > 0)
            parts.Add($"{ready} ready");

        var idle = rows.Count(r => r.State == VentureState.Idle);
        if (idle > 0)
            parts.Add($"{idle} idle");

        var next = rows.Where(r => r.State == VentureState.Running && r.Remaining != null)
            .Select(r => r.Remaining!.Value)
            .DefaultIfEmpty(TimeSpan.MinValue)
            .Min();
        if (next > TimeSpan.MinValue)
            parts.Add($"next in {Describe(next)}");

        var unknown = rows.Count(r => r.State == VentureState.Unknown);
        if (unknown > 0)
            parts.Add($"{unknown} timer{(unknown == 1 ? "" : "s")} unknown");

        return parts.Count == 0 ? $"{rows.Count} out on ventures" : string.Join(" - ", parts);
    }

    /// <summary>Whether anything is worth a trip to a bell right now.</summary>
    public static bool AnythingToDo(IReadOnlyList<VentureRow> rows) =>
        rows.Any(r => r.State is VentureState.Ready or VentureState.Idle);

    /// <summary>Coarse, glanceable durations — nobody needs seconds on a 40 minute venture.</summary>
    public static string Describe(TimeSpan left)
    {
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        if (left.TotalMinutes < 1)
            return $"{left.Seconds}s";
        if (left.TotalHours < 1)
            return $"{(int)left.TotalMinutes}m";
        return $"{(int)left.TotalHours}h {left.Minutes}m";
    }
}
