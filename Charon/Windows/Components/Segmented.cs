using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Charon.Windows.Components;

/// <summary>
/// Segmented selector for a small set of mutually exclusive states (Quick Kill's role, the movement
/// provider). A Charon component rather than an Argus one: the settings that matter most are picked
/// with one click instead of a combo box.
/// </summary>
internal static class Segmented
{
    /// <returns>The chosen index, or the incoming <paramref name="current"/> when nothing was picked.</returns>
    public static int Draw(string[] options, int current, string? tooltip = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var height = 20f * scale;
        var padX = 9f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        var widths = new float[options.Length];
        var total = 0f;
        for (var i = 0; i < options.Length; i++)
        {
            widths[i] = ImGui.CalcTextSize(options[i]).X + padX * 2f;
            total += widths[i];
        }

        var chosen = current;
        var hovered = ImGui.IsMouseHoveringRect(origin, origin + new Vector2(total, height));
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        dl.AddRectFilled(origin, origin + new Vector2(total, height), ImGui.GetColorU32(CharonTheme.CardBgSoft), 5f * scale);
        dl.AddRect(origin, origin + new Vector2(total, height), ImGui.GetColorU32(CharonTheme.Border), 5f * scale);

        var x = origin.X;
        for (var i = 0; i < options.Length; i++)
        {
            var cellMin = new Vector2(x, origin.Y);
            var cellMax = new Vector2(x + widths[i], origin.Y + height);
            var cellHovered = ImGui.IsMouseHoveringRect(cellMin, cellMax);

            if (i == current)
            {
                dl.AddRectFilled(cellMin, cellMax,
                    ImGui.GetColorU32(CharonTheme.WithAlpha(CharonTheme.Accent, 0.45f)), 4f * scale);
            }
            else if (cellHovered)
            {
                dl.AddRectFilled(cellMin, cellMax, ImGui.GetColorU32(CharonTheme.CardBgHover), 4f * scale);
            }

            var label = options[i];
            var labelSize = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(x + (widths[i] - labelSize.X) * 0.5f, origin.Y + (height - labelSize.Y) * 0.5f),
                ImGui.GetColorU32(i == current ? CharonTheme.TextStrong : CharonTheme.TextDim), label);

            if (i > 0)
                dl.AddLine(new Vector2(x, origin.Y + 3f), new Vector2(x, origin.Y + height - 3f),
                    ImGui.GetColorU32(CharonTheme.Border), 1f);

            if (cellHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                chosen = i;

            x += widths[i];
        }

        ImGui.Dummy(new Vector2(total, height));
        if (tooltip != null)
            Styling.Tooltip(tooltip);

        return chosen;
    }
}
