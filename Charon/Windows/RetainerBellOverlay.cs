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

    public RetainerBellOverlay(
        IGameGui gameGui,
        RetainerReader retainers,
        RetainerPlanner planner,
        VentureRunner runner,
        Func<ulong> contentId,
        Action openBoard)
        : base("##CharonRetainerBell")
    {
        _gameGui = gameGui;
        _retainers = retainers;
        _planner = planner;
        _runner = runner;
        _contentId = contentId;
        _openBoard = openBoard;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoMove;
        RespectCloseHotkey = false;
        IsOpen = false;
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

        // Just inside the bell list's right edge, so the retainer names under it stay readable.
        var scale = unit->Scale;
        Position = new System.Numerics.Vector2(
            node->ScreenX + (node->Width * scale) + 8f,
            node->ScreenY);
    }

    public override void Draw()
    {
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
