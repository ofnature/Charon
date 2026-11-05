using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace Charon.Windows.Components;

/// <summary>
/// World-space shapes drawn over the game, projected point by point through WorldToScreen — NecroLens's
/// plain-2D approach (MIT, Jukkales/NecroLens): world-radius circles projected segment by segment, sight
/// cones sweeping ±45° of a facing. Shared by the deep-dungeon and Eureka overlays.
/// </summary>
public sealed class WorldShapes
{
    private const int CircleSegments = 48;

    private readonly IGameGui _gameGui;

    public WorldShapes(IGameGui gameGui) => _gameGui = gameGui;

    /// <summary>
    /// A world-space line, projected in pieces. A single screen-space line fails the moment one end is
    /// behind the camera (its projection is garbage), so the line is sampled and only the stretches in
    /// FRONT of the camera are drawn — turn away from the target and the line still points toward it.
    /// </summary>
    public void Line(ImDrawListPtr drawList, Vector3 from, Vector3 to, uint color)
    {
        const int segments = 32;
        var drawing = false;

        for (var i = 0; i <= segments; i++)
        {
            var point = Vector3.Lerp(from, to, (float)i / segments);
            if (_gameGui.WorldToScreen(point, out var screen, out _))
            {
                drawList.PathLineTo(screen);
                drawing = true;
            }
            else if (drawing)
            {
                drawList.PathStroke(color, ImDrawFlags.None, 2.5f);
                drawing = false;
            }
        }

        if (drawing)
            drawList.PathStroke(color, ImDrawFlags.None, 2.5f);
        drawList.PathClear();
    }

    public void Circle(ImDrawListPtr drawList, Vector3 center, float radius, uint color, bool filled)
    {
        for (var i = 0; i <= CircleSegments; i++)
        {
            var angle = 2f * MathF.PI * i / CircleSegments;
            _gameGui.WorldToScreen(
                new Vector3(center.X + radius * MathF.Sin(angle), center.Y, center.Z + radius * MathF.Cos(angle)),
                out var point);
            drawList.PathLineTo(point);
        }

        if (filled)
        {
            drawList.PathFillConvex((color & 0x00FFFFFF) | 0x40000000);
            // redraw the outline — the fill consumed the path
            for (var i = 0; i <= CircleSegments; i++)
            {
                var angle = 2f * MathF.PI * i / CircleSegments;
                _gameGui.WorldToScreen(
                    new Vector3(center.X + radius * MathF.Sin(angle), center.Y, center.Z + radius * MathF.Cos(angle)),
                    out var point);
                drawList.PathLineTo(point);
            }
        }

        drawList.PathStroke(color, ImDrawFlags.None, 1.5f);
        drawList.PathClear();
    }

    /// <summary>NecroLens's sight cone: ±45° around the mob's facing (their rotation+π/4 sweep).</summary>
    public void Cone(ImDrawListPtr drawList, Vector3 center, float rotation, float radius, uint color)
    {
        const float coneAngle = 1.571f; // 90° total
        var start = rotation + MathF.PI / 4f;
        var step = coneAngle / CircleSegments;

        _gameGui.WorldToScreen(center, out var origin);
        drawList.PathLineTo(origin);
        for (var i = 0; i <= CircleSegments; i++)
        {
            var angle = start - i * step;
            _gameGui.WorldToScreen(
                new Vector3(center.X + radius * MathF.Sin(angle), center.Y, center.Z + radius * MathF.Cos(angle)),
                out var point);
            drawList.PathLineTo(point);
        }

        drawList.PathLineTo(origin);
        drawList.PathFillConvex((color & 0x00FFFFFF) | 0x33000000);

        drawList.PathLineTo(origin);
        for (var i = 0; i <= CircleSegments; i++)
        {
            var angle = start - i * step;
            _gameGui.WorldToScreen(
                new Vector3(center.X + radius * MathF.Sin(angle), center.Y, center.Z + radius * MathF.Cos(angle)),
                out var point);
            drawList.PathLineTo(point);
        }

        drawList.PathLineTo(origin);
        drawList.PathStroke(color, ImDrawFlags.None, 1.5f);
        drawList.PathClear();
    }

    public void FacingArrow(ImDrawListPtr drawList, Vector3 center, float rotation, uint color)
    {
        var tip = new Vector3(center.X + 2.2f * MathF.Sin(rotation), center.Y, center.Z + 2.2f * MathF.Cos(rotation));
        if (!_gameGui.WorldToScreen(center, out var from) || !_gameGui.WorldToScreen(tip, out var to))
            return;

        drawList.AddLine(from, to, color, 2.5f);
        var dir = Vector2.Normalize(to - from);
        var normal = new Vector2(-dir.Y, dir.X);
        drawList.AddTriangleFilled(to + dir * 6f, to + normal * 4f, to - normal * 4f, color);
    }

    /// <summary>Text centred just above a world point; nothing when the point is off screen.</summary>
    public void Label(ImDrawListPtr drawList, Vector3 at, string text, uint color) => Label(drawList, at, text, color, 0);

    /// <summary>The same, raised <paramref name="linesUp"/> text lines so a second label stacks above the first.</summary>
    public void Label(ImDrawListPtr drawList, Vector3 at, string text, uint color, int linesUp)
    {
        if (!_gameGui.WorldToScreen(at, out var screen))
            return;
        var size = ImGui.CalcTextSize(text);
        var raise = linesUp * ImGui.GetTextLineHeightWithSpacing();
        drawList.AddText(new Vector2(screen.X - size.X / 2f, screen.Y - size.Y - 2f - raise), color, text);
    }
}
