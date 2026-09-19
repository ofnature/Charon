using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Charon.Windows.Components;

/// <summary>
/// A background-boxed content region that HUGS its content, stacked one after another down a page.
///
/// This is the pattern for anything whose height is not known up front (a list, a table, a sentence).
/// Do NOT use <see cref="Card.Begin"/> with a zero size for this: an ImGui child sized 0,0 takes the
/// rest of the parent, so the first card swallows the column and every card after it is squeezed to
/// nothing — which showed up as the fleet board's later cards painting their text over the first one.
/// </summary>
internal sealed class Panel : IDisposable
{
    private const float PaddingX = 10f;
    private const float PaddingY = 5f;
    private const float Gap = 6f;

    private readonly Vector2 _origin;
    private readonly float _width;
    private readonly Vector4 _background;
    private readonly Vector4? _stripe;

    /// <param name="stripe">Optional state colour for the 3px left edge stripe.</param>
    /// <param name="background">Override for the card wash (defaults to the soft surface).</param>
    public static Panel Begin(Vector4? stripe = null, Vector4? background = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(origin + new Vector2(PaddingX, PaddingY) * scale);
        ImGui.BeginGroup();
        return new Panel(origin, width, background ?? CharonTheme.CardBgSoft, stripe);
    }

    private Panel(Vector2 origin, float width, Vector4 background, Vector4? stripe)
    {
        _origin = origin;
        _width = width;
        _background = background;
        _stripe = stripe;
    }

    /// <summary>Screen X of the panel's inner left edge (inside the card padding).</summary>
    public float LeftEdge => _origin.X + PaddingX * ImGuiHelpers.GlobalScale;

    /// <summary>
    /// Screen X of the panel's inner right edge. Use this — never GetContentRegionAvail arithmetic —
    /// to right-align something inside a panel: the available-region value is measured against the
    /// window, not the card, which puts the text past the card edge (observed: the "needs you" values
    /// clipped by the card border).
    /// </summary>
    public float RightEdge => _origin.X + _width - PaddingX * ImGuiHelpers.GlobalScale;

    public void Dispose()
    {
        ImGui.EndGroup();
        var scale = ImGuiHelpers.GlobalScale;
        var end = new Vector2(_origin.X + _width, ImGui.GetItemRectMax().Y + PaddingY * scale);

        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);
        var rounding = CharonTheme.CardRounding * scale;
        dl.AddRectFilled(_origin, end, ImGui.GetColorU32(_background), rounding);
        dl.AddRect(_origin, end, ImGui.GetColorU32(CharonTheme.WithAlpha(CharonTheme.BorderDim, 0.55f)), rounding);
        if (_stripe is { } a)
            dl.AddRectFilled(_origin, new Vector2(_origin.X + 3f * scale, end.Y),
                ImGui.GetColorU32(CharonTheme.WithAlpha(a, 0.85f)), 2f);
        dl.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(_origin.X, end.Y));
        ImGui.Dummy(new Vector2(_width, 0f));
        Styling.VSpace(Gap);
    }
}
