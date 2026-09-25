using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Features.AutoPillion;

namespace Charon.Windows;

/// <summary>
/// The driver's glance: a small window showing the mount the local player is driving as a 2D vehicle
/// drawn top-down — the pilot's seat at the nose, passenger seats on the floorpan, plus who is in each
/// one. Opened by the plugin ONLY while the local player owns a mounted multi-seat mount (the same
/// cached snapshot the boarding logic reads), so it can never linger after a dismount. When an
/// auto-pillion session is running, empty seats also show the invite state (invited / timed out) so a
/// hole in the roster explains itself.
///
/// The geometry comes from <see cref="VehicleLayout"/> (pure, unit-tested); this class only paints and
/// places text. Seat text is wrapped and clipped to its cushion, so a long name can never spill over a
/// neighbouring seat.
/// </summary>
public sealed class PillionRidersWindow : Window
{
    /// <summary>
    /// Width of the drawing in pixels. <see cref="VehicleLayout"/> is 200 units wide, so this is also the
    /// scale — sized so a two-word character name wraps to two lines inside a seat's cushion.
    /// </summary>
    private const float DrawingWidth = 272f;

    private const float TextPadding = 4f;

    // Seat glyph, in layout units: the cushion carries the name, the backrest the seat number.
    private const float CushionInset = 15f;
    private const float BackInset = 4f;
    private const float BackTop = 21f;
    private const float BackHeight = 14f;
    private const float HeadWidth = 20f;
    private const float HeadHeight = 9f;
    private const float HeadTop = 11f;

    private readonly Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> _occupancy;
    private readonly Func<string> _driverName;
    private readonly Func<string> _mountName;
    private readonly PillionManager _pillion;

    public PillionRidersWindow(
        Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> occupancy,
        Func<string> driverName,
        Func<string> mountName,
        PillionManager pillion)
        : base("Charon — Riders##CharonPillionRiders")
    {
        _occupancy = occupancy;
        _driverName = driverName;
        _mountName = mountName;
        _pillion = pillion;

        Flags = ImGuiWindowFlags.AlwaysAutoResize;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void Draw()
    {
        var rows = _occupancy();
        if (rows.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "Not driving a multi-seat mount.");
            return;
        }

        var filled = 0;
        foreach (var row in rows)
        {
            if (row.EntityId != 0)
                filled++;
        }

        var layout = VehicleLayout.For(rows.Count);
        var scale = DrawingWidth / layout.Width;

        DrawHeader(filled, rows.Count);
        DrawVehicle(layout, rows, scale);
    }

    private void DrawHeader(int filled, int seats)
    {
        ImGui.TextColored(filled == seats ? CharonTheme.StatusGreen : CharonTheme.Accent, $"Riders {filled} / {seats}");

        var mount = _mountName();
        if (mount.Length == 0)
            return;

        // Only when it fits: a mount name that would run into the seat count is worse than no name.
        var label = mount.ToUpperInvariant();
        var width = ImGui.CalcTextSize(label).X;
        var count = ImGui.CalcTextSize($"Riders {filled} / {seats}").X;
        if (count + width + 10f > DrawingWidth)
            return;

        ImGui.SameLine(DrawingWidth - width);
        ImGui.TextColored(CharonTheme.TextMuted, label);
    }

