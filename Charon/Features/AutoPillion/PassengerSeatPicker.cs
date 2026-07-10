using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.AutoPillion;

/// <summary>
/// Deterministic passenger-side seat selection. Every passenger client runs this independently
/// with the same inputs (trusted candidate names, current seat occupancy) and arrives at a
/// non-colliding assignment WITHOUT any cross-client messaging: candidates are ranked by
/// name (ordinal, case-insensitive — identical on every machine), and the k-th unmounted
/// candidate takes the k-th free seat.
/// </summary>
public static class PassengerSeatPicker
{
    /// <summary>
    /// Pick the seat the local toon should ride, or null when it should not board
    /// (not a candidate, or not enough free seats).
    /// </summary>
    /// <param name="selfName">Local character name.</param>
    /// <param name="unmountedCandidates">Trusted candidates not currently riding (owner excluded).</param>
    /// <param name="seatOccupied">Per passenger seat, index 0 = seat 1; true = taken.</param>
    public static int? PickSeat(string selfName, IEnumerable<string> unmountedCandidates, IReadOnlyList<bool> seatOccupied)
    {
        if (string.IsNullOrWhiteSpace(selfName))
            return null;

        var ordered = unmountedCandidates
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rank = ordered.FindIndex(n => n.Equals(selfName, StringComparison.OrdinalIgnoreCase));
        if (rank < 0)
            return null;

        var freeSeats = new List<int>();
        for (var i = 0; i < seatOccupied.Count; i++)
        {
            if (!seatOccupied[i])
                freeSeats.Add(i + 1);
        }

        return rank < freeSeats.Count ? freeSeats[rank] : null;
    }
}
