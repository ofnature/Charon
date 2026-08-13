using System;

namespace Charon.Features.Leveling;

/// <summary>
/// The Doman Enclave weekly reset arithmetic. Pure logic — no Dalamud types.
///
/// The budget resets TUESDAY 08:00 UTC, per character. Tracking anchors to that instant, never
/// to a rolling seven days — a donation on Monday evening is spendable again Tuesday morning.
/// </summary>
public static class DonationWeek
{
    /// <summary>The most recent Tuesday 08:00 UTC at or before <paramref name="utcNow"/>.</summary>
    public static DateTime PreviousReset(DateTime utcNow)
    {
        var daysSinceTuesday = ((int)utcNow.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7;
        var reset = utcNow.Date.AddDays(-daysSinceTuesday).AddHours(8);
        return reset > utcNow ? reset.AddDays(-7) : reset;
    }

    /// <summary>Whether a donation recorded at <paramref name="lastDonationUtc"/> still counts —
    /// i.e. it happened after the most recent reset.</summary>
    public static bool HasDonatedThisWeek(DateTime? lastDonationUtc, DateTime utcNow) =>
        lastDonationUtc != null && lastDonationUtc.Value >= PreviousReset(utcNow);
}
