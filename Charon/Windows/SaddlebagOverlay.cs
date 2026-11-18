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
        // Whichever of the two saddlebag windows is up (the second one is the saddlebag beside a
        // retainer's inventory).
        if (_saddlebag.OpenAddonName() is not { } name)
            return;
        var addon = _gameGui.GetAddonByName(name);
        if (addon.IsNull)
            return;

        var unit = (AtkUnitBase*)addon.Address;
        var scale = unit->Scale;
        var node = unit->GetNodeById(AnchorNodeId);
        if (node != null)
        {
            // Bottom bar of the saddlebag, just under its right edge — where Pandora puts theirs.
            Position = new Vector2(
                node->ScreenX + (node->Width * scale) - 170f,
                node->ScreenY + (node->Height * scale) + 2f);
            return;
        }

        // A saddlebag window without that bar: under the window's own bottom-right corner.
        var root = unit->RootNode;
        if (root == null)
            return;
        Position = new Vector2(
            unit->X + (root->Width * scale) - 170f,
            unit->Y + (root->Height * scale) + 2f);
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
