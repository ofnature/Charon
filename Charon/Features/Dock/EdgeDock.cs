using System;
using System.Collections.Generic;
using System.Numerics;

namespace Charon.Features.Dock;

/// <summary>The context tabs, in the order they stack on the edge.</summary>
public enum DockTab
{
    Retainers,
    Floor,
    Chest,
    Riders,
    Route,

    /// <summary>The main window, minimised to the edge (Odysseus's minimise-to-tab).</summary>
    Main,
}

/// <summary>
/// The screen edge a tab is pinned to. The values are the shared layout's edge numbers, the same in Daedalus.
/// </summary>
public enum DockEdge
{
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3,
}

/// <summary>Where one tab sits: its edge, and its centre as a fraction along that edge (0.5 = the middle).</summary>
public sealed class DockPlacement
{
    public DockEdge Edge { get; set; }
    public float Along { get; set; } = 0.5f;
}

/// <summary>What is true right now that a tab could be about.</summary>
public sealed record DockContext(bool AtBell, bool InDeepDungeon, bool ChestOpen, bool DrivingMultiSeat, bool InVariant = false);

/// <summary>
/// The edge dock's decisions. Pure — no Dalamud or ImGui types.
///
/// The idea is Lifestream's (its "badge dock": tabs pinned to a screen edge with sideways labels, a
/// panel popping out beside the one you point at). Nothing of theirs is reused — that component ships
/// only as a compiled library with no licence — so this is Charon's own, built from the behaviour.
///
/// A tab exists only while it APPLIES: at a bell, in a deep dungeon, with the company chest open,
/// driving a multi-seat mount. The rest of the time the edge is empty.
/// </summary>
public static class DockTabs
{
    public static IReadOnlyList<DockTab> Visible(DockContext context)
    {
        var tabs = new List<DockTab>(4);
        if (context.AtBell) tabs.Add(DockTab.Retainers);
        if (context.InDeepDungeon) tabs.Add(DockTab.Floor);
        if (context.ChestOpen) tabs.Add(DockTab.Chest);
        if (context.DrivingMultiSeat) tabs.Add(DockTab.Riders);
        if (context.InVariant) tabs.Add(DockTab.Route);
        return tabs;
    }

    public static string Label(DockTab tab) => tab switch
    {
        DockTab.Retainers => "Retainers",
        DockTab.Floor => "Floor",
        DockTab.Chest => "Chest",
        DockTab.Riders => "Riders",
        DockTab.Route => "Route",
        DockTab.Main => "Charon",
        _ => tab.ToString(),
    };
}

/// <summary>
/// Which tab's panel is out. Pointing at a tab opens it; it stays while the pointer is on the tab or
/// the panel, and closes a moment after it leaves — the delay is what stops it flickering shut on the
/// way across the gap. Clicking a tab PINS its panel open (click again to unpin), for a panel you want
/// to keep on screen, like the deep dungeon floor.
/// </summary>
public sealed class DockHover
{
    private DateTime _lastTouchedUtc;

    public DockTab? Open { get; private set; }
    public DockTab? Pinned { get; private set; }

    /// <param name="hoveredTab">The tab under the pointer this frame, if any.</param>
    /// <param name="panelBusy">The pointer is on the open panel, or the panel is mid-interaction (a combo open, a slider held).</param>
    public void Update(DockTab? hoveredTab, bool panelBusy, DateTime nowUtc, TimeSpan closeDelay, IReadOnlyList<DockTab> visible)
    {
        // A tab whose moment has passed takes its panel (and its pin) with it.
        if (Pinned is { } pinned && !Contains(visible, pinned))
            Pinned = null;
        if (Open is { } open && !Contains(visible, open))
            Open = null;

        if (hoveredTab is { } hovered && Contains(visible, hovered))
        {
            Open = hovered;
            _lastTouchedUtc = nowUtc;
            return;
        }

        if (panelBusy)
        {
            _lastTouchedUtc = nowUtc;
            return;
        }

        if (Open != null && nowUtc - _lastTouchedUtc > closeDelay)
            Open = Pinned; // back to the pinned panel, or to nothing
    }

    /// <summary>A click on a tab: pin it, or unpin it if it already was.</summary>
    public void Click(DockTab tab)
    {
        Pinned = Pinned == tab ? null : tab;
        if (Pinned != null)
            Open = Pinned;
    }

    private static bool Contains(IReadOnlyList<DockTab> tabs, DockTab tab)
    {
        foreach (var t in tabs)
        {
            if (t == tab)
                return true;
        }

        return false;
    }
}

/// <summary>
/// Sideways text. ImGui cannot draw rotated text, so the label is drawn normally and its vertices are
/// then turned a quarter-turn anticlockwise about a pivot (the standard technique). These two functions
/// are that geometry: text of size (w, h) drawn at the pivot lands, once turned, in a box of size
/// (h, w) whose top-left is where the caller asked — reading bottom-to-top.
/// </summary>
public static class DockMath
{
    /// <summary>Where to draw the unrotated text so that, turned, its box has this top-left corner.</summary>
    public static Vector2 PivotFor(Vector2 topLeft, Vector2 textSize) => new(topLeft.X, topLeft.Y + textSize.X);

