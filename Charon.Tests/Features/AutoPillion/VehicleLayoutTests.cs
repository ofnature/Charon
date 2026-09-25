using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.AutoPillion;

namespace Charon.Tests.Features.AutoPillion;

/// <summary>
/// The riders window paints whatever the layout returns, so these tests are what stands between a bad
/// drawing and the driver's screen: one slot per seat, every seat inside the vehicle, a shell ImGui can
/// actually fill (PathFillConvex needs a convex polygon), and seats that never overlap each other.
/// </summary>
public sealed class VehicleLayoutTests
{
    /// <summary>The capacities the Mount sheet actually contains (ExtraSeats 1 / 3 / 7).</summary>
    public static TheoryData<int> RealCapacities => new() { 1, 3, 7 };

    /// <summary>Plus capacities no mount has, which must still draw instead of throwing.</summary>
    public static TheoryData<int> AllCapacities => new() { 1, 2, 3, 4, 5, 6, 7, 8, 12 };

    [Theory]
    [InlineData(1, VehicleKind.Tandem)]
    [InlineData(3, VehicleKind.Car)]
    [InlineData(7, VehicleKind.Van)]
    public void Kind_FollowsThePassengerSeatCount(int passengerSeats, VehicleKind expected)
    {
        Assert.Equal(expected, VehicleLayout.For(passengerSeats).Kind);
    }

    [Theory]
    [MemberData(nameof(AllCapacities))]
    public void EverySeat_GetsExactlyOneSlot(int passengerSeats)
    {
        var layout = VehicleLayout.For(passengerSeats);

        Assert.Equal(passengerSeats + 1, layout.Slots.Count);
        Assert.Equal(passengerSeats, layout.PassengerSeats);
        Assert.Equal(Enumerable.Range(0, passengerSeats + 1), layout.Slots.Select(s => s.Seat));
    }

    [Theory]
    [MemberData(nameof(AllCapacities))]
    public void EverySeat_SitsInsideTheVehicle(int passengerSeats)
    {
        var layout = VehicleLayout.For(passengerSeats);

        foreach (var slot in layout.Slots)
        {
            Assert.True(Covered(layout, slot.Rect.Center), $"seat {slot.Seat} centre is off the vehicle");

            // A tandem is a machine you sit ON, so only the car and the van have a shell to sit inside.
            if (layout.Kind == VehicleKind.Tandem)
                continue;

            Assert.True(Contains(Shell(layout), new LayoutPoint(slot.Rect.X, slot.Rect.Y)), $"seat {slot.Seat} corner is off the body");
            Assert.True(Contains(Shell(layout), new LayoutPoint(slot.Rect.Right, slot.Rect.Bottom)), $"seat {slot.Seat} corner is off the body");
        }
    }

    [Theory]
    [MemberData(nameof(AllCapacities))]
    public void Seats_NeverOverlap(int passengerSeats)
    {
        var layout = VehicleLayout.For(passengerSeats);

        foreach (var a in layout.Slots)
        {
            foreach (var b in layout.Slots)
            {
                if (a.Seat >= b.Seat)
                    continue;

                var overlap = a.Rect.X < b.Rect.Right && b.Rect.X < a.Rect.Right &&
                              a.Rect.Y < b.Rect.Bottom && b.Rect.Y < a.Rect.Bottom;
                Assert.False(overlap, $"seats {a.Seat} and {b.Seat} overlap");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllCapacities))]
    public void Shell_IsConvex(int passengerSeats)
    {
        // The shell is filled with PathFillConvex: a concave outline would shade as garbage. The nose was
        // exactly that — a line meeting a curve at a shallow angle bends the outline the wrong way.
        var shell = Shell(VehicleLayout.For(passengerSeats));
        var sign = 0;

        for (var i = 0; i < shell.Count; i++)
        {
            var a = shell[i];
            var b = shell[(i + 1) % shell.Count];
            var c = shell[(i + 2) % shell.Count];
            var cross = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
            if (MathF.Abs(cross) < 0.001f)
                continue;

            var current = cross > 0f ? 1 : -1;
            if (sign == 0)
            {
                sign = current;
                continue;
            }

            Assert.True(current == sign, $"the shell bends the other way at vertex {i} — not convex");
        }

        Assert.NotEqual(0, sign);
    }

