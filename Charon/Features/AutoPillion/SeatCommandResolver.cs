using System;
using System.Collections.Generic;

namespace Charon.Features.AutoPillion;

/// <summary>An owner-issued seat assignment received over the LAN relay, with an expiry.</summary>
public sealed record SeatCommand(string OwnerName, int SeatIndex, DateTime ExpiresUtc);

/// <summary>
/// Decides which seat a passenger boards: an owner's COMMAND wins over the observation-based
/// picker, but only while it is fresh, addressed to the mount we're boarding, in range, and
/// the seat is actually free (live occupancy is authoritative — a stale command must degrade
/// to the picker, never block boarding). Pure logic, no Dalamud types.
/// </summary>
public static class SeatCommandResolver
{
    /// <param name="command">Latest command received for the local character (null = none).</param>
    /// <param name="ownerName">Owner of the mount we're about to board.</param>
    /// <param name="nowUtc">Current time (for expiry).</param>
    /// <param name="seatOccupied">Live per-seat occupancy, index 0 = seat 1.</param>
    /// <param name="pickerSeat">The observation-based fallback seat (null = picker found none).</param>
    public static int? Resolve(
        SeatCommand? command,
        string ownerName,
        DateTime nowUtc,
        IReadOnlyList<bool> seatOccupied,
        int? pickerSeat)
    {
        if (command == null
            || nowUtc >= command.ExpiresUtc
            || !command.OwnerName.Equals(ownerName, StringComparison.OrdinalIgnoreCase)
            || command.SeatIndex < 1
            || command.SeatIndex > seatOccupied.Count
            || seatOccupied[command.SeatIndex - 1])
            return pickerSeat;

        return command.SeatIndex;
    }
}
