using Charon.Features.AutoPillion;

namespace Charon.Tests.Features.AutoPillion;

/// <summary>
/// The picker must be deterministic across clients: same inputs → same assignment on every
/// machine, with no cross-client messaging. Candidates rank by name (ordinal, case-insensitive);
/// the k-th unmounted candidate takes the k-th free seat.
/// </summary>
public sealed class PassengerSeatPickerTests
{
    private static bool[] NoneOccupied(int seats) => new bool[seats];

    [Fact]
    public void FirstCandidateByName_GetsSeatOne()
    {
        var seat = PassengerSeatPicker.PickSeat("Arthena", new[] { "Kronos", "Arthena", "Selene" }, NoneOccupied(3));
        Assert.Equal(1, seat);
    }

    [Fact]
    public void RankIsByName_NotInputOrder()
    {
        // Same candidate set in two different input orders → identical assignment.
        var a = PassengerSeatPicker.PickSeat("Kronos", new[] { "Selene", "Kronos", "Arthena" }, NoneOccupied(3));
        var b = PassengerSeatPicker.PickSeat("Kronos", new[] { "Arthena", "Kronos", "Selene" }, NoneOccupied(3));
        Assert.Equal(2, a); // Arthena < Kronos < Selene
        Assert.Equal(a, b);
    }

    [Fact]
    public void EveryCandidate_GetsADistinctSeat()
    {
        var candidates = new[] { "Selene", "Arthena", "Kronos" };
        var seats = candidates
            .Select(self => PassengerSeatPicker.PickSeat(self, candidates, NoneOccupied(3)))
            .ToList();

        Assert.Equal(3, seats.Distinct().Count()); // no collisions
        Assert.All(seats, s => Assert.InRange(s!.Value, 1, 3));
    }

    [Fact]
    public void OccupiedSeats_AreSkipped()
    {
        var occupied = new[] { true, false, true, false }; // seats 1 and 3 taken
        var seat = PassengerSeatPicker.PickSeat("Arthena", new[] { "Arthena", "Kronos" }, occupied);
        Assert.Equal(2, seat); // first FREE seat

        var second = PassengerSeatPicker.PickSeat("Kronos", new[] { "Arthena", "Kronos" }, occupied);
        Assert.Equal(4, second);
    }

    [Fact]
    public void SelfNotACandidate_ReturnsNull()
    {
        Assert.Null(PassengerSeatPicker.PickSeat("Stranger", new[] { "Arthena", "Kronos" }, NoneOccupied(3)));
    }

    [Fact]
    public void MoreCandidatesThanFreeSeats_OverflowGetsNull()
    {
        var candidates = new[] { "Arthena", "Kronos", "Selene", "Zeno" };
        Assert.Null(PassengerSeatPicker.PickSeat("Zeno", candidates, NoneOccupied(3)));
    }

    [Fact]
    public void MatchingIsCaseInsensitive_AndDeduplicates()
    {
        var seat = PassengerSeatPicker.PickSeat("ARTHENA", new[] { "arthena", "Arthena", "Kronos" }, NoneOccupied(3));
        Assert.Equal(1, seat);
    }
}
