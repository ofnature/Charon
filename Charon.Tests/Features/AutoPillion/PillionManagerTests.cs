using Charon.Features.AutoPillion;

namespace Charon.Tests.Features.AutoPillion;

/// <summary>
/// Seat numbering matches the game's Ride Pillion menu: passenger seats 1..N-1 for an
/// N-person mount (Mount Seat #1–#3 on a 4-seater, #1–#7 on an 8-seater). The owner rides
/// the implicit last spot and is never assigned.
/// </summary>
public sealed class PillionManagerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PillionConfig DefaultConfig(bool lanOnly = false) =>
        new(Enabled: true, PillionDelaySeconds: 1.5f, SeatTimeoutSeconds: 5.0f, LanMembersOnly: lanOnly);

    private static PillionCandidate Lan(string name, int order) => new(name, "Ultros", true, order);
    private static PillionCandidate Manual(string name) => new(name, "Gilgamesh", false, 0);

    private static (PillionManager manager, List<PillionInvite> invites) Create(
        PillionConfig? config = null)
    {
        var invites = new List<PillionInvite>();
        var manager = new PillionManager(config ?? DefaultConfig(), invites.Add);
        return (manager, invites);
    }

    /// <summary>Mount + wait out the invite delay so assignment can run.</summary>
    private static DateTime MountAndSettle(PillionManager manager, int passengerSeats, DateTime t0)
    {
        manager.OnMounted(1u, passengerSeats, t0);
        var afterDelay = t0.AddSeconds(2);
        manager.Update(afterDelay);
        return afterDelay;
    }

    // --- Seat ordering & assignment ---

    [Fact]
    public void Assignment_StartsAtSeatOne()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        MountAndSettle(manager, passengerSeats: 3, T0); // 4-person mount

        var invite = Assert.Single(invites);
        Assert.Equal(1, invite.SeatIndex);
    }

    [Fact]
    public void Assignment_FillsSeatsInAscendingOrder()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0), Lan("Kronos", 1), Lan("Selene", 2) });
        MountAndSettle(manager, passengerSeats: 3, T0);

        Assert.Equal(new[] { 1, 2, 3 }, invites.Select(i => i.SeatIndex));
    }

    [Fact]
    public void Assignment_EightPersonMount_UsesSeatsOneThroughSeven()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(Enumerable.Range(0, 9).Select(i => Lan($"Toon{i}", i)));
        MountAndSettle(manager, passengerSeats: 7, T0); // 8-person mount

        Assert.Equal(7, invites.Count); // two extra candidates wait
        Assert.Equal(Enumerable.Range(1, 7), invites.Select(i => i.SeatIndex));
    }

    [Fact]
    public void Assignment_LanMembersOrderedByToonIndex_BeforeManualEntries()
    {
        var (manager, invites) = Create();
        // Deliberately shuffled input: manual first, LAN out of order.
        manager.SetCandidates(new[] { Manual("OldFriend"), Lan("Kronos", 1), Lan("Arthena", 0) });
        MountAndSettle(manager, passengerSeats: 3, T0);

        Assert.Equal(new[] { "Arthena", "Kronos", "OldFriend" }, invites.Select(i => i.CharacterName));
    }

    [Fact]
    public void Assignment_NoDuplicateInvitesForSameCandidate()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0), Lan("Arthena", 1) });
        var now = MountAndSettle(manager, passengerSeats: 7, T0);
        manager.Update(now.AddSeconds(0.5));
        manager.Update(now.AddSeconds(1.0));

        Assert.Single(invites);
    }

    [Fact]
    public void Assignment_MoreCandidatesThanSeats_ExtrasWait()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0), Lan("Kronos", 1), Lan("Selene", 2) });
        MountAndSettle(manager, passengerSeats: 2, T0); // only seats 1 and 2

        Assert.Equal(2, invites.Count);
    }

    [Fact]
    public void LanMembersOnly_ExcludesManualEntries()
    {
        var (manager, invites) = Create(DefaultConfig(lanOnly: true));
        manager.SetCandidates(new[] { Manual("OldFriend"), Lan("Arthena", 0) });
        MountAndSettle(manager, passengerSeats: 3, T0);

        var invite = Assert.Single(invites);
        Assert.Equal("Arthena", invite.CharacterName);
    }

    [Fact]
    public void Disabled_NoSessionStarts()
    {
        var config = DefaultConfig() with { Enabled = false };
        var (manager, invites) = Create(config);
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        MountAndSettle(manager, passengerSeats: 3, T0);

        Assert.False(manager.SessionActive);
        Assert.Empty(invites);
    }

    // --- Invite delay ---

    [Fact]
    public void InviteDelay_NoInvitesBeforeDelayElapses()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        manager.OnMounted(1u, 3, T0);

        manager.Update(T0.AddSeconds(1.0)); // delay is 1.5s
        Assert.Empty(invites);

        manager.Update(T0.AddSeconds(1.6));
        Assert.Single(invites);
    }

    // --- Timeout handling ---

    [Fact]
    public void Timeout_MarksSeatDeclined_AndNeverReinvitesThatSeat()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        var now = MountAndSettle(manager, passengerSeats: 3, T0);

        var expired = now.AddSeconds(6); // timeout is 5s
        manager.Update(expired);

        var seat1 = manager.Seats.Single(s => s.Index == 1);
        Assert.Equal(SeatStatus.Declined, seat1.Status);

        // Candidate got exactly one retry — on a DIFFERENT seat.
        Assert.Equal(2, invites.Count);
        Assert.Equal(2, invites[1].SeatIndex);
    }

    [Fact]
    public void Timeout_TwiceDropsCandidateForSession()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        var now = MountAndSettle(manager, passengerSeats: 7, T0);

        manager.Update(now.AddSeconds(6));  // first timeout → retry on seat 2
        manager.Update(now.AddSeconds(12)); // second timeout → dropped
        manager.Update(now.AddSeconds(18)); // no further invites

        Assert.Equal(2, invites.Count);
        Assert.Equal(2, manager.Seats.Count(s => s.Status == SeatStatus.Declined));
    }

    [Fact]
    public void PendingSeat_NotExpiredBeforeTimeout()
    {
        var (manager, _) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        var now = MountAndSettle(manager, passengerSeats: 3, T0);

        manager.Update(now.AddSeconds(4)); // under the 5s timeout

        var seat1 = manager.Seats.Single(s => s.Index == 1);
        Assert.Equal(SeatStatus.InvitePending, seat1.Status);
    }

    // --- Accept handling ---

    [Fact]
    public void Accept_MarksSeatFilled_RemovesFromPending()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        var now = MountAndSettle(manager, passengerSeats: 3, T0);

        manager.OnSeatOccupied(1, "Arthena");
        manager.Update(now.AddSeconds(10)); // way past timeout — must NOT decline a filled seat

        var seat1 = manager.Seats.Single(s => s.Index == 1);
        Assert.Equal(SeatStatus.Filled, seat1.Status);
        Assert.Single(invites); // no re-invite for a seated candidate
        Assert.Equal(1, manager.SeatsFilled);
    }

    [Fact]
    public void Accept_OnDifferentSeat_ReleasesTheInvitedSeat()
    {
        var (manager, _) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        MountAndSettle(manager, passengerSeats: 3, T0);

        // Invited to seat 1, but they hopped onto seat 3 themselves.
        manager.OnSeatOccupied(3, "Arthena");

        Assert.Equal(SeatStatus.Available, manager.Seats.Single(s => s.Index == 1).Status);
        Assert.Equal(SeatStatus.Filled, manager.Seats.Single(s => s.Index == 3).Status);
    }

    // --- Seat reopening ---

    [Fact]
    public void SeatVacated_ReassignsToPendingCandidate()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0), Lan("Kronos", 1), Lan("Selene", 2) });
        var now = MountAndSettle(manager, passengerSeats: 2, T0); // seats 1,2 — Selene waits

        manager.OnSeatOccupied(1, "Arthena");
        manager.OnSeatOccupied(2, "Kronos");
        manager.OnSeatVacated(2); // Kronos hops off
        manager.Update(now.AddSeconds(1));

        Assert.Equal(3, invites.Count);
        Assert.Equal("Selene", invites[2].CharacterName);
        Assert.Equal(2, invites[2].SeatIndex);
    }

    // --- Dismount ---

    [Fact]
    public void Dismount_ClearsAllState()
    {
        var (manager, _) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        MountAndSettle(manager, passengerSeats: 3, T0);
        manager.OnSeatOccupied(1, "Arthena");

        manager.OnDismounted();

        Assert.False(manager.SessionActive);
        Assert.Empty(manager.Seats);
        Assert.Equal(0, manager.SeatsFilled);
        Assert.Equal(0u, manager.MountId);
        Assert.Equal(0, manager.PassengerSeats);
    }

    [Fact]
    public void Remount_AfterDismount_StartsFreshSession()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        var now = MountAndSettle(manager, passengerSeats: 3, T0);
        manager.Update(now.AddSeconds(6)); // Arthena times out on seat 1 (+retry seat 2)
        manager.OnDismounted();

        // New session: declined seats and attempt counts must not carry over.
        MountAndSettle(manager, passengerSeats: 3, now.AddSeconds(10));

        Assert.Equal(1, invites[^1].SeatIndex); // seat 1 is invitable again
    }

    [Fact]
    public void SoloMount_NoSession()
    {
        var (manager, invites) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        MountAndSettle(manager, passengerSeats: 0, T0); // single-seater: no passenger seats

        Assert.False(manager.SessionActive);
        Assert.Empty(invites);
    }

    [Fact]
    public void MidSessionDisable_EndsSession()
    {
        var (manager, _) = Create();
        manager.SetCandidates(new[] { Lan("Arthena", 0) });
        var now = MountAndSettle(manager, passengerSeats: 3, T0);

        manager.UpdateConfig(DefaultConfig() with { Enabled = false });
        manager.Update(now.AddSeconds(1));

        Assert.False(manager.SessionActive);
    }
}
