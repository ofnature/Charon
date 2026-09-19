using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Charon.Windows;

/// <summary>
/// Charon's palette and shared helpers, in the Argus idiom: a dark slate surface ramp, four text
/// steps, and one brand accent (teal) with a small set of state colours.
///
/// Teal is the identity accent; amber means "something is waiting for you", mint is good, rose is a
/// problem, cyan/blue/violet are informational stripes. The old gold-era names are kept as aliases
/// where the sections still use them, so a colour change here re-skins every window at once.
/// </summary>
internal static class CharonTheme
{
    // Brand accent — teal (the "eyes" of the companion set).
    public static readonly Vector4 Accent = new(0.16f, 0.78f, 0.74f, 1.00f);
    public static readonly Vector4 AccentSoft = new(0.50f, 0.92f, 0.88f, 1.00f);

    // State colours.
    public static readonly Vector4 AccentAmber = new(0.92f, 0.74f, 0.34f, 1.00f);
    public static readonly Vector4 AccentMint = new(0.46f, 0.86f, 0.66f, 1.00f);
    public static readonly Vector4 AccentRose = new(0.93f, 0.42f, 0.50f, 1.00f);
    public static readonly Vector4 AccentCyan = new(0.47f, 0.94f, 0.92f, 1.00f);
    public static readonly Vector4 AccentBlue = new(0.40f, 0.68f, 0.98f, 1.00f);
    public static readonly Vector4 AccentViolet = new(0.62f, 0.42f, 0.96f, 1.00f);

    // Surfaces.
    public static readonly Vector4 BgDeep = new(0.055f, 0.059f, 0.075f, 1.00f);
    public static readonly Vector4 CardBg = new(0.098f, 0.110f, 0.133f, 0.90f);
    public static readonly Vector4 CardBgSoft = new(0.086f, 0.098f, 0.117f, 0.55f);
    public static readonly Vector4 CardBgHover = new(0.118f, 0.133f, 0.161f, 0.95f);

    /// <summary>Legacy name for the panel surface (kept: the sections paint with it).</summary>
    public static readonly Vector4 BgPanel = CardBg;

    /// <summary>Legacy name for the row wash (kept: the sections paint with it).</summary>
    public static readonly Vector4 BgRow = CardBgSoft;

    public static readonly Vector4 Border = new(0.22f, 0.26f, 0.30f, 1.00f);
    public static readonly Vector4 BorderDim = new(0.22f, 0.26f, 0.30f, 1.00f);
    public static readonly Vector4 Hairline = new(1f, 1f, 1f, 0.055f);

    // Text ramp.
    public static readonly Vector4 TextStrong = new(0.96f, 0.96f, 0.97f, 1.00f);
    public static readonly Vector4 TextSecondary = new(0.78f, 0.80f, 0.84f, 1.00f);
    public static readonly Vector4 TextDim = new(0.55f, 0.58f, 0.62f, 1.00f);
    public static readonly Vector4 TextMuted = new(0.40f, 0.42f, 0.46f, 1.00f);

    // Legacy text names.
    public static readonly Vector4 TextPrimary = new(0.96f, 0.96f, 0.97f, 1.00f);
    public static readonly Vector4 TextDisabled = new(0.40f, 0.42f, 0.46f, 1.00f);

    // Legacy status names.
    public static readonly Vector4 StatusGreen = AccentMint;
    public static readonly Vector4 StatusYellow = AccentAmber;
    public static readonly Vector4 StatusRed = AccentRose;
    public static readonly Vector4 StatusGrey = TextDim;

    // Geometry.
    public const float CardRounding = 7f;
    public const float FrameRounding = 5f;
    public const float WindowRounding = 7f;

    public static Vector4 WithAlpha(Vector4 c, float a) => c with { W = a };

    /// <summary>0..1 sine pulse on the given period, for "something is happening" glows.</summary>
    public static float Pulse(double periodMs = 800.0)
    {
        var t = (Environment.TickCount % periodMs) / periodMs;
        return (float)((Math.Sin(t * Math.PI * 2.0) + 1.0) * 0.5);
    }

    public static float Phase(double periodMs) => (float)((Environment.TickCount % periodMs) / periodMs);

    /// <summary>
    /// Section header: a dim small-cap label with a hairline running to the right edge. Kept at the
    /// old signature so every section keeps working; the colour now comes from the palette.
    /// </summary>
    public static void SectionHeader(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(TextDim, label.ToUpperInvariant());
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineY = (min.Y + max.Y) / 2f;
        var lineStart = new Vector2(max.X + 8f, lineY);
        var lineEnd = new Vector2(
            ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X, lineY);
        if (lineEnd.X > lineStart.X)
            ImGui.GetWindowDrawList().AddLine(lineStart, lineEnd, ImGui.GetColorU32(Hairline), 1f);
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
