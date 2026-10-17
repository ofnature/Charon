using System;
using System.Numerics;
using Charon.Services.Game;
using Charon.Windows.Components;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Charon.Windows;

/// <summary>
/// The in-dungeon route guide: the route being run, step by step, for the variant dungeon you are in.
/// Opened ONCE on entering a variant dungeon (edge-triggered by the plugin, so closing it sticks for the
/// run) and from the Variant section's button. Same body as that section, compact.
/// </summary>
public sealed class VariantGuideWindow : Window
{
    private readonly VariantService _service;
    private readonly Func<string, string> _display;

    public VariantGuideWindow(VariantService service, Func<string, string> display)
        : base("Charon — Variant route##CharonVariant")
    {
        BgAlpha = CharonTheme.PanelAlpha;
        _service = service;
        _display = display;

        Size = new Vector2(400, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 220),
            MaximumSize = new Vector2(700, 900),
        };
    }

    public override void Draw()
    {
        var dungeon = _service.Current;
        if (dungeon == null)
        {
            Styling.TextWrapped("Not in a variant dungeon. The Variant section in Charon's main window has every "
                                + "dungeon's routes.", CharonTheme.TextMuted);
            return;
        }

        Styling.Text(dungeon.Name, CharonTheme.TextStrong);
        VariantView.DrawDungeon(_service, dungeon, compact: true, _display);
    }
}
