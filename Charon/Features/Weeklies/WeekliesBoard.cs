using System;
using System.Collections.Generic;
using Charon.Features.Leveling;

namespace Charon.Features.Weeklies;

/// <summary>
/// The weekly/daily to-do board: what this character can still spend before the next reset.
/// Pure logic — raw counts come in from the game adapters, line items with countdowns come out.
///
/// Reset instants are the game's fixed clock: weeklies (Doman budget, Custom Delivery allowances)
/// reset Tuesday 08:00 UTC (shared with <see cref="DonationWeek"/>); dailies (Allied Society
/// quest allowances) reset 15:00 UTC.
/// </summary>
public static class WeekliesBoard
{
    /// <summary>Custom Delivery allowances shared across all clients per week.</summary>
    public const int DeliveryAllowancesPerWeek = 12;

    /// <summary>Allied Society daily quest allowances shared across all tribes per day.</summary>
    public const int TribeAllowancesPerDay = 12;

    public enum ItemState
    {
        /// <summary>Nothing left to spend — this one is finished until its reset.</summary>
        Done,

        /// <summary>There is still something to do.</summary>
        Pending,

        /// <summary>The game hasn't given us an answer (state not loaded yet).</summary>
        Unknown,
    }

    public sealed record Item(string Name, string Detail, ItemState State, string Reset);

    /// <summary>The next Tuesday 08:00 UTC after <paramref name="utcNow"/>.</summary>
    public static DateTime NextWeeklyReset(DateTime utcNow) =>
        DonationWeek.PreviousReset(utcNow).AddDays(7);

    /// <summary>The next 15:00 UTC after <paramref name="utcNow"/>.</summary>
    public static DateTime NextDailyReset(DateTime utcNow)
    {
        var today = utcNow.Date.AddHours(15);
        return today > utcNow ? today : today.AddDays(1);
    }

    /// <summary>"2d 16h" / "16h 12m" / "12m" — coarse on purpose, it's a countdown not a timer.</summary>
    public static string FormatCountdown(TimeSpan until)
    {
        if (until < TimeSpan.Zero)
            until = TimeSpan.Zero;
        if (until.TotalDays >= 1)
            return $"{(int)until.TotalDays}d {until.Hours}h";
        if (until.TotalHours >= 1)
            return $"{(int)until.TotalHours}h {until.Minutes}m";
        return $"{Math.Max(1, until.Minutes)}m";
    }

    /// <summary>
    /// Build the board. Loaded flags are per-source because they genuinely differ: the Doman
    /// manager needs its sig resolved, the delivery arrays fill only once the client has fetched
    /// them, and the tribe allowance is available whenever a player is loaded.
    /// </summary>
    public static IReadOnlyList<Item> Compose(
        DateTime utcNow,
        bool domanLoaded, bool domanDone, int domanBudgetRemaining, bool domanCached,
        bool deliveriesLoaded, int deliveriesUsed,
        bool tribesLoaded, int tribeAllowanceLeft)
    {
        var weekly = $"in {FormatCountdown(NextWeeklyReset(utcNow) - utcNow)} (Tue)";
        var daily = $"in {FormatCountdown(NextDailyReset(utcNow) - utcNow)}";

        var items = new List<Item>();

        // The budget is denominated in gil (allowance minus donated so far) — show the number
        // when we have it, the same figure DailyDuty prints, rather than a bare "still open".
        // A cached figure (the client only sends enclave state near the enclave) says so.
        var cachedNote = domanCached ? " (from last visit)" : "";
        items.Add(!domanLoaded
            ? new Item("Doman donation", "state not readable — visit the enclave once", ItemState.Unknown, weekly)
            : domanDone
                ? new Item("Doman donation", $"donated this week{cachedNote}", ItemState.Done, weekly)
                : new Item("Doman donation",
                    (domanBudgetRemaining > 0 ? $"{domanBudgetRemaining:N0} gil remaining" : "budget still open")
                    + cachedNote,
                    ItemState.Pending, weekly));

        if (!deliveriesLoaded)
        {
            items.Add(new Item("Custom Deliveries", "not loaded yet (log in fully / unlock a client)",
                ItemState.Unknown, weekly));
        }
        else
        {
            var left = Math.Max(0, DeliveryAllowancesPerWeek - deliveriesUsed);
            items.Add(left == 0
                ? new Item("Custom Deliveries", $"all {DeliveryAllowancesPerWeek} used", ItemState.Done, weekly)
                : new Item("Custom Deliveries", $"{left} of {DeliveryAllowancesPerWeek} left", ItemState.Pending, weekly));
        }

        if (!tribesLoaded)
        {
            items.Add(new Item("Allied Society dailies", "not loaded yet", ItemState.Unknown, daily));
        }
        else
        {
            items.Add(tribeAllowanceLeft == 0
                ? new Item("Allied Society dailies", $"all {TribeAllowancesPerDay} used", ItemState.Done, daily)
                : new Item("Allied Society dailies", $"{tribeAllowanceLeft} of {TribeAllowancesPerDay} left", ItemState.Pending, daily));
        }

        return items;
    }
}
