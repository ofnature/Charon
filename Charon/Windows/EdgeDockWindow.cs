using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Charon.Features.Dock;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Charon.Windows;

/// <summary>
/// The edge dock: context tabs pinned to a screen edge with sideways labels, each popping its panel
/// out beside it (see <see cref="DockTabs"/> and <see cref="DockHover"/> for the rules).
///
/// A tab's panel is the BODY of the context window it stands for — the window's own <c>Draw()</c>,
/// called inside the pop-out — so the dock and the windows cannot drift apart. While the dock is on,
/// those windows no longer open by themselves; their tabs are how you reach them.
///
/// The sideways label is ordinary text whose vertices are turned after it is drawn
/// (<see cref="DockMath"/>). It has to be drawn under a full-screen clip rect: ImGui culls glyphs
/// against the clip rect BEFORE they are turned, and the unrotated text runs far outside a 34-pixel tab.
/// </summary>
public sealed unsafe class EdgeDockWindow : Window
{
    /// <param name="FixedSize">A panel whose content stretches (a table with stretch columns) needs a real size; null lets it auto-size.</param>
    public sealed record Tab(DockTab Id, Func<bool> Applies, Action DrawPanel, Vector2? FixedSize = null);

    private const float TabWidth = 34f;
    private const float TabPadding = 14f;
    private const float TabGap = 4f;
    private const float PanelGap = 6f;
    private const float LabelScale = 1.25f;
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(450);

    private readonly IReadOnlyList<Tab> _tabs;
    private readonly Func<bool> _rightSide;
    private readonly DockLayoutShare? _layout;
    private readonly DockHover _hover = new();
    private List<Tab> _visible = new();
    private Vector2 _panelSize = new(300f, 200f);

    /// <param name="layout">
    /// The layout shared with other plugins' docks (Daedalus), so docks on the same edge stack instead of
    /// overlapping. Null centres this dock on its own.
    /// </param>
    public EdgeDockWindow(IReadOnlyList<Tab> tabs, Func<bool> rightSide, DockLayoutShare? layout = null)
        : base("##CharonEdgeDock")
    {
        _tabs = tabs;
        _rightSide = rightSide;
        _layout = layout;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar;
        RespectCloseHotkey = false;
        PositionCondition = ImGuiCond.Always;
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

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        var viewport = ImGui.GetMainViewport();
        var total = _visible.Sum(t => TabHeight(t.Id)) + TabGap * Math.Max(0, _visible.Count - 1);
        var right = _rightSide();
        var x = right ? viewport.WorkPos.X + viewport.WorkSize.X - TabWidth : viewport.WorkPos.X;
        var y = _layout?.Place(right, total, viewport.WorkPos.Y, viewport.WorkSize.Y)
                ?? viewport.WorkPos.Y + (viewport.WorkSize.Y - total) / 2f;
        Position = new Vector2(x, y);
    }

    public override void PostDraw() => ImGui.PopStyleVar();

    public override void Draw()
    {
        var drawList = ImGui.GetWindowDrawList();
        var right = _rightSide();
        DockTab? hovered = null;
        Tab? openTab = null;
        var openMin = Vector2.Zero;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, TabGap));
        foreach (var tab in _visible)
        {
            var size = new Vector2(TabWidth, TabHeight(tab.Id));
            var min = ImGui.GetCursorScreenPos();

            if (ImGui.InvisibleButton($"##dock{tab.Id}", size))
                _hover.Click(tab.Id);
            if (ImGui.IsItemHovered())
                hovered = tab.Id;

            var isOpen = _hover.Open == tab.Id;
            var pinned = _hover.Pinned == tab.Id;
            DrawTab(drawList, min, size, DockTabs.Label(tab.Id), isOpen || hovered == tab.Id, pinned, right);

            if (isOpen)
            {
                openTab = tab;
                openMin = min;
            }
        }

        ImGui.PopStyleVar();

        var panelBusy = openTab != null && DrawPanel(openTab, openMin, right);
        _hover.Update(hovered, panelBusy, DateTime.UtcNow, CloseDelay, _visible.Select(t => t.Id).ToList());

        Status = $"{_visible.Count} tab(s): {string.Join(", ", _visible.Select(t => DockTabs.Label(t.Id)))}"
                 + (_hover.Open is { } open ? $" · {DockTabs.Label(open)} out{(_hover.Pinned == open ? " (pinned)" : "")}" : "");
    }

    private static float TabHeight(DockTab tab) =>
        MathF.Max(90f, ImGui.CalcTextSize(DockTabs.Label(tab)).X * LabelScale + TabPadding * 2f);

    private static void DrawTab(ImDrawListPtr drawList, Vector2 min, Vector2 size, string label, bool lit, bool pinned, bool right)
    {
        var max = min + size;
        var fill = lit ? new Vector4(0.16f, 0.17f, 0.20f, 0.96f) : new Vector4(0.09f, 0.10f, 0.12f, 0.88f);
        // Rounded on the side facing the screen, square against the edge it is pinned to.
        var corners = right ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersRight;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 6f, corners);

        if (pinned)
        {
            // The accent bar the sidebar uses for "selected", on the tab's outer edge.
            var barMin = right ? new Vector2(min.X, min.Y) : new Vector2(max.X - 2f, min.Y);
            drawList.AddRectFilled(barMin, barMin + new Vector2(2f, size.Y), ImGui.GetColorU32(CharonTheme.Accent));
        }

        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * LabelScale;
        var textSize = ImGui.CalcTextSize(label) * LabelScale;

        // Turned, the label is textSize.Y wide and textSize.X tall — centred in the tab.
        var topLeft = min + new Vector2((size.X - textSize.Y) / 2f, (size.Y - textSize.X) / 2f);
        var pivot = DockMath.PivotFor(topLeft, textSize);
        var color = ImGui.GetColorU32(lit ? CharonTheme.TextPrimary : CharonTheme.TextSecondary);

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
    private bool DrawPanel(Tab tab, Vector2 tabMin, bool right)
    {
        var viewport = ImGui.GetMainViewport();
        var size = tab.FixedSize ?? _panelSize;

        // Beside the tab, pulled up if it would run off the bottom of the screen.
        var x = right ? tabMin.X - PanelGap : tabMin.X + TabWidth + PanelGap;
        var bottom = viewport.WorkPos.Y + viewport.WorkSize.Y - size.Y - 4f;
        var y = MathF.Max(viewport.WorkPos.Y + 4f, MathF.Min(tabMin.Y, bottom));

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always, new Vector2(right ? 1f : 0f, 0f));
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
