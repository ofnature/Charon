using System;
using System.Collections.Generic;

namespace Charon.Features.AutoPillion;

/// <summary>Which vehicle the seat map is drawn as. Picked from the passenger-seat count alone.</summary>
public enum VehicleKind
{
    /// <summary>Two seats, one behind the other: the rider and a single pillion passenger.</summary>
    Tandem,

    /// <summary>Two rows of two — the four-person mount, drawn as a car.</summary>
    Car,

    /// <summary>Three abreast, three rows — the eight-person mount.</summary>
    Van,
}

/// <summary>How a drawn shape is painted; the window maps each part onto the palette.</summary>
public enum VehiclePart
{
    /// <summary>The shell: filled slate, stroked with the border colour.</summary>
    Body,

    /// <summary>A solid dark part that is not the shell — tyres, mirrors, fork, tank.</summary>
    Solid,

    /// <summary>Windows: a cyan wash with a faint edge.</summary>
    Glass,

    /// <summary>Nose lamps (amber).</summary>
    HeadLamp,

    /// <summary>Tail lamps (rose).</summary>
    TailLamp,

    /// <summary>Identity wash with no edge — side glass, the whale-style glow.</summary>
    Accent,

    /// <summary>Stroke-only structural line; <see cref="VehicleShape.Thickness"/> sets the width.</summary>
    Trim,
}

/// <summary>An axis-aligned rectangle in layout units (200 units across the drawing's width).</summary>
public readonly record struct LayoutRect(float X, float Y, float W, float H)
{
    public float Right => X + W;

    public float Bottom => Y + H;

    public LayoutPoint Center => new(X + W / 2f, Y + H / 2f);
}

/// <summary>A point in layout units.</summary>
public readonly record struct LayoutPoint(float X, float Y);

/// <summary>
/// One polygon of a drawing, in draw order. Four-point axis-aligned shapes are rectangles; anything
/// else is a path (the body shell, the tank).
/// </summary>
/// <param name="Part">Determines the colours the window paints it with.</param>
/// <param name="Points">Vertices in layout units; every shape is convex so it can be filled directly.</param>
/// <param name="Closed">False for open Trim lines.</param>
/// <param name="Thickness">Stroke width in layout units; 0 means the part's default.</param>
public sealed record VehicleShape(
    VehiclePart Part,
    IReadOnlyList<LayoutPoint> Points,
    bool Closed = true,
    float Thickness = 0f);

/// <summary>
/// A seat in the drawing. <see cref="Seat"/> 0 is the DRIVER — the local player, who owns the mount and
/// rides its implicit last spot; 1..N are the passenger seats, numbered exactly as the game numbers them
/// (<c>Character.ModeParam</c> while riding pillion), so a seat's number here is the modus the boarding
/// logic and the Ride Pillion menu use.
/// </summary>
public readonly record struct SeatSlot(int Seat, LayoutRect Rect);