    [Theory]
    [MemberData(nameof(RealCapacities))]
    public void Drawing_StaysInTheBudgetedBounds(int passengerSeats)
    {
        var layout = VehicleLayout.For(passengerSeats);

        Assert.Equal(VehicleLayout.ModelWidth, layout.Width);
        Assert.InRange(layout.Height, 150f, 340f);
        Assert.True(layout.Slots.All(s => s.Rect.W >= 40f), "a seat narrower than 40 units cannot hold a name");
        Assert.True(layout.Slots.All(s => s.Rect.Bottom <= layout.Height), "a seat hangs off the bottom of the drawing");
        Assert.True(layout.SteerRadius > 0f);
    }

    [Theory]
    [MemberData(nameof(AllCapacities))]
    public void Driver_IsSeatedAtTheFrontLeft_WithTheControlCueAhead(int passengerSeats)
    {
        var layout = VehicleLayout.For(passengerSeats);
        var driver = layout.Slots[0].Rect;
        var frontRow = layout.Slots.Where(s => MathF.Abs(s.Rect.Y - driver.Y) < 0.001f).ToList();

        Assert.Equal(driver.Y, layout.Slots.Min(s => s.Rect.Y)); // the front row
        Assert.Equal(driver.X, frontRow.Min(s => s.Rect.X));     // and the left of it
        Assert.True(layout.Steer.Y < driver.Y, "the wheel must sit ahead of the driver, not behind");
        Assert.InRange(layout.Steer.X, driver.X, driver.Right);
    }

    [Fact]
    public void FourSeater_IsACar_WithTwoRowsOfTwo()
    {
        var layout = VehicleLayout.For(3);
        var rows = layout.Slots.GroupBy(s => s.Rect.Y).Select(g => g.Count()).ToList();

        Assert.Equal(VehicleKind.Car, layout.Kind);
        Assert.Equal(new[] { 2, 2 }, rows);
    }

    [Fact]
    public void EightSeater_IsThreeAbreast_SoItStaysShort()
    {
        var layout = VehicleLayout.For(7);
        var rows = layout.Slots.GroupBy(s => s.Rect.Y).Select(g => g.Count()).ToList();

        Assert.Equal(VehicleKind.Van, layout.Kind);
        Assert.Equal(new[] { 3, 3, 2 }, rows); // driver + two abreast, then three, then two
    }

    [Fact]
    public void TwoSeater_IsATandem_WithThePillionBehindTheRider()
    {
        var layout = VehicleLayout.For(1);

        Assert.Equal(VehicleKind.Tandem, layout.Kind);
        Assert.Equal(2, layout.Slots.Count);
        Assert.True(layout.Slots[1].Rect.Y > layout.Slots[0].Rect.Bottom - 2f, "the pillion seat sits behind the rider");
    }

    [Fact]
    public void MoreSeats_NeverDrawShorter()
    {
        var heights = new[] { 3, 4, 5, 6, 7, 8, 9, 12 }.Select(n => VehicleLayout.For(n).Height).ToList();

        for (var i = 1; i < heights.Count; i++)
            Assert.True(heights[i] >= heights[i - 1], $"capacity {i + 3} drew shorter than {i + 2}");

        Assert.True(VehicleLayout.For(12).Height > VehicleLayout.For(7).Height, "a twelve-seater is taller than the eight");
    }

    /// <summary>The shell: the body polygon with the largest area (the car and van have exactly one).</summary>
    private static IReadOnlyList<LayoutPoint> Shell(VehicleLayout layout) => layout.Shapes
        .Where(s => s.Part == VehiclePart.Body)
        .OrderByDescending(s => Area(s.Points))
        .First()
        .Points;

    /// <summary>Is any solid part of the vehicle under this point (body, tyres, glass)?</summary>
    private static bool Covered(VehicleLayout layout, LayoutPoint point) => layout.Shapes.Any(s =>
        s.Part is VehiclePart.Body or VehiclePart.Solid or VehiclePart.Glass or VehiclePart.Accent &&
        Contains(s.Points, point));

    private static float Area(IReadOnlyList<LayoutPoint> polygon)
    {
        var sum = 0f;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return MathF.Abs(sum) / 2f;
    }

    /// <summary>Ray casting: is the point inside the polygon?</summary>
    private static bool Contains(IReadOnlyList<LayoutPoint> polygon, LayoutPoint point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