    private void DrawVehicle(VehicleLayout layout, IReadOnlyList<(int Seat, uint EntityId, string Name)> rows, float scale)
    {
        var origin = ImGui.GetCursorScreenPos();
        var height = layout.Height * scale;
        ImGui.Dummy(new Vector2(DrawingWidth, height));

        var dl = ImGui.GetWindowDrawList();

        foreach (var shape in layout.Shapes)
            DrawShape(dl, shape, origin, scale);

        // The control cue: the wheel, ahead of the driver's cushion.
        var steer = ToScreen(layout.Steer, origin, scale);
        dl.AddCircle(steer, layout.SteerRadius * scale, ImGui.GetColorU32(CharonTheme.WithAlpha(CharonTheme.AccentSoft, 0.60f)), 24, 1.7f * scale);
        dl.AddCircleFilled(steer, 2.6f * scale, ImGui.GetColorU32(CharonTheme.WithAlpha(CharonTheme.AccentSoft, 0.50f)), 16);

        foreach (var slot in layout.Slots)
            DrawSeat(dl, slot, rows, origin, scale);

        // The seat text moved the ImGui cursor; park it under the drawing so nothing else rides up.
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height));
    }

    private void DrawSeat(
        ImDrawListPtr dl,
        SeatSlot slot,
        IReadOnlyList<(int Seat, uint EntityId, string Name)> rows,
        Vector2 origin,
        float scale)
    {
        var rect = slot.Rect;
        var min = ToScreen(new LayoutPoint(rect.X, rect.Y), origin, scale);
        var max = ToScreen(new LayoutPoint(rect.Right, rect.Bottom), origin, scale);
        var cushionMax = new Vector2(max.X, ToScreen(new LayoutPoint(0f, rect.Y + rect.H - CushionInset), origin, scale).Y);

        var style = Describe(slot, rows);

        dl.AddRectFilled(min, cushionMax, ImGui.GetColorU32(style.Fill), 9f * scale);
        if (style.Dashed)
            DashRect(dl, min, cushionMax, ImGui.GetColorU32(style.Edge), MathF.Max(1f, scale));
        else
            dl.AddRect(min, cushionMax, ImGui.GetColorU32(style.Edge), 9f * scale, ImDrawFlags.None, 1.2f * scale);

        // Backrest, and the headrest above it.
        var backMin = new Vector2(min.X + (BackInset * scale), min.Y + ((rect.H - BackTop) * scale));
        var backMax = new Vector2(max.X - (BackInset * scale), backMin.Y + (BackHeight * scale));
        dl.AddRectFilled(backMin, backMax, ImGui.GetColorU32(style.Back), 6f * scale);

        var headMin = new Vector2(min.X + ((rect.W - HeadWidth) / 2f * scale), min.Y + ((rect.H - HeadTop) * scale));
        dl.AddRectFilled(headMin, new Vector2(headMin.X + (HeadWidth * scale), headMin.Y + (HeadHeight * scale)), ImGui.GetColorU32(style.Head), 4.5f * scale);

        // Seat number rides on the backrest, so it can never collide with the name.
        if (slot.Seat > 0)
            DrawText(dl, slot.Seat.ToString(), backMin, backMax, CharonTheme.TextDim, 1f);

        DrawText(dl, style.Text, min, cushionMax, style.TextColor, TextPadding);
    }

    /// <summary>
    /// Straight ImGui text, wrapped to the box and clipped to it. Names are the one thing this window
    /// exists to show, and a name that spills into the next seat reads as a different seat's occupant.
    /// </summary>
    private static void DrawText(ImDrawListPtr dl, string text, Vector2 min, Vector2 max, Vector4 color, float padding)
    {
        var width = MathF.Max(max.X - min.X - (2f * padding), 1f);
        var height = MathF.Max(max.Y - min.Y, 1f);
        var size = ImGui.CalcTextSize(text);
        var oneLine = size.X <= width;
        var y = oneLine ? min.Y + MathF.Max((height - size.Y) / 2f, 0f) : min.Y + 1f;

        ImGui.PushClipRect(min, new Vector2(min.X + width + (2f * padding), min.Y + height), true);
        ImGui.SetCursorScreenPos(new Vector2(min.X + padding, y));
        ImGui.PushTextWrapPos(min.X + padding + width);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
        ImGui.PopClipRect();
    }

    private SeatStyle Describe(SeatSlot slot, IReadOnlyList<(int Seat, uint EntityId, string Name)> rows)
    {
        if (slot.Seat == 0)
        {
            var driver = _driverName();
            return new SeatStyle(
                driver.Length > 0 ? driver : "you",
                CharonTheme.TextStrong,
                CharonTheme.WithAlpha(CharonTheme.Accent, 0.16f),
                CharonTheme.WithAlpha(CharonTheme.Accent, 0.75f),
                CharonTheme.WithAlpha(CharonTheme.Accent, 0.28f),
                CharonTheme.WithAlpha(CharonTheme.Accent, 0.22f),
                false);
        }

        foreach (var row in rows)
        {
            if (row.Seat != slot.Seat)
                continue;

            if (row.EntityId != 0)
            {
                return Filled(row.Name.Length > 0 ? row.Name : "occupied");
            }

            break;
        }

        if (_pillion.SessionActive)
        {
            foreach (var seat in _pillion.Seats)
            {
                if (seat.Index != slot.Seat)
                    continue;

                switch (seat.Status)
                {
                    case SeatStatus.InvitePending:
                        return new SeatStyle(
                            $"invited {seat.AssignedName}", CharonTheme.StatusYellow,
                            CharonTheme.WithAlpha(CharonTheme.AccentAmber, 0.14f),
                            CharonTheme.WithAlpha(CharonTheme.AccentAmber, 0.55f),
                            CharonTheme.WithAlpha(CharonTheme.AccentAmber, 0.26f),
                            CharonTheme.WithAlpha(CharonTheme.AccentAmber, 0.20f),
                            false);

                    case SeatStatus.Declined:
                        return new SeatStyle(
                            "invite timed out", CharonTheme.StatusRed,
                            CharonTheme.WithAlpha(CharonTheme.AccentRose, 0.13f),
                            CharonTheme.WithAlpha(CharonTheme.AccentRose, 0.50f),
                            CharonTheme.WithAlpha(CharonTheme.AccentRose, 0.24f),
                            CharonTheme.WithAlpha(CharonTheme.AccentRose, 0.18f),
                            false);
                }
            }
        }

        return new SeatStyle(
            "empty", CharonTheme.TextDisabled,
            new Vector4(1f, 1f, 1f, 0.012f),
            CharonTheme.BorderDim,
            new Vector4(1f, 1f, 1f, 0.045f),
            new Vector4(1f, 1f, 1f, 0.035f),
            true);
    }

    private static SeatStyle Filled(string name) => new(
        name,
        CharonTheme.TextStrong,
        CharonTheme.WithAlpha(CharonTheme.AccentMint, 0.16f),
        CharonTheme.WithAlpha(CharonTheme.AccentMint, 0.60f),
        CharonTheme.WithAlpha(CharonTheme.AccentMint, 0.28f),
        CharonTheme.WithAlpha(CharonTheme.AccentMint, 0.22f),
        false);

    private static void DrawShape(ImDrawListPtr dl, VehicleShape shape, Vector2 origin, float scale)
    {
        var points = shape.Points;
        if (points.Count == 0)
            return;

        var (fill, stroke, unitThickness, rounding) = StyleOf(shape.Part);
        var thickness = (shape.Thickness > 0f ? shape.Thickness : unitThickness) * scale;

        if (points.Count == 4 && IsAxisAligned(points))
        {
            var a = ToScreen(points[0], origin, scale);
            var b = ToScreen(points[2], origin, scale);
            if (fill.W > 0f)
                dl.AddRectFilled(a, b, ImGui.GetColorU32(fill), rounding * scale);
            if (stroke.W > 0f)
                dl.AddRect(a, b, ImGui.GetColorU32(stroke), rounding * scale, ImDrawFlags.None, thickness);

            return;
        }

        // Stroke first, then refill: ImGui's path builders each consume the path.
        AppendPath(dl, points, origin, scale, shape.Closed);
        if (stroke.W > 0f)
            dl.PathStroke(ImGui.GetColorU32(stroke), shape.Closed ? ImDrawFlags.Closed : ImDrawFlags.None, thickness);

        if (fill.W > 0f)
        {
            AppendPath(dl, points, origin, scale, false);
            dl.PathFillConvex(ImGui.GetColorU32(fill));
        }
    }

    private static void AppendPath(ImDrawListPtr dl, IReadOnlyList<LayoutPoint> points, Vector2 origin, float scale, bool closed)
    {
        for (var i = 0; i < points.Count; i++)
            dl.PathLineTo(ToScreen(points[i], origin, scale));

        if (closed)
            dl.PathLineTo(ToScreen(points[0], origin, scale));
    }

    private static bool IsAxisAligned(IReadOnlyList<LayoutPoint> p) =>
        MathF.Abs(p[0].Y - p[1].Y) < 0.001f && MathF.Abs(p[2].Y - p[3].Y) < 0.001f &&
        MathF.Abs(p[0].X - p[3].X) < 0.001f && MathF.Abs(p[1].X - p[2].X) < 0.001f;

    private static (Vector4 Fill, Vector4 Stroke, float Thickness, float Rounding) StyleOf(VehiclePart part) => part switch
    {
        VehiclePart.Body => (CharonTheme.WithAlpha(CharonTheme.CardBg, 0.97f), CharonTheme.Border, 1.6f, 0f),
        VehiclePart.Solid => (CharonTheme.BgDeep, CharonTheme.Border, 1.2f, 6f),
        VehiclePart.Glass => (CharonTheme.WithAlpha(CharonTheme.AccentCyan, 0.11f), CharonTheme.WithAlpha(CharonTheme.AccentCyan, 0.30f), 1f, 0f),
        VehiclePart.HeadLamp => (CharonTheme.WithAlpha(CharonTheme.AccentAmber, 0.45f), Vector4.Zero, 0f, 3f),
        VehiclePart.TailLamp => (CharonTheme.WithAlpha(CharonTheme.AccentRose, 0.45f), Vector4.Zero, 0f, 3f),
        VehiclePart.Accent => (CharonTheme.WithAlpha(CharonTheme.AccentCyan, 0.12f), Vector4.Zero, 0f, 3f),
        _ => (Vector4.Zero, CharonTheme.Hairline, 1f, 0f),
    };

    private static Vector2 ToScreen(LayoutPoint point, Vector2 origin, float scale) =>
        new(origin.X + (point.X * scale), origin.Y + (point.Y * scale));

    /// <summary>
    /// A dashed border, which is how an open seat reads as open rather than as a seat with nobody in it.
    /// ImGui's own AddRect has no dash pattern, so the four edges are drawn as segments.
    /// </summary>
    private static void DashRect(ImDrawListPtr dl, Vector2 min, Vector2 max, uint color, float thickness)
    {
        const float dash = 5f;
        const float gap = 4f;
        DashLine(dl, new Vector2(min.X, min.Y), new Vector2(max.X, min.Y), color, thickness, dash, gap);
        DashLine(dl, new Vector2(max.X, min.Y), new Vector2(max.X, max.Y), color, thickness, dash, gap);
        DashLine(dl, new Vector2(max.X, max.Y), new Vector2(min.X, max.Y), color, thickness, dash, gap);
        DashLine(dl, new Vector2(min.X, max.Y), new Vector2(min.X, min.Y), color, thickness, dash, gap);
    }

    private static void DashLine(ImDrawListPtr dl, Vector2 from, Vector2 to, uint color, float thickness, float dash, float gap)
    {
        var delta = to - from;
        var length = delta.Length();
        if (length <= 0.001f)
            return;

        var direction = delta / length;
        for (var start = 0f; start < length; start += dash + gap)
            dl.AddLine(from + (direction * start), from + (direction * MathF.Min(start + dash, length)), color, thickness);
    }

    /// <summary>Everything about how one seat is painted, resolved once per frame.</summary>
    private readonly record struct SeatStyle(
        string Text,
        Vector4 TextColor,
        Vector4 Fill,
        Vector4 Edge,
        Vector4 Back,
        Vector4 Head,
        bool Dashed);
}
