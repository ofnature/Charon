using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Charon.Services.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Windows;

/// <summary>
/// The "Entrust duplicates" button, riding ON the game's saddlebag window the way Pandora's Box
/// draws its own (BSD-3-Clause, PunishXIV/PandorasBox — including their anchor: node 83, the
/// InventoryBuddy bottom bar). A frameless ImGui window repositioned to that node every frame;
/// opened and closed by the plugin from the addon's visibility, so it can never outlive the
/// window it decorates.
/// </summary>
public sealed unsafe class SaddlebagOverlay : Window
{
    private const string SaddlebagAddon = "InventoryBuddy";
    private const uint AnchorNodeId = 83;

    private readonly IGameGui _gameGui;
    private readonly SaddlebagEntruster _saddlebag;

    public SaddlebagOverlay(IGameGui gameGui, SaddlebagEntruster saddlebag)
        : base("##CharonSaddlebagOverlay")
    {
        _gameGui = gameGui;
        _saddlebag = saddlebag;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoMove;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void PreDraw()
    {
        var addon = _gameGui.GetAddonByName(SaddlebagAddon);
        if (addon.IsNull)
            return;

        var unit = (AtkUnitBase*)addon.Address;
        var node = unit->GetNodeById(AnchorNodeId);
        if (node == null)
            return;

        // Bottom bar of the saddlebag, just under its right edge — where Pandora puts theirs.
        var scale = unit->Scale;
        Position = new Vector2(
            node->ScreenX + (node->Width * scale) - 170f,
            node->ScreenY + (node->Height * scale) + 2f);
    }

    public override void Draw()
    {
        if (_saddlebag.Busy)
        {
            if (ImGui.Button("Stop##saddleOverlay"))
                _saddlebag.Cancel();
        }
        else
        {
            var dupes = _saddlebag.CountEntrustable();
            if (dupes == 0) ImGui.BeginDisabled();
            if (ImGui.Button($"Entrust duplicates ({dupes})##saddleOverlay"))
                _saddlebag.Start();
            if (dupes == 0) ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Move every bag stack the saddlebag ALREADY holds a copy of.\n"
                                 + "Unique items are skipped. The saddlebag copy is the seed —\n"
                                 + "nothing new is ever moved in.");
        }

        if (_saddlebag.Busy || _saddlebag.Status != "idle")
        {
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDisabled, _saddlebag.Status);
        }
    }
}
