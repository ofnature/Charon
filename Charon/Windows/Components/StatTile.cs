using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Charon.Windows.Components;

/// <summary>One metric: dim uppercase label on top, strong value bottom-left, optional dim sub bottom-right, accent tick on the left.</summary>
internal static class StatTile
{
    public static void Draw(string label, string value, string? sub, Vector4 accent, float width, string? tooltip = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var height = Layout.StatTileHeight * scale;
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(width, height);
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, end, ImGui.GetColorU32(CharonTheme.CardBgSoft), 5f * scale);
        dl.AddRect(origin, end, ImGui.GetColorU32(CharonTheme.WithAlpha(CharonTheme.BorderDim, 0.6f)), 5f * scale);
        dl.AddRectFilled(origin, new Vector2(origin.X + 3f * scale, end.Y), ImGui.GetColorU32(CharonTheme.WithAlpha(accent, 0.85f)), 2f);

        var padX = 9f * scale;
        var padY = 6f * scale;

        ImGui.SetWindowFontScale(0.72f);
        dl.AddText(new Vector2(origin.X + padX, origin.Y + padY), ImGui.GetColorU32(CharonTheme.TextDim), label.ToUpperInvariant());
        ImGui.SetWindowFontScale(1.15f);
        var valueText = Styling.FitText(value, width - padX * 2f);
        var valSize = ImGui.CalcTextSize(valueText);
        var valY = end.Y - padY - valSize.Y;
        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX, valY));
        Styling.Text(valueText, CharonTheme.TextStrong);
        ImGui.SetWindowFontScale(1f);

        if (!string.IsNullOrEmpty(sub))
        {
            ImGui.SetWindowFontScale(0.72f);
            var subText = Styling.FitText(sub, width - padX * 2f);
            var subSize = ImGui.CalcTextSize(subText);

            // Drop the sub rather than let it collide with the value — a narrower window is the only
            // thing that can make these two touch, and the tooltip still carries the full text.
            if (valSize.X + subSize.X + 10f * scale <= width - padX * 2f)
                dl.AddText(new Vector2(end.X - padX - subSize.X, valY + valSize.Y - subSize.Y),
                    ImGui.GetColorU32(CharonTheme.TextDim), subText);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        if (tooltip != null)
            Styling.Tooltip(tooltip);
    }
}
