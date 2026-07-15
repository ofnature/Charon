using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Services.Game;

namespace Charon.Windows;

/// <summary>
/// Standalone FC chest window that pops up alongside the game's Free Company chest so the
/// entrust/withdraw tools are right there without opening the main Charon window. Auto-opened
/// and closed by the FreeCompanyChest addon lifecycle (see CharonPlugin); can also be pinned
/// open. Renders the same body as the main window's FC Chest section.
/// </summary>
public sealed class FcChestWindow : Window
{
    private readonly CharonConfig _config;
    private readonly Action _save;
    private readonly FcChestManager _fcChest;

    public FcChestWindow(CharonConfig config, Action save, FcChestManager fcChest)
        : base("Charon — FC Chest##CharonFcChest")
    {
        _config = config;
        _save = save;
        _fcChest = fcChest;

        Size = new Vector2(380, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 300),
            MaximumSize = new Vector2(700, 900),
        };
    }

    /// <summary>Keep the minimum size in step with the text scale so bigger text can't clip.</summary>
    public override void PreDraw()
    {
        var scale = Math.Clamp(_config.FcChestFontScale, FcChestView.MinFontScale, FcChestView.MaxFontScale);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320f * scale, 300f),
            MaximumSize = new Vector2(900f, 1000f),
        };
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        try
        {
            FcChestView.DrawBody(_config, _save, _fcChest);
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }
}
