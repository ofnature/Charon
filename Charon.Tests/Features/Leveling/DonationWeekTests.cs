using System;
using Charon.Features.Leveling;

namespace Charon.Tests.Features.Leveling;

public sealed class DonationWeekTests
{
    // 2026-08-11 is a Tuesday.
    private static readonly DateTime TuesdayReset = new(2026, 8, 11, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MidWeek_ResetIsTheTuesdayBehindUs()
    {
        var wednesday = new DateTime(2026, 8, 12, 18, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TuesdayReset, DonationWeek.PreviousReset(wednesday));
    }

    [Fact]
    public void TuesdayBeforeEight_StillBelongsToLastWeek()
    {
        // 07:59 on Tuesday: the reset hasn't happened yet — the anchor is LAST Tuesday.
        var early = new DateTime(2026, 8, 11, 7, 59, 0, DateTimeKind.Utc);
        Assert.Equal(TuesdayReset.AddDays(-7), DonationWeek.PreviousReset(early));
    }

    [Fact]
    public void TuesdayAtEightExactly_IsTheNewWeek()
    {
        Assert.Equal(TuesdayReset, DonationWeek.PreviousReset(TuesdayReset));
    }

    [Fact]
    public void DonationAfterTheReset_CountsThisWeek()
    {
        var donated = new DateTime(2026, 8, 12, 19, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 8, 13, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(DonationWeek.HasDonatedThisWeek(donated, now));
    }

    [Fact]
    public void DonationBeforeTheReset_ExpiresOnTuesdayMorning()
    {
        // Donated Monday evening; by Tuesday 08:01 the budget is fresh — a rolling seven days
        // would wrongly say "already donated" here, which is why the anchor is the reset instant.
        var donated = new DateTime(2026, 8, 10, 22, 0, 0, DateTimeKind.Utc);
        var tuesdayMorning = new DateTime(2026, 8, 11, 8, 1, 0, DateTimeKind.Utc);
        Assert.False(DonationWeek.HasDonatedThisWeek(donated, tuesdayMorning));
    }

    [Fact]
    public void NeverDonated_IsNotThisWeek()
    {
        Assert.False(DonationWeek.HasDonatedThisWeek(null, DateTime.UtcNow));
    }
}
