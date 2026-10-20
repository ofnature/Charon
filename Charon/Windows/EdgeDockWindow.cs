using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Charon.Features.Dock;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Charon.Windows;

/// <summary>
/// The edge dock: context tabs pinned to the screen edges, each popping its panel out beside it (see
/// <see cref="DockTabs"/> and <see cref="DockHover"/> for the rules). Every tab has its own edge and its own
/// spot along it; dragging a tab and letting go puts it on the nearest edge, where it was let go.
///
/// This window itself is an invisible 1×1 host. Each tab is its own small ImGui window, so tabs can sit
/// anywhere on any edge, and <see cref="DockLayoutShare"/> keeps them from covering each other or Daedalus's.
///
/// A tab's panel is the BODY of the context window it stands for — the window's own <c>Draw()</c>,
/// called inside the pop-out — so the dock and the windows cannot drift apart. While the dock is on,
/// those windows no longer open by themselves; their tabs are how you reach them.
///
/// On the left and right edges the label is ordinary text whose vertices are turned after it is drawn
/// (<see cref="DockMath"/>). It has to be drawn under a full-screen clip rect: ImGui culls glyphs against the
/// clip rect BEFORE they are turned, and the unrotated text runs far outside a 34-pixel tab. On the top and
/// bottom edges the label reads normally.
/// </summary>
public sealed unsafe class EdgeDockWindow : Window
{
    /// <param name="FixedSize">A panel whose content stretches (a table with stretch columns) needs a real size; null lets it auto-size.</param>
    /// <param name="OnClick">What a click does instead of pinning — the minimised main window's tab restores the window.</param>
    public sealed record Tab(DockTab Id, Func<bool> Applies, Action DrawPanel, Vector2? FixedSize = null, Action? OnClick = null);

    private const float TabThickness = 34f;
    private const float TabPadding = 14f;
    private const float PanelGap = 6f;
    private const float LabelScale = 1.25f;
    private const float DragThreshold = 6f;
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(450);

    private const ImGuiWindowFlags TabFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    private readonly IReadOnlyList<Tab> _tabs;
    private readonly Func<DockTab, DockPlacement> _placement;
    private readonly Action<DockTab, DockEdge, float> _moveTab;
    private readonly DockLayoutShare _layout;
    private readonly DockHover _hover = new();
    private List<Tab> _visible = new();
    private Vector2 _panelSize = new(300f, 200f);
    private DockTab? _dragging;

    /// <param name="placement">Where a tab is set to sit: its edge, and its centre as a fraction along it.</param>
    /// <param name="moveTab">A tab was dragged and let go: store its new edge and spot.</param>
    /// <param name="layout">The layout shared with other plugins' tabs (Daedalus), so none cover each other.</param>
    public EdgeDockWindow(
        IReadOnlyList<Tab> tabs, Func<DockTab, DockPlacement> placement, Action<DockTab, DockEdge, float> moveTab,
        DockLayoutShare layout)
        : base("##CharonEdgeDock")
    {
        _tabs = tabs;
        _placement = placement;
        _moveTab = moveTab;
        _layout = layout;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar;
        RespectCloseHotkey = false;
        PositionCondition = ImGuiCond.Always;
        SizeCondition = ImGuiCond.Always;
        Size = Vector2.One;
        IsOpen = false;
    }

    /// <summary>What the dock is showing, for the Debug page.</summary>
    public string Status { get; private set; } = "off";

    /// <summary>Whether any tab applies right now — the plugin opens the window only then.</summary>
    public bool AnyTab()
    {
        _visible = _tabs.Where(t => t.Applies()).ToList();
        if (_visible.Count == 0)
            Status = "no tab applies here";
        return _visible.Count > 0;
    }

    public override void PreDraw() => Position = ImGui.GetMainViewport().WorkPos;

