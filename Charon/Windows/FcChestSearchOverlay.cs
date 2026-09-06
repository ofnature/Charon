using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Charon.Services.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Windows;

/// <summary>
/// A search bar riding the game's Free Company chest window (the FCCH idea, reimplemented):
/// a frameless ImGui input anchored over the addon's title bar; whatever is typed dims the
/// slots that don't match via <see cref="ChestSearchFilter"/>. Opened/closed by the plugin
/// from the addon's visibility, so it can never outlive the window it decorates.
/// </summary>
public sealed unsafe class FcChestSearchOverlay : Window
{
    private const string ChestAddonName = "FreeCompanyChest";
    private const float BarWidth = 200f;

    private readonly IGameGui _gameGui;

    public FcChestSearchOverlay(IGameGui gameGui)
        : base("##CharonFcChestSearch")
    {
        _gameGui = gameGui;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoMove;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    /// <summary>The live query, read by the plugin tick and fed to the filter.</summary>
    public string Query { get; private set; } = string.Empty;

    public override void PreDraw()
    {
        var addon = _gameGui.GetAddonByName(ChestAddonName);
        if (addon.IsNull)
            return;

        var unit = (AtkUnitBase*)addon.Address;
        var header = unit->WindowHeaderCollisionNode;
        var root = unit->RootNode;
        if (header == null || root == null)
            return;

        // Centered on the title bar, like the game's own retainer/inventory search fields.
        var scale = unit->Scale;
        var barWidth = MathF.Min(BarWidth * scale, header->Width * scale * 0.5f);
        var x = unit->X + (root->Width / 2f) * scale - barWidth / 2f;
        var y = unit->Y + (header->Y + header->Height / 2f) * scale - 12f;

        Position = new Vector2(x, y);
        PositionCondition = ImGuiCond.Always;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(barWidth, 0),
            MaximumSize = new Vector2(barWidth, 60f),
        };
    }

    public override void Draw()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.05f, 0.05f, 0.07f, 0.85f));
        var query = Query;
        if (ImGui.InputTextWithHint("##fcsearch", "search chest…", ref query, 64))
            Query = query;
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    public override void OnClose() => Query = string.Empty;
}
