using System;
using System.Numerics;
using Charon.Features.Tweaks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Charon.Windows;

/// <summary>
/// Shake the mouse to find it: while <see cref="CursorShake"/> is active, a circle on the cursor and lines
/// through it across the whole screen (straight and diagonal), sized and faded by how hard you shook.
/// Ported from Where's Mouse? by Paissa Heavy Industries (MIT, paissaheavyindustries/WheresMouse) — its
/// main indicator with its defaults (50 px circle, 5 px lines) and its persistent cursor circle (the DOT,
/// user-requested 2026-10-22: 'its hard to track the cursor sometimes'); its trail, corner and off-screen
/// extras are not ported. Fullscreen, no background, no inputs — it can never eat a click.
/// </summary>
public sealed class CursorFinderOverlay : Window
{
    private const float CircleRadius = 50f;
    private const float LineThickness = 5f;

    /// <summary>The dot's size is the player's (1-40 px radius); the ring sits this far outside it.</summary>
    private const float DotRingGap = 5f;
    private const float MinDot = 1f;
    private const float MaxDot = 40f;

    /// <summary>"Practically infinity", as the original draws its lines.</summary>
    private const float Reach = 20000f;

    private readonly CursorShake _shake = new();
    private readonly Func<bool> _allowedNow;
    private readonly Func<bool> _shakeEnabled;
    private readonly Func<bool> _dotEnabled;
    private readonly Func<Vector4> _color;
    private readonly Func<float> _dotSize;
    private readonly Func<float> _dotOpacity;
    private DateTime _lastUtc = DateTime.UtcNow;

    public CursorFinderOverlay(Func<bool> allowedNow, Func<bool> shakeEnabled, Func<bool> dotEnabled, Func<Vector4> color,
        Func<float> dotSize, Func<float> dotOpacity)
        : base("##CharonCursorFinder")
    {
        _dotSize = dotSize;
        _dotOpacity = dotOpacity;
        _allowedNow = allowedNow;
        _shakeEnabled = shakeEnabled;
        _dotEnabled = dotEnabled;
        _color = color;
        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    /// <summary>For the Debug line.</summary>
    public string Status { get; private set; } = "off";

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        Position = viewport.Pos;
        Size = viewport.Size;
    }

    public override void Draw()
    {
        var now = DateTime.UtcNow;
        var cursor = ImGui.GetMousePos();
        _shake.Update(cursor, (now - _lastUtc).TotalSeconds);
        _lastUtc = now;

        var viewport = ImGui.GetMainViewport();
        var onScreen = cursor.X >= viewport.Pos.X && cursor.Y >= viewport.Pos.Y
                       && cursor.X <= viewport.Pos.X + viewport.Size.X && cursor.Y <= viewport.Pos.Y + viewport.Size.Y;

        if (!_allowedNow())
        {
            Status = "only shown in combat";
            return;
        }

        if (!onScreen)
        {
            Status = "the cursor is off the game window";
            return;
        }

        var tint = _color();
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();

        if (_dotEnabled())
        {
            // Clamped: a hand-edited config must not draw a dot the size of the screen.
            var radius = Math.Clamp(_dotSize(), MinDot, MaxDot);
            var alpha = tint.W * Math.Clamp(_dotOpacity(), 0f, 1f);
            draw.AddCircleFilled(cursor, radius, ImGui.GetColorU32(tint with { W = alpha }), 24);
            draw.AddCircle(cursor, radius + DotRingGap, ImGui.GetColorU32(tint with { W = alpha * 0.6f }), 32, 2f);
        }

        if (!_shakeEnabled() || !_shake.Active)
        {
            draw.PopClipRect();
            Status = _dotEnabled() ? "dot on the cursor" : "watching — shake the mouse to show it";
            return;
        }

        var power = (float)_shake.Power;
        var color = ImGui.GetColorU32(tint with { W = tint.W * power });
        var half = LineThickness * power / 2f;

        // Straight lines through the cursor.
        Band(draw, cursor, new Vector2(0, 1), half, color);
        Band(draw, cursor, new Vector2(1, 0), half, color);

        // Diagonal lines through the cursor.
        Band(draw, cursor, Vector2.Normalize(new Vector2(1, 1)), half, color);
        Band(draw, cursor, Vector2.Normalize(new Vector2(1, -1)), half, color);

        draw.AddCircleFilled(cursor, CircleRadius * power, color, 32);
        draw.PopClipRect();

        Status = $"showing ({power:P0})";
    }

    /// <summary>A long thin quad through <paramref name="centre"/> along <paramref name="direction"/>.</summary>
    private static void Band(ImDrawListPtr draw, Vector2 centre, Vector2 direction, float halfWidth, uint color)
    {
        var across = new Vector2(-direction.Y, direction.X) * halfWidth;
        var along = direction * Reach;
        draw.AddQuadFilled(centre - along - across, centre - along + across, centre + along + across, centre + along - across, color);
    }
}