/// <summary>
/// The top-down seat map of a multi-seat mount, as pure geometry: no Dalamud types, so the window only
/// paints what this returns and the drawing itself can be unit-tested.
///
/// The shape is chosen from the passenger-seat count alone — the only thing the game tells us
/// (<c>Mount.ExtraSeats</c>, via <see cref="Services.Game.MountStateReader"/>): 1 on a 2-seater, 3 on a
/// 4-seater, 7 on the 8-seater. Anything else falls through the same row builder, so an unfamiliar
/// capacity still draws something sane instead of throwing in a UI path.
///
/// Every drawing lives in a 200-unit-wide space; the window scales it to pixels.
/// </summary>
/// <param name="Kind">Which vehicle was chosen.</param>
/// <param name="Width">Drawing width in layout units (always 200 — the model's width).</param>
/// <param name="Height">Drawing height in layout units; multiply by the window's scale for pixels.</param>
/// <param name="Slots">Driver first, then the passenger seats in row-major order.</param>
/// <param name="Shapes">Polygons to paint, back to front.</param>
/// <param name="Steer">Where the control cue sits: the wheel ahead of the driver's cushion.</param>
/// <param name="SteerRadius">Radius of that cue, in layout units.</param>
public sealed record VehicleLayout(
    VehicleKind Kind,
    float Width,
    float Height,
    IReadOnlyList<SeatSlot> Slots,
    IReadOnlyList<VehicleShape> Shapes,
    LayoutPoint Steer,
    float SteerRadius)
{
    /// <summary>Model width every drawing is built in.</summary>
    public const float ModelWidth = 200f;

    private const float Nose = 10f;
    private const float Windshield = 15f;
    private const float RowHeight = 56f;
    private const float SeatHeight = 46f;
    private const float RearGlass = 15f;
    private const float Tail = 10f;
    private const float SeatInset = 5f;
    // Three abreast has to fit inside the 20..180 flanks with room to spare, so those seats are
    // narrower: an eight-seater's boxes used to overhang the body they were drawn on.
    private const float SeatWidthTwo = 62f;
    private const float SeatWidthThree = 48f;
    private const float SeatGapTwo = 24f;
    private const float SeatGapThree = 7f;

    /// <summary>Passenger seats this layout was built for (slots minus the driver).</summary>
    public int PassengerSeats => Slots.Count - 1;

    /// <summary>
    /// The seat map for a mount with this many passenger seats (the owner is the driver and rides the
    /// implicit last spot, so the vehicle always has one slot more than this).
    /// </summary>
    public static VehicleLayout For(int passengerSeats)
    {
        if (passengerSeats <= 1)
            return Tandem();

        var rows = RowsFor(passengerSeats + 1);
        var kind = rows.Count <= 2 ? VehicleKind.Car : VehicleKind.Van;
        return MultiRow(kind, rows);
    }

    /// <summary>
    /// Seats per row: two abreast on the small vehicles, three abreast on the eight-seater and anything
    /// larger. A row of three is only used when it cannot strand a single seat beside it.
    /// </summary>
    private static List<int> RowsFor(int slots)
    {
        if (slots == 3)
            return new List<int> { 2, 1 };

        if (slots <= 4)
            return new List<int> { 2, 2 };

        var rows = new List<int>();
        var left = slots;
        while (left > 0)
        {
            if (left == 1)
            {
                rows.Add(1);
                break;
            }

            var take = left >= 3 ? 3 : 2;
            if (left - take == 1)
                take = 2;

            rows.Add(take);
            left -= take;
        }

        return rows;
    }

    private static VehicleLayout MultiRow(VehicleKind kind, List<int> rows)
    {
        var van = kind == VehicleKind.Van;
        var hood = van ? 44f : 52f;
        var trunk = van ? 30f : 46f;
        var noseWidth = van ? 34f : 40f;
        var tailWidth = van ? 40f : 44f;

        var cabinTop = Nose + hood + Windshield;
        var cabinBottom = cabinTop + rows.Count * RowHeight;
        var height = cabinBottom + RearGlass + trunk + Tail;
        var frontAxle = Nose + 40f;
        var rearAxle = height - Nose - 62f;

        var slots = BuildSlots(rows, cabinTop);
        var shapes = BuildVehicleShapes(height, cabinTop, cabinBottom, frontAxle, rearAxle, noseWidth, tailWidth);
        var driver = slots[0].Rect;

        return new VehicleLayout(
            kind,
            ModelWidth,
            height,
            slots,
            shapes,
            new LayoutPoint(driver.X + 18f, driver.Y - 9f),
            9f);
    }

    private static List<SeatSlot> BuildSlots(IReadOnlyList<int> rows, float cabinTop)
    {
        var slots = new List<SeatSlot>();
        var seat = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var count = rows[r];
            var width = count >= 3 ? SeatWidthThree : SeatWidthTwo;
            var gap = count >= 3 ? SeatGapThree : SeatGapTwo;
            var total = count * width + (count - 1) * gap;
            var x0 = (ModelWidth - total) / 2f;
            var y = cabinTop + r * RowHeight + SeatInset;

            for (var c = 0; c < count; c++)
            {
                slots.Add(new SeatSlot(seat, new LayoutRect(x0 + c * (width + gap), y, width, SeatHeight)));
                seat++;
            }
        }

        return slots;
    }

    private static List<VehicleShape> BuildVehicleShapes(
        float height,
        float cabinTop,
        float cabinBottom,
        float frontAxle,
        float rearAxle,
        float noseWidth,
        float tailWidth)
    {
        var center = ModelWidth / 2f;
        var shapes = new List<VehicleShape>();

        // Tyres first: the shell is painted over their inner edges, as on the real thing.
        foreach (var axle in new[] { frontAxle, rearAxle })
        {
            // Flush with the flanks: a tyre drawn clear of the body reads as a stray box, not a wheel.
            shapes.Add(Rect(VehiclePart.Solid, 2f, axle - 20f, 18f, 40f));
            shapes.Add(Rect(VehiclePart.Solid, ModelWidth - 20f, axle - 20f, 18f, 40f));
        }

        // Shell: flat bumper, a flare over the front axle, straight flanks, tapered tail.
        var hull = new List<LayoutPoint> { new(center - noseWidth, Nose) };
        Quad(hull, center - noseWidth, Nose, center - noseWidth - 24f, Nose + 2f, 26f, frontAxle - 26f);
        Quad(hull, 26f, frontAxle - 26f, 20f, frontAxle - 14f, 20f, frontAxle + 6f);
        hull.Add(new LayoutPoint(20f, height - 46f));
        Quad(hull, 20f, height - 46f, 20f, height - 28f, 30f, height - 22f);
        hull.Add(new LayoutPoint(center - tailWidth - 4f, height - Tail));
        hull.Add(new LayoutPoint(center + tailWidth + 4f, height - Tail));
        Quad(hull, center + tailWidth + 4f, height - Tail, 180f, height - 28f, 180f, height - 46f);
        hull.Add(new LayoutPoint(180f, frontAxle + 6f));
        Quad(hull, 180f, frontAxle + 6f, 180f, frontAxle - 14f, 174f, frontAxle - 26f);
        Quad(hull, 174f, frontAxle - 26f, center + noseWidth + 24f, Nose + 2f, center + noseWidth, Nose);
        shapes.Add(new VehicleShape(VehiclePart.Body, hull));

        // Wheel arches and shut lines.
        foreach (var axle in new[] { frontAxle, rearAxle })
        {
            shapes.Add(new VehicleShape(VehiclePart.Trim, new[] { new LayoutPoint(20f, axle - 22f), new LayoutPoint(20f, axle + 22f) }, false, 6f));
            shapes.Add(new VehicleShape(VehiclePart.Trim, new[] { new LayoutPoint(180f, axle - 22f), new LayoutPoint(180f, axle + 22f) }, false, 6f));
        }

        shapes.Add(Rect(VehiclePart.Solid, 11f, cabinTop - 12f, 11f, 9f));
        shapes.Add(Rect(VehiclePart.Solid, ModelWidth - 22f, cabinTop - 12f, 11f, 9f));
        shapes.Add(new VehicleShape(VehiclePart.Trim, new[] { new LayoutPoint(32f, 34f), new LayoutPoint(168f, 34f) }, false, 1f));

        shapes.Add(Rect(VehiclePart.HeadLamp, 56f, 13f, 26f, 7f));
        shapes.Add(Rect(VehiclePart.HeadLamp, ModelWidth - 82f, 13f, 26f, 7f));

        shapes.Add(new VehicleShape(VehiclePart.Glass, new[]
        {
            new LayoutPoint(46f, cabinTop - Windshield), new LayoutPoint(154f, cabinTop - Windshield),
            new LayoutPoint(170f, cabinTop), new LayoutPoint(30f, cabinTop),
        }));
        shapes.Add(new VehicleShape(VehiclePart.Glass, new[]
        {
            new LayoutPoint(30f, cabinBottom), new LayoutPoint(170f, cabinBottom),
            new LayoutPoint(154f, cabinBottom + RearGlass), new LayoutPoint(46f, cabinBottom + RearGlass),
        }));

        shapes.Add(Rect(VehiclePart.Accent, 21f, cabinTop + 4f, 6f, cabinBottom - cabinTop - 8f));
        shapes.Add(Rect(VehiclePart.Accent, ModelWidth - 27f, cabinTop + 4f, 6f, cabinBottom - cabinTop - 8f));

        shapes.Add(Rect(VehiclePart.TailLamp, 60f, height - 15f, 30f, 6f));
        shapes.Add(Rect(VehiclePart.TailLamp, ModelWidth - 90f, height - 15f, 30f, 6f));
        shapes.Add(new VehicleShape(VehiclePart.Trim, new[]
        {
            new LayoutPoint(40f, cabinBottom + 22f), new LayoutPoint(160f, cabinBottom + 22f),
        }, false, 1f));

        return shapes;
    }

    /// <summary>A single pillion passenger on a two-seater: rider's seat, pillion behind it, one wheel each end.</summary>
    private static VehicleLayout Tandem()
    {
        const float height = 286f;
        var rider = new LayoutRect(70f, 124f, 60f, 48f);
        var slots = new List<SeatSlot> { new(0, rider), new(1, new LayoutRect(74f, 180f, 52f, 42f)) };

        var shapes = new List<VehicleShape>
        {
            Rect(VehiclePart.Solid, 84f, 6f, 32f, 68f),
            Rect(VehiclePart.Body, 93f, 15f, 14f, 50f),
            new(VehiclePart.Solid, new[]
            {
                new LayoutPoint(92f, 72f), new LayoutPoint(108f, 72f),
                new LayoutPoint(106f, 94f), new LayoutPoint(94f, 94f),
            }),
            Rect(VehiclePart.Solid, 58f, 78f, 84f, 8f),
            Rect(VehiclePart.HeadLamp, 88f, 72f, 24f, 8f),
            new(VehiclePart.Body, new[]
            {
                new LayoutPoint(92f, 94f), new LayoutPoint(108f, 94f), new LayoutPoint(124f, 116f),
                new LayoutPoint(126f, 129f), new LayoutPoint(119f, 134f), new LayoutPoint(81f, 134f),
                new LayoutPoint(74f, 129f), new LayoutPoint(76f, 116f),
            }),
            new(VehiclePart.Body, new[]
            {
                new LayoutPoint(74f, 132f), new LayoutPoint(126f, 132f), new LayoutPoint(128f, 244f),
                new LayoutPoint(128f, 258f), new LayoutPoint(114f, 258f), new LayoutPoint(86f, 258f),
                new LayoutPoint(72f, 258f), new LayoutPoint(72f, 244f),
            }),
            Rect(VehiclePart.Solid, 84f, 248f, 32f, 32f),
            Rect(VehiclePart.Body, 93f, 252f, 14f, 24f),
            Rect(VehiclePart.TailLamp, 88f, 240f, 24f, 6f),
        };

        return new VehicleLayout(
            VehicleKind.Tandem,
            ModelWidth,
            height,
            slots,
            shapes,
            new LayoutPoint(rider.X + 18f, rider.Y - 9f),
            9f);
    }

    private static VehicleShape Rect(VehiclePart part, float x, float y, float w, float h) => new(part, new[]
    {
        new LayoutPoint(x, y), new LayoutPoint(x + w, y), new LayoutPoint(x + w, y + h), new LayoutPoint(x, y + h),
    });

    /// <summary>
    /// Samples a quadratic curve into the point list. The curves are the drawing's corners: sampling them
    /// here keeps the shape a plain convex polygon, which is what ImGui can fill and what a test can
    /// check a seat against.
    /// </summary>
    private static void Quad(List<LayoutPoint> points, float x0, float y0, float cx, float cy, float x1, float y1)
    {
        const int steps = 7;
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var u = 1f - t;
            points.Add(new LayoutPoint(
                (u * u * x0) + (2f * u * t * cx) + (t * t * x1),
                (u * u * y0) + (2f * u * t * cy) + (t * t * y1)));
        }
    }
}