    /// <summary>A quarter-turn anticlockwise about <paramref name="pivot"/>, in screen space (Y grows downward).</summary>
    public static Vector2 TurnUp(Vector2 point, Vector2 pivot)
    {
        var d = point - pivot;
        return pivot + new Vector2(d.Y, -d.X);
    }

    /// <summary>
    /// A vertex of text DRAWN at <paramref name="drawnAt"/>, turned a quarter-turn and moved so the text's
    /// origin lands on <paramref name="pivot"/>. Drawing somewhere else first is not optional: ImGui
    /// throws away glyphs that fall outside the screen BEFORE they are turned, and text drawn at a pivot
    /// on the RIGHT edge runs off the screen — the "Retainers" tab read "Reta" (live, 2026-10-16). Draw
    /// it at a point where the whole unturned line is on screen, then place it with this.
    /// </summary>
    public static Vector2 TurnUpAndPlace(Vector2 point, Vector2 drawnAt, Vector2 pivot)
    {
        var d = point - drawnAt;
        return pivot + new Vector2(d.Y, -d.X);
    }

    /// <summary>Top and bottom tabs run along the edge; left and right ones stand up the edge.</summary>
    public static bool IsHorizontal(DockEdge edge) => edge is DockEdge.Top or DockEdge.Bottom;

    /// <summary>The screen edge closest to <paramref name="point"/> — where a dragged tab lands when let go.</summary>
    public static DockEdge NearestEdge(Vector2 point, Vector2 workPos, Vector2 workSize)
    {
        var best = DockEdge.Left;
        var bestDistance = point.X - workPos.X;

        void Consider(DockEdge edge, float distance)
        {
            if (distance < bestDistance)
            {
                best = edge;
                bestDistance = distance;
            }
        }

        Consider(DockEdge.Right, workPos.X + workSize.X - point.X);
        Consider(DockEdge.Top, point.Y - workPos.Y);
        Consider(DockEdge.Bottom, workPos.Y + workSize.Y - point.Y);
        return best;
    }

    /// <summary>How far along <paramref name="edge"/> a point is, as a fraction of the edge (0..1).</summary>
    public static float AlongFraction(DockEdge edge, Vector2 point, Vector2 workPos, Vector2 workSize)
    {
        var fraction = IsHorizontal(edge)
            ? (point.X - workPos.X) / MathF.Max(1f, workSize.X)
            : (point.Y - workPos.Y) / MathF.Max(1f, workSize.Y);
        return Math.Clamp(fraction, 0f, 1f);
    }

    /// <summary>
    /// A tab's rectangle: <paramref name="start"/> along the edge (its top, or its left on the top and bottom
    /// edges), <paramref name="length"/> long, <paramref name="thickness"/> out from the edge.
    /// </summary>
    public static (Vector2 Min, Vector2 Size) TabRect(
        DockEdge edge, float start, float length, float thickness, Vector2 workPos, Vector2 workSize) => edge switch
    {
        DockEdge.Right => (new Vector2(workPos.X + workSize.X - thickness, start), new Vector2(thickness, length)),
        DockEdge.Top => (new Vector2(start, workPos.Y), new Vector2(length, thickness)),
        DockEdge.Bottom => (new Vector2(start, workPos.Y + workSize.Y - thickness), new Vector2(length, thickness)),
        _ => (new Vector2(workPos.X, start), new Vector2(thickness, length)),
    };

    /// <summary>
    /// Where an open tab's panel goes: beside the tab, on the screen side of it, level with it — slid back
    /// along the edge if <paramref name="panelSize"/> would run it off the screen.
    /// </summary>
    /// <returns>The window position and the pivot that position refers to (as for SetNextWindowPos).</returns>
    public static (Vector2 Pos, Vector2 Pivot) PanelPlacement(
        DockEdge edge, Vector2 tabMin, Vector2 tabSize, Vector2 panelSize, Vector2 workPos, Vector2 workSize, float gap)
    {
        const float margin = 4f;
        var y = MathF.Max(workPos.Y + margin, MathF.Min(tabMin.Y, workPos.Y + workSize.Y - panelSize.Y - margin));
        var x = MathF.Max(workPos.X + margin, MathF.Min(tabMin.X, workPos.X + workSize.X - panelSize.X - margin));

        return edge switch
        {
            DockEdge.Right => (new Vector2(tabMin.X - gap, y), new Vector2(1f, 0f)),
            DockEdge.Top => (new Vector2(x, tabMin.Y + tabSize.Y + gap), Vector2.Zero),
            DockEdge.Bottom => (new Vector2(x, tabMin.Y - gap), new Vector2(0f, 1f)),
            _ => (new Vector2(tabMin.X + tabSize.X + gap, y), Vector2.Zero),
        };
    }
}
