using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Charon.Windows;

/// <summary>
/// Shared palette + helpers — same Greek-pantheon dark/gold identity as Daedalus
/// (see the Daedalus ImGui design skill).
/// </summary>
internal static class CharonTheme
{
    // Background layers
    public static readonly Vector4 BgDeep = new(0.08f, 0.08f, 0.10f, 1.00f);
    public static readonly Vector4 BgPanel = new(0.12f, 0.12f, 0.15f, 1.00f);
    public static readonly Vector4 BgRow = new(0.15f, 0.15f, 0.18f, 0.60f);

    // Accent — gold/amber
    public static readonly Vector4 AccentGold = new(0.85f, 0.65f, 0.20f, 1.00f);
    public static readonly Vector4 AccentDim = new(0.55f, 0.42f, 0.13f, 1.00f);

    // Status colors
    public static readonly Vector4 StatusGreen = new(0.20f, 0.75f, 0.35f, 1.00f);
    public static readonly Vector4 StatusYellow = new(0.85f, 0.75f, 0.10f, 1.00f);
    public static readonly Vector4 StatusRed = new(0.85f, 0.25f, 0.20f, 1.00f);
    public static readonly Vector4 StatusGrey = new(0.45f, 0.45f, 0.50f, 1.00f);

    // Text
    public static readonly Vector4 TextPrimary = new(0.92f, 0.90f, 0.85f, 1.00f);
    public static readonly Vector4 TextSecondary = new(0.60f, 0.58f, 0.55f, 1.00f);
    public static readonly Vector4 TextDisabled = new(0.35f, 0.35f, 0.38f, 1.00f);

    /// <summary>
    /// Gold-accented section header. This Dalamud ImGui binding has no SeparatorText, so it is
    /// hand-drawn: gold label with a hairline continuing to the right edge (same as Daedalus).
    /// </summary>
    public static void SectionHeader(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(AccentGold, label);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineY = (min.Y + max.Y) / 2f;
        var lineStart = new Vector2(max.X + 8f, lineY);
        var lineEnd = new Vector2(
            ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X, lineY);
        if (lineEnd.X > lineStart.X)
            ImGui.GetWindowDrawList().AddLine(lineStart, lineEnd,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.20f, 0.24f, 1f)), 1f);
        ImGui.Spacing();
    }

    /// <summary>Colored status dot + label (green = active, grey = disabled).</summary>
    public static void StatusDot(bool active, string activeLabel = "Active", string inactiveLabel = "Disabled")
    {
        ImGui.TextColored(active ? StatusGreen : StatusGrey, "●");
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(active ? StatusGreen : TextSecondary, active ? activeLabel : inactiveLabel);
    }

    /// <summary>Hover "(?)" tooltip for a non-obvious control.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