    public override void Draw()
    {
        var viewport = ImGui.GetMainViewport();
        var workPos = viewport.WorkPos;
        var workSize = viewport.WorkSize;

        var requests = _visible.Select(t =>
        {
            var place = _placement(t.Id);
            return new DockRequest(t.Id.ToString(), place.Edge, place.Along, TabLength(t.Id));
        }).ToList();
        var starts = _layout.Place(requests, workPos, workSize);

        DockTab? hovered = null;
        Tab? openTab = null;
        var openMin = Vector2.Zero;
        var openSize = Vector2.Zero;
        var openEdge = DockEdge.Left;

        for (var i = 0; i < _visible.Count; i++)
        {
            var tab = _visible[i];
            var edge = requests[i].Edge;
            if (!starts.TryGetValue(requests[i].Id, out var start))
                continue;

            var (min, size) = DockMath.TabRect(edge, start, requests[i].Length, TabThickness, workPos, workSize);
            if (DrawTabWindow(tab, edge, min, size, ref hovered))
            {
                openTab = tab;
                openMin = min;
                openSize = size;
                openEdge = edge;
            }
        }

        if (_dragging != null)
            DrawDrag(workPos, workSize);

        var panelBusy = openTab != null && DrawPanel(openTab, openEdge, openMin, openSize);
        _hover.Update(hovered, panelBusy, DateTime.UtcNow, CloseDelay, _visible.Select(t => t.Id).ToList());

        Status = $"{_visible.Count} tab(s): {string.Join(", ", requests.Select(r => $"{r.Id} {r.Edge} {r.Along:0.00}"))}"
                 + (_hover.Open is { } open ? $" · {DockTabs.Label(open)} out{(_hover.Pinned == open ? " (pinned)" : "")}" : "");
    }

    /// <summary>One tab, in its own little window. True when its panel is out.</summary>
    private bool DrawTabWindow(Tab tab, DockEdge edge, Vector2 min, Vector2 size, ref DockTab? hovered)
    {
        ImGui.SetNextWindowPos(min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        var isOpen = false;
        if (ImGui.Begin($"##CharonEdgeDockTab{tab.Id}", TabFlags))
        {
            // A press that moves becomes a drag; only a press that does not is a click.
            var clicked = ImGui.InvisibleButton($"##dock{tab.Id}", size);
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, DragThreshold))
                _dragging = tab.Id;
            if (clicked && _dragging != tab.Id)
            {
                if (tab.OnClick != null)
                    tab.OnClick();
                else
                    _hover.Click(tab.Id);
            }
            if (ImGui.IsItemHovered() && _dragging == null)
                hovered = tab.Id;

            isOpen = _hover.Open == tab.Id && _dragging == null;
            var lit = isOpen || hovered == tab.Id || _dragging == tab.Id;
            DrawTab(ImGui.GetWindowDrawList(), min, size, DockTabs.Label(tab.Id), lit, _hover.Pinned == tab.Id, edge);
        }

