using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Charon.Windows.Components;

/// <summary>Full-width sidebar entry: icon + label, accent bar on the left when selected, optional badge on the right.</summary>
internal static class SidebarTab
{
    public static bool Draw(string label, FontAwesomeIcon icon, bool selected, string? badge = null, Vector4? badgeColor = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var height = Layout.NavRowHeight * scale;
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(width, height);
        var dl = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(origin, end);
        var accent = CharonTheme.Accent;

        var bg = selected
            ? Vector4.Lerp(CharonTheme.CardBg, accent, 0.18f)
            : hovered ? CharonTheme.CardBgHover : new Vector4(0, 0, 0, 0);
        dl.AddRectFilled(origin, end, ImGui.GetColorU32(bg), 5f * scale);
        if (selected)
            dl.AddRectFilled(origin, new Vector2(origin.X + 3f * scale, end.Y), ImGui.GetColorU32(accent), 1f);

        var padX = 11f * scale;
        var iconStr = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(iconStr);

        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX, origin.Y + (height - iconSize.Y) * 0.5f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, selected ? accent : CharonTheme.TextSecondary))
            ImGui.TextUnformatted(iconStr);

        var labelSize = ImGui.CalcTextSize(label);
        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX + iconSize.X + 8f * scale, origin.Y + (height - labelSize.Y) * 0.5f));
        using (ImRaii.PushColor(ImGuiCol.Text, selected ? CharonTheme.TextStrong : CharonTheme.TextSecondary))
            ImGui.TextUnformatted(label);

        if (!string.IsNullOrEmpty(badge))
        {
            ImGui.SetWindowFontScale(0.78f);
            var bs = ImGui.CalcTextSize(badge);
            var bh = bs.Y + 4f * scale;
            var bw = bs.X + 10f * scale;
            var bo = new Vector2(end.X - bw - 7f * scale, origin.Y + (height - bh) * 0.5f);
            var color = badgeColor ?? CharonTheme.AccentAmber;
            dl.AddRectFilled(bo, bo + new Vector2(bw, bh), ImGui.GetColorU32(Vector4.Lerp(CharonTheme.CardBg, color, 0.25f)), bh * 0.5f);
            dl.AddText(bo + new Vector2(5f * scale, 2f * scale), ImGui.GetColorU32(color), badge);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));

        if (!hovered)
            return false;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }
}
