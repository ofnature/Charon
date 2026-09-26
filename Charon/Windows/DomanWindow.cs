using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Services.Game;

namespace Charon.Windows;

/// <summary>
/// Standalone Doman donation window that pops up with the game's donation basket, so the two steps are
/// there when you are standing at the basket instead of three clicks away in the main window. Auto-opened
/// and closed by the ReconstructionBox addon lifecycle (see CharonPlugin) — with one deliberate exception:
/// it also stays up while an operation is running or a split stack is waiting, because Prepare CLOSES the
/// basket on purpose and closing the window with it would take the Stage button away exactly when it is
/// the next move. Renders the same body as the main window's Doman Donate section.
/// </summary>
public sealed class DomanWindow : Window
{
    private readonly CharonConfig _config;
    private readonly Action _save;
    private readonly DomanDonator _doman;
    private readonly GilCapSeller _gilSeller;

    public DomanWindow(CharonConfig config, Action save, DomanDonator doman, GilCapSeller gilSeller)
        : base("Charon — Doman Donate##CharonDoman")
    {
        BgAlpha = CharonTheme.PanelAlpha;
        _config = config;
        _save = save;
        _doman = doman;
        _gilSeller = gilSeller;

        Size = new Vector2(380, 330);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 250),
            MaximumSize = new Vector2(640, 700),
        };
    }

    /// <summary>
    /// Whether this window wants to be up: the basket is open, or the flow is mid-way. See the class doc
    /// for why it cannot simply follow the basket.
    /// </summary>
    public static bool WantsOpen(bool basketOpen, DomanDonator doman) =>
        basketOpen || doman.Busy || doman.StackReady;

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        try
        {
            DomanView.DrawBody(_config, _save, _doman, _gilSeller);
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }
}
