using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Charon.Features.Retainers;
using Charon.Services.Game;
using Charon.Windows.Components;

namespace Charon.Windows;

/// <summary>
/// The at-the-bell overlay: while the game's retainer list is open, one line per retainer with the two
/// buttons that matter and the venture it will be sent on — so the whole decision is visible at the moment
/// the bell is being used, without opening the board.
///
/// It dies with the addon. Nothing here is clickable when the bell is closed, because nothing here exists:
/// the window's visibility IS the addon's visibility.
/// </summary>
public sealed unsafe class RetainerBellOverlay : Window
{
    private const string BellAddon = "RetainerList";

    private readonly IGameGui _gameGui;
    private readonly RetainerReader _retainers;
    private readonly RetainerPlanner _planner;
    private readonly VentureRunner _runner;
    private readonly Func<ulong> _contentId;
    private readonly Action _openBoard;
    private readonly IPluginLog _log;
    private DateTime _lastLoggedUtc = DateTime.MinValue;

    public RetainerBellOverlay(
        IGameGui gameGui,
        RetainerReader retainers,
        RetainerPlanner planner,
        VentureRunner runner,
        Func<ulong> contentId,
        Action openBoard,
        IPluginLog log)
        : base("##CharonRetainerBell")
    {
        _gameGui = gameGui;
        _retainers = retainers;
        _planner = planner;
        _runner = runner;
        _contentId = contentId;
        _openBoard = openBoard;
        _log = log;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoMove;
        RespectCloseHotkey = false;
        IsOpen = false;

        // Drawn above other plugins' windows. This panel is a decision aid for a GAME window that is open right
        // now, and it is placed beside it — which is exactly where another plugin's panel can be sitting (an
        // Allagan Market window covers that spot). Being the one thing the player opened on purpose, it wins.
        IsTopMost = true;
    }

    public override void PreDraw()
    {
        var addon = _gameGui.GetAddonByName(BellAddon);
        if (addon.IsNull)
            return;

        var unit = (AtkUnitBase*)addon.Address;
        var node = unit->RootNode;
        if (node == null)
            return;

        // Just inside the bell list's right edge, so the retainer names under it stay readable — and CLAMPED into
        // the viewport, because a position derived from the game window can land outside it (a list dragged to the
        // edge, a scaled HUD) and a window parked off-screen looks exactly like a window that never opened.
        var scale = unit->Scale;
        var viewport = ImGui.GetMainViewport();
        const float assumedWidth = 420f;
        const float assumedHeight = 150f;

        var x = node->ScreenX + (node->Width * scale) + 8f;
        var y = node->ScreenY;

        var maxX = viewport.WorkPos.X + viewport.WorkSize.X - assumedWidth;
        var maxY = viewport.WorkPos.Y + viewport.WorkSize.Y - assumedHeight;

        Position = new System.Numerics.Vector2(
            Math.Clamp(x, viewport.WorkPos.X, Math.Max(viewport.WorkPos.X, maxX)),
            Math.Clamp(y, viewport.WorkPos.Y, Math.Max(viewport.WorkPos.Y, maxY)));
    }

    public override void Draw()
    {
        // Say where this actually landed, at most twice a minute. "Nothing appeared" and "it appeared at
        // 12000,300" look identical to the player, and only one of them is a drawing problem.
        if (DateTime.UtcNow - _lastLoggedUtc > TimeSpan.FromSeconds(30))
        {
            _lastLoggedUtc = DateTime.UtcNow;
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            _log.Debug("[Retainers] bell overlay drawn at {0},{1} size {2}x{3} (viewport {4}x{5})",
                pos.X, pos.Y, size.X, size.Y,
                ImGui.GetMainViewport().WorkSize.X, ImGui.GetMainViewport().WorkSize.Y);
        }

        var rows = _retainers.Read(DateTime.UtcNow);

        if (_runner.Armed)
        {
            if (Buttons.Action("Stop", true, 90f, CharonTheme.AccentRose))
                _runner.Stop("stopped");

            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDim, "working");
        }
        else
        {
            if (Buttons.Action("Send a retainer", rows.Count > 0, 120f))
            {
                _runner.Plan(0);
                _runner.Arm();
            }

            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextMuted, "then pick who at the bell");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Board"))
            _openBoard();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the retainer board: plans, the venture list, the farm list");

        ImGui.Spacing();

        if (rows.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled,
                _retainers.Loaded ? "No retainers read yet." : _retainers.Status);
            return;
        }

        foreach (var row in rows)
        {
            var key = _planner.Key(_contentId(), row.Name);
            var option = _planner.Resolve(row, key);
            var mode = _planner.Mode(key);
            var ready = row.CompleteUtc is { } t && t <= DateTime.UtcNow;

            ImGui.TextColored(CharonTheme.TextSecondary, row.Name);
            ImGui.SameLine(92f);

            ImGui.TextColored(ready ? CharonTheme.StatusGreen : CharonTheme.TextDim,
                ready ? "ready" : Describe(row));
            ImGui.SameLine(158f);

            ImGui.TextColored(mode == VentureAssignment.Off ? CharonTheme.TextMuted : CharonTheme.TextDim,
                option != null ? $"→ {option.Venture.Name}" : "→ as before");

            ImGui.SameLine(330f);
            if (Buttons.Action(ready ? "Collect" : "Collect##bellidle", ready, 68f))
            {
                _runner.Plan(0);
                _runner.Arm();
            }

            ImGui.SameLine();
            if (Buttons.Action("Send##bell", row.VentureId == 0, 60f))
            {
                _runner.Plan(_planner.PlanTaskId(row, key));
                _runner.Arm();
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextDisabled, _runner.Status);
    }

    private static string Describe(RetainerVenture row)
    {
        if (row.CompleteUtc is not { } done)
            return row.VentureId == 0 ? "idle" : "out";

        var left = done - DateTime.UtcNow;
        return left <= TimeSpan.Zero ? "ready" : $"{left.Hours}h {left.Minutes:00}m";
    }
}
