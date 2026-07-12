using Charon.Features.AutoPillion;

namespace Charon.Tests.Features.AutoPillion;

public sealed class SeatCommandResolverTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly bool[] NoneOccupied = new bool[3];

    private static SeatCommand Command(string owner = "Korha Ishere", int seat = 2, double ttl = 10) =>
        new(owner, seat, T0.AddSeconds(ttl));

    [Fact]
    public void FreshCommand_OverridesPicker()
    {
        var seat = SeatCommandResolver.Resolve(Command(seat: 2), "Korha Ishere", T0, NoneOccupied, pickerSeat: 1);
        Assert.Equal(2, seat);
    }

    [Fact]
    public void ExpiredCommand_FallsBackToPicker()
    {
        var seat = SeatCommandResolver.Resolve(Command(ttl: 5), "Korha Ishere", T0.AddSeconds(6), NoneOccupied, 1);
        Assert.Equal(1, seat);
    }

    [Fact]
    public void CommandForDifferentOwner_Ignored()
    {
        var seat = SeatCommandResolver.Resolve(Command(owner: "Someone Else"), "Korha Ishere", T0, NoneOccupied, 1);
        Assert.Equal(1, seat);
    }

    [Fact]
    public void OwnerMatch_IsCaseInsensitive()
    {
        var seat = SeatCommandResolver.Resolve(Command(owner: "KORHA ISHERE", seat: 3), "Korha Ishere", T0, NoneOccupied, 1);
        Assert.Equal(3, seat);
    }

    [Fact]
    public void OccupiedCommandedSeat_DegradesToPicker()
    {
        var occupied = new[] { false, true, false }; // seat 2 taken
        var seat = SeatCommandResolver.Resolve(Command(seat: 2), "Korha Ishere", T0, occupied, 3);
        Assert.Equal(3, seat);
    }

    [Fact]
    public void OutOfRangeCommandedSeat_DegradesToPicker()
    {
        var seat = SeatCommandResolver.Resolve(Command(seat: 7), "Korha Ishere", T0, NoneOccupied, 1);
        Assert.Equal(1, seat); // 3 passenger seats — commanded 7 is nonsense for this mount
    }

    [Fact]
    public void NoCommand_UsesPicker_IncludingNull()
    {
        Assert.Equal(1, SeatCommandResolver.Resolve(null, "Korha Ishere", T0, NoneOccupied, 1));
        Assert.Null(SeatCommandResolver.Resolve(null, "Korha Ishere", T0, NoneOccupied, null));
    }
}
