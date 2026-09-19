using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Charon.Windows.Components;

/// <summary>
/// A card of settings rows. Rows are label-left / control-right with hairlines between them and a hover
/// help tooltip. The card background is drawn after the content so it can size to fit.
/// </summary>
internal sealed class SettingsGroup : IDisposable
{
    private const float PaddingX = 10f;
    private const float PaddingY = 5f;
    private const float GroupGap = 10f;
    private const float RowHeight = 27f;

    private readonly Vector2 origin;
    private readonly float width;
    private bool firstRow = true;

    public static SettingsGroup Begin(string title)
    {
        if (title.Length > 0)
        {
            Styling.SectionLabel(title);
            Styling.VSpace(2f);
        }

        return new SettingsGroup();
    }

    private SettingsGroup()
    {
        var scale = ImGuiHelpers.GlobalScale;
        origin = ImGui.GetCursorScreenPos();
        width = ImGui.GetContentRegionAvail().X;

        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(origin + new Vector2(PaddingX, PaddingY) * scale);
        ImGui.BeginGroup();
    }

    private float RightEdge => origin.X + width - PaddingX * ImGuiHelpers.GlobalScale;

    public void Dispose()
    {
        ImGui.EndGroup();
        var scale = ImGuiHelpers.GlobalScale;
        var end = new Vector2(origin.X + width, ImGui.GetItemRectMax().Y + PaddingY * scale);

        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);
        var rounding = CharonTheme.CardRounding * scale;
        dl.AddRectFilled(origin, end, ImGui.GetColorU32(CharonTheme.CardBgSoft), rounding);
        dl.AddRect(origin, end, ImGui.GetColorU32(CharonTheme.WithAlpha(CharonTheme.BorderDim, 0.55f)), rounding);
        dl.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(origin.X, end.Y));
        ImGui.Dummy(new Vector2(width, 0f));
        Styling.VSpace(GroupGap);
    }

    /// <summary>A row with a right-aligned control of the given (unscaled) width.</summary>
    public void Row(string label, string? help, float controlWidth, Action drawControl, float controlHeight = 0f)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var rowOrigin = ImGui.GetCursorScreenPos();
        var rowH = RowHeight * scale;
        var right = RightEdge;
        var midY = rowOrigin.Y + rowH * 0.5f;
        var hovered = ImGui.IsMouseHoveringRect(rowOrigin, new Vector2(right, rowOrigin.Y + rowH));

        if (!firstRow)
            ImGui.GetWindowDrawList().AddLine(rowOrigin, rowOrigin with { X = right }, ImGui.GetColorU32(CharonTheme.Hairline), 1f);
        firstRow = false;

        var labelSize = ImGui.CalcTextSize(label);
        ImGui.SetCursorScreenPos(new Vector2(rowOrigin.X, midY - labelSize.Y * 0.5f));
        Styling.Text(label, hovered ? CharonTheme.TextStrong : CharonTheme.TextSecondary);
        var labelHovered = ImGui.IsItemHovered();

        var iconHovered = false;
        if (!string.IsNullOrEmpty(help) && hovered)
        {
            var iconStr = FontAwesomeIcon.InfoCircle.ToIconString();
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var iconSize = ImGui.CalcTextSize(iconStr);
                ImGui.SetCursorScreenPos(new Vector2(rowOrigin.X + labelSize.X + 6f * scale, midY - iconSize.Y * 0.5f));
                Styling.Text(iconStr, CharonTheme.WithAlpha(CharonTheme.TextMuted, 0.9f));
            }

            iconHovered = ImGui.IsItemHovered();
        }

        if (!string.IsNullOrEmpty(help) && (labelHovered || iconHovered))
        {
            using var tip = ImRaii.Tooltip();
            ImGui.PushTextWrapPos(300f * scale);
            Styling.Text(help, CharonTheme.TextSecondary);
            ImGui.PopTextWrapPos();
        }

        var h = controlHeight > 0f ? controlHeight * scale : ImGui.GetFrameHeight();
        ImGui.SetCursorScreenPos(new Vector2(right - controlWidth * scale, midY - h * 0.5f));
        drawControl();

        ImGui.SetCursorScreenPos(rowOrigin);
        ImGui.Dummy(new Vector2(right - rowOrigin.X, rowH));
    }

    public bool Toggle(string label, string? help, ref bool value)
    {
        var changed = false;
        var v = value;
        Row(label, help, ToggleSwitch.Width, () => changed = ToggleSwitch.Draw($"##{label}", ref v), ToggleSwitch.Height);
        value = v;
        return changed;
    }

    /// <summary>Wrapped muted text under a row, for captions and warnings.</summary>
    public void Note(string text, Vector4? color = null)
    {
        Styling.VSpace(3f);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + (RightEdge - ImGui.GetCursorScreenPos().X));
        Styling.Text(text, color ?? CharonTheme.TextMuted);
        ImGui.PopTextWrapPos();
        Styling.VSpace(5f);
    }
}