        ImGui.End();
        ImGui.PopStyleVar(3);
        return isOpen;
    }

    /// <summary>
    /// A tab being dragged: the edge it would land on lit, an outline where it would sit, and on release the
    /// move.
    /// </summary>
    private void DrawDrag(Vector2 workPos, Vector2 workSize)
    {
        if (_dragging is not { } dragging)
            return;

        var mouse = ImGui.GetMousePos();
        var target = DockMath.NearestEdge(mouse, workPos, workSize);
        var along = DockMath.AlongFraction(target, mouse, workPos, workSize);

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _dragging = null;
            _moveTab(dragging, target, along);
            return;
        }

        var fg = ImGui.GetForegroundDrawList();
        var accent = ImGui.GetColorU32(CharonTheme.Accent);
        var a = workPos;
        var b = workPos + workSize;
        const float bar = 3f;
        switch (target)
        {
            case DockEdge.Left: fg.AddRectFilled(a, new Vector2(a.X + bar, b.Y), accent); break;
            case DockEdge.Right: fg.AddRectFilled(new Vector2(b.X - bar, a.Y), b, accent); break;
            case DockEdge.Top: fg.AddRectFilled(a, new Vector2(b.X, a.Y + bar), accent); break;
            case DockEdge.Bottom: fg.AddRectFilled(new Vector2(a.X, b.Y - bar), b, accent); break;
        }

        // The outline of where it would land (before any nudge from its neighbours).
        var length = TabLength(dragging);
        var horizontal = DockMath.IsHorizontal(target);
        var edgeStart = horizontal ? workPos.X : workPos.Y;
        var edgeLength = horizontal ? workSize.X : workSize.Y;
        var start = Math.Clamp(edgeStart + along * edgeLength - length / 2f, edgeStart,
            MathF.Max(edgeStart, edgeStart + edgeLength - length));
        var (min, size) = DockMath.TabRect(target, start, length, TabThickness, workPos, workSize);
        fg.AddRect(min, min + size, accent, 6f, ImDrawFlags.None, 2f);
    }

    private static float TabLength(DockTab tab) =>
        MathF.Max(90f, ImGui.CalcTextSize(DockTabs.Label(tab)).X * LabelScale + TabPadding * 2f);

    private static void DrawTab(ImDrawListPtr drawList, Vector2 min, Vector2 size, string label, bool lit, bool pinned, DockEdge edge)
    {
        var max = min + size;
        var fill = lit ? new Vector4(0.16f, 0.17f, 0.20f, 0.96f) : new Vector4(0.09f, 0.10f, 0.12f, 0.88f);
        // Rounded on the side facing the screen, square against the edge it is pinned to.
        var corners = edge switch
        {
            DockEdge.Right => ImDrawFlags.RoundCornersLeft,
            DockEdge.Top => ImDrawFlags.RoundCornersBottom,
            DockEdge.Bottom => ImDrawFlags.RoundCornersTop,
            _ => ImDrawFlags.RoundCornersRight,
        };
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 6f, corners);

        if (pinned)
        {
            // The accent bar the sidebar uses for "selected", on the tab's screen-facing side.
            var (barMin, barSize) = edge switch
            {
                DockEdge.Right => (min, new Vector2(2f, size.Y)),
                DockEdge.Top => (new Vector2(min.X, max.Y - 2f), new Vector2(size.X, 2f)),
                DockEdge.Bottom => (min, new Vector2(size.X, 2f)),
                _ => (new Vector2(max.X - 2f, min.Y), new Vector2(2f, size.Y)),
            };
            drawList.AddRectFilled(barMin, barMin + barSize, ImGui.GetColorU32(CharonTheme.Accent));
        }

        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * LabelScale;
        var textSize = ImGui.CalcTextSize(label) * LabelScale;
        var color = ImGui.GetColorU32(lit ? CharonTheme.TextPrimary : CharonTheme.TextSecondary);

        if (DockMath.IsHorizontal(edge))
        {
            drawList.AddText(font, fontSize, min + (size - textSize) / 2f, color, label);
            return;
        }

        // Turned, the label is textSize.Y wide and textSize.X tall — centred in the tab.
        var topLeft = min + new Vector2((size.X - textSize.Y) / 2f, (size.Y - textSize.X) / 2f);
        var pivot = DockMath.PivotFor(topLeft, textSize);

        // Drawn first at the top-left of the screen, where the whole unturned line fits, then turned and
        // moved into the tab. Drawn AT the tab, the line ran off the right edge and lost its tail.
        var drawnAt = ImGui.GetMainViewport().WorkPos + new Vector2(8f, 8f);
        drawList.PushClipRectFullScreen();
        var first = drawList.VtxBuffer.Size;
        drawList.AddText(font, fontSize, drawnAt, color, label);
        var last = drawList.VtxBuffer.Size;
        var vertices = drawList.VtxBuffer.Data;
        for (var i = first; i < last; i++)
            vertices[i].Pos = DockMath.TurnUpAndPlace(vertices[i].Pos, drawnAt, pivot);
        drawList.PopClipRect();
    }

    /// <summary>Draws the open tab's panel beside it. True while the pointer is on it or it is mid-interaction.</summary>
    private bool DrawPanel(Tab tab, DockEdge edge, Vector2 tabMin, Vector2 tabSize)
    {
        var viewport = ImGui.GetMainViewport();
        var (pos, pivot) = DockMath.PanelPlacement(
            edge, tabMin, tabSize, tab.FixedSize ?? _panelSize, viewport.WorkPos, viewport.WorkSize, PanelGap);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always, pivot);
        ImGui.SetNextWindowBgAlpha(CharonTheme.PanelAlpha);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoFocusOnAppearing;
        if (tab.FixedSize is { } fixedSize)
            ImGui.SetNextWindowSize(fixedSize, ImGuiCond.Always);
        else
            flags |= ImGuiWindowFlags.AlwaysAutoResize;

        var busy = false;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 8f));
        if (ImGui.Begin($"##CharonEdgeDockPanel{tab.Id}", flags))
        {
            try
            {
                tab.DrawPanel();
            }
            catch (Exception ex)
            {
                // Caught HERE, not let through: an exception leaving between Begin and End would skip the
                // End and corrupt ImGui's window stack for every plugin drawing after this one.
                ImGui.TextColored(CharonTheme.StatusRed, $"This panel could not be drawn: {ex.Message}");
            }

            _panelSize = ImGui.GetWindowSize();

            // The panel also counts as "in use" while one of its own pop-ups is open or a control is
            // held: a combo's list is a separate window, and losing the panel under it closes the combo.
            busy = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                         | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                         | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                   || ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
                   || ImGui.IsAnyItemActive();
        }

        ImGui.End();
        ImGui.PopStyleVar();
        return busy;
    }
}
