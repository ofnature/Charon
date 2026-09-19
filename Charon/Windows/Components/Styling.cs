using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Charon.Windows.Components;

/// <summary>
/// Shared style pushes and text helpers, in the Argus idiom: everything is a small helper so a page
/// reads as a list of statements rather than a wall of PushStyleColor.
/// </summary>
internal static class Styling
{
    public static void VSpace(float pixels)
        => ImGui.Dummy(new Vector2(0, pixels * ImGuiHelpers.GlobalScale));

    public static void SectionLabel(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, CharonTheme.TextDim))
            ImGui.TextUnformatted(label.ToUpperInvariant());
    }

    public static void Text(string text, Vector4 color)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
    }

    /// <summary>Text that wraps at the content edge, for sentences rather than labels.</summary>
    public static void TextWrapped(string text, Vector4 color)
    {
        ImGui.PushTextWrapPos(0f);
        Text(text, color);
        ImGui.PopTextWrapPos();
    }

    public static void TextScaled(string text, Vector4 color, float fontScale)
    {
        ImGui.SetWindowFontScale(fontScale);
        Text(text, color);
        ImGui.SetWindowFontScale(1f);
    }

    public static void TextCentered(string text, Vector4 color, float fontScale = 1f)
    {
        if (fontScale != 1f)
            ImGui.SetWindowFontScale(fontScale);
        var w = ImGui.CalcTextSize(text).X;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > w)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - w) * 0.5f);
        Text(text, color);
        if (fontScale != 1f)
            ImGui.SetWindowFontScale(1f);
    }

    public static IDisposable PushWindowStyle()
    {
        var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, CharonTheme.FrameRounding);
        style.Push(ImGuiStyleVar.WindowRounding, CharonTheme.WindowRounding);
        style.Push(ImGuiStyleVar.ChildRounding, CharonTheme.CardRounding);
        style.Push(ImGuiStyleVar.ItemSpacing, new Vector2(6, 5) * ImGuiHelpers.GlobalScale);

        // Recolour the game's stock widgets into the palette. The section pages still use their
        // original ImGui controls (they are converted to the component set page by page), and without
        // this they would keep the game's default grey/blue chrome next to the new cards.
        var colors = ImRaii.PushColor(ImGuiCol.CheckMark, CharonTheme.Accent)
            .Push(ImGuiCol.FrameBg, CharonTheme.CardBgSoft)
            .Push(ImGuiCol.FrameBgHovered, CharonTheme.CardBgHover)
            .Push(ImGuiCol.FrameBgActive, CharonTheme.WithAlpha(CharonTheme.Accent, 0.30f))
            .Push(ImGuiCol.Button, CharonTheme.WithAlpha(CharonTheme.Accent, 0.55f))
            .Push(ImGuiCol.ButtonHovered, CharonTheme.WithAlpha(CharonTheme.Accent, 0.75f))
            .Push(ImGuiCol.ButtonActive, CharonTheme.Accent)
            .Push(ImGuiCol.Header, CharonTheme.WithAlpha(CharonTheme.Accent, 0.22f))
            .Push(ImGuiCol.HeaderHovered, CharonTheme.WithAlpha(CharonTheme.Accent, 0.30f))
            .Push(ImGuiCol.HeaderActive, CharonTheme.WithAlpha(CharonTheme.Accent, 0.38f))
            .Push(ImGuiCol.SliderGrab, CharonTheme.Accent)
            .Push(ImGuiCol.SliderGrabActive, CharonTheme.AccentSoft)
            .Push(ImGuiCol.Separator, CharonTheme.Hairline)
            .Push(ImGuiCol.ScrollbarGrab, CharonTheme.BorderDim);

        return new CombinedStyle(style, colors);
    }

    /// <summary>Disposes two ImRaii scopes as one (style vars + colours cannot share a single stack).</summary>
    private sealed class CombinedStyle : IDisposable
    {
        private readonly IDisposable _style;
        private readonly IDisposable _colors;

        public CombinedStyle(IDisposable style, IDisposable colors)
        {
            _style = style;
            _colors = colors;
        }

        public void Dispose()
        {
            _colors.Dispose();
            _style.Dispose();
        }
    }

    public static IDisposable PushCardStyle()
    {
        var p = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, CharonTheme.CardRounding * ImGuiHelpers.GlobalScale);
        p.Push(ImGuiStyleVar.ChildBorderSize, 1f);
        p.Push(ImGuiStyleVar.WindowPadding, new Vector2(10, 7) * ImGuiHelpers.GlobalScale);
        p.Push(ImGuiStyleVar.FrameRounding, CharonTheme.FrameRounding);
        return p;
    }

    public static void Tooltip(string text, float wrapWidth = 300f)
    {
        if (!ImGui.IsItemHovered())
            return;

        using var tip = ImRaii.Tooltip();
        ImGui.PushTextWrapPos(wrapWidth * ImGuiHelpers.GlobalScale);
        Text(text, CharonTheme.TextSecondary);
        ImGui.PopTextWrapPos();
    }

    /// <summary>
    /// Trim text to fit a width, adding an ellipsis when anything was dropped. Respects the current
    /// window font scale, so call it with the same scale the text is drawn at. Every place that puts
    /// text beside other text inside a fixed-width card needs this: a status line that fits at 1080px
    /// overruns its tile at 940px, and it overruns into its neighbour rather than wrapping.
    /// </summary>
    public static string FitText(string text, float maxWidth)
    {
        if (maxWidth <= 0f || ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + "…";
            if (ImGui.CalcTextSize(candidate).X <= maxWidth)
                return candidate;
        }

        return "…";
    }
}
