using System;
using System.Linq;
using System.Numerics;
using Charon.Features.AutoLogin;
using Charon.Services.Game;
using Charon.Windows.Components;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Charon.Windows;

/// <summary>
/// The login window: on the title screen, one card per character that has logged in on this PC —
/// job, name, world and level, and when it last played — and a click logs that character in. While
/// a login is running (started here or by a disconnect) it shows that character, the step and Stop.
/// Opened by the plugin only while logged out and has no title bar, so its setting is the way to
/// hide it; it moves by dragging the header.
///
/// The idea is the quick-launch overlay of VIWI (VeraNala; no licence, so nothing of it was copied).
/// Two differences: every character seen on this PC gets a card instead of one last character per
/// region, and a click starts the same content-id login the disconnect recovery uses, so no data
/// centre is ever looked up.
/// </summary>
public sealed class LoginWindow : Window
{
    private const float Width = 288f;
    private const float RowHeight = 46f;
    private const float ChipSize = 32f;

    private readonly AutoLoginService _login;
    private readonly Func<string, string> _display;

    public LoginWindow(AutoLoginService login, Func<string, string> display)
        : base("Charon — Login##CharonLogin")
    {
        _login = login;
        _display = display;

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing;
        Position = new Vector2(30f, 30f);
        PositionCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void PreDraw()
    {
        var s = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f, 11f) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 6f) * s);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, CharonTheme.WithAlpha(CharonTheme.BgDeep, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.Border, CharonTheme.WithAlpha(CharonTheme.Accent, 0.38f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
    }

    public override void Draw()
    {
        var s = ImGuiHelpers.GlobalScale;
        var width = Width * s;
        var now = DateTime.UtcNow;
        var characters = _login.Characters.Read(now);
        var running = _login.Running;
        var ready = _login.TitleReady;

        Header(width, s, running, ready);

        if (running)
        {
            var id = _login.RunContentId;
            var who = characters.FirstOrDefault(c => c.ContentId == id)
                      ?? new KnownCharacter(id, _login.RunName, string.Empty, now);
            Card(who, string.Empty, clickable: false, live: true, width, s);
            Progress(width, s);
            Wrapped(_login.Step, CharonTheme.TextSecondary, width);
            if (Buttons.Action("Stop##charonLogin", true, width, CharonTheme.AccentRose))
                _login.Stop();
            return;
        }

        foreach (var c in characters)
        {
            if (Card(c, KnownCharacters.Ago(now - c.SeenUtc), clickable: ready, live: false, width, s))
                _login.StartManual(c);
        }

        if (_login.LastFailure.Length > 0)
            Wrapped($"Last login stopped: {_login.LastFailure}", CharonTheme.AccentRose, width);
    }

    /// <summary>The brand mark on the left, what the window can do right now on the right.</summary>
    private static void Header(float width, float s, bool running, bool ready)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var line = ImGui.GetTextLineHeight();

        dl.AddRectFilled(pos + new Vector2(0f, 1f * s), pos + new Vector2(3f * s, line - 1f * s), U(CharonTheme.Accent), 1.5f * s);
        dl.AddText(pos + new Vector2(9f * s, 0f), U(CharonTheme.Accent), "CHARON");
        dl.AddText(pos + new Vector2(15f * s + ImGui.CalcTextSize("CHARON").X, 0f), U(CharonTheme.TextDim), "LOGIN");

        var (text, colour) = running
            ? ("logging in", CharonTheme.WithAlpha(CharonTheme.Accent, 0.55f + 0.45f * CharonTheme.Pulse()))
            : ready
                ? ("ready", CharonTheme.AccentMint)
                : ("waiting for the title menu", CharonTheme.TextDim);
        var textX = pos.X + width - ImGui.CalcTextSize(text).X;
        dl.AddText(new Vector2(textX, pos.Y), U(CharonTheme.TextDim), text);
        dl.AddCircleFilled(new Vector2(textX - 8f * s, pos.Y + line / 2f), 3f * s, U(colour));

        ImGui.Dummy(new Vector2(width, line + 2f * s));
        var rule = ImGui.GetCursorScreenPos();
        dl.AddLine(rule, rule + new Vector2(width, 0f), U(CharonTheme.Hairline), 1f);
        ImGui.Dummy(new Vector2(width, 1f * s));
    }

    /// <summary>
    /// One character: a job chip in its role colour, the name over world and level, and on the right
    /// when it last played — which turns into "Log in" under the pointer. Returns true on a click.
    /// </summary>
    private bool Card(KnownCharacter c, string right, bool clickable, bool live, float width, float s)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, RowHeight * s);
        var max = min + size;

        var clicked = ImGui.InvisibleButton($"##charonLoginCard{c.ContentId}", size) && clickable;
        var hovered = clickable && ImGui.IsItemHovered();
        var held = hovered && ImGui.IsItemActive();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var alpha = clickable || live ? 1f : 0.45f;
        var rounding = CharonTheme.CardRounding * s;
        var midY = min.Y + size.Y / 2f;

        dl.AddRectFilled(min, max, U(held ? CharonTheme.WithAlpha(CharonTheme.Accent, 0.20f)
            : hovered ? CharonTheme.CardBgHover : CharonTheme.CardBg), rounding);
        dl.AddRect(min, max, U(live ? CharonTheme.WithAlpha(CharonTheme.Accent, 0.35f + 0.35f * CharonTheme.Pulse(1400.0))
            : hovered ? CharonTheme.WithAlpha(CharonTheme.Accent, 0.70f)
            : CharonTheme.WithAlpha(CharonTheme.BorderDim, 0.60f)), rounding);
        if (hovered || live)
            dl.AddRectFilled(new Vector2(min.X, min.Y + 7f * s), new Vector2(min.X + 3f * s, max.Y - 7f * s), U(CharonTheme.Accent), 2f);

        // The job chip, or the initials when the job was never recorded.
        var name = _display(c.Name);
        var role = RoleColour(c);
        var chip = ChipSize * s;
        var chipMin = new Vector2(min.X + 10f * s, midY - chip / 2f);
        var chipMax = chipMin + new Vector2(chip, chip);
        var chipText = c.Job.Length > 0 ? c.Job : KnownCharacters.Initials(name);
        var chipTextSize = ImGui.CalcTextSize(chipText);
        dl.AddRectFilled(chipMin, chipMax, U(CharonTheme.WithAlpha(role, 0.16f * alpha)), 6f * s);
        dl.AddRect(chipMin, chipMax, U(CharonTheme.WithAlpha(role, 0.65f * alpha)), 6f * s);
        dl.AddText(chipMin + (new Vector2(chip, chip) - chipTextSize) / 2f, U(CharonTheme.WithAlpha(role, alpha)), chipText);

        // The right edge: when it last played, or the invitation under the pointer.
        var rightText = hovered ? "Log in" : right;
        var rightSize = ImGui.CalcTextSize(rightText);
        var rightX = max.X - 10f * s - (hovered ? 11f * s : 0f) - rightSize.X;
        dl.AddText(new Vector2(rightX, midY - rightSize.Y / 2f),
            U(hovered ? CharonTheme.Accent : CharonTheme.WithAlpha(CharonTheme.TextMuted, alpha)), rightText);
        if (hovered)
        {
            var tip = new Vector2(max.X - 10f * s, midY);
            var colour = U(CharonTheme.Accent);
            dl.AddLine(tip + new Vector2(-4f * s, -4f * s), tip, colour, 1.5f * s);
            dl.AddLine(tip + new Vector2(-4f * s, 4f * s), tip, colour, 1.5f * s);
        }

        // Name over world and level, cut off before the right edge rather than drawn across it.
        var subtitle = KnownCharacters.Subtitle(c.World, c.Level);
        var line = ImGui.GetTextLineHeight();
        var textX = chipMax.X + 10f * s;
        var top = subtitle.Length > 0 ? midY - line - 1f * s : midY - line / 2f;
        dl.PushClipRect(new Vector2(textX, min.Y), new Vector2(rightX - 6f * s, max.Y), true);
        dl.AddText(new Vector2(textX, top), U(CharonTheme.WithAlpha(CharonTheme.TextStrong, alpha)), name);
        if (subtitle.Length > 0)
            dl.AddText(new Vector2(textX, top + line + 2f * s), U(CharonTheme.WithAlpha(CharonTheme.TextDim, alpha)), subtitle);
        dl.PopClipRect();

        return clicked;
    }

    /// <summary>A thin bar with a band travelling along it: something is happening, with no known end.</summary>
    private static void Progress(float width, float s)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var height = 3f * s;
        var band = width * 0.28f;
        var start = min.X + (width + band) * CharonTheme.Phase(1300.0) - band;

        dl.AddRectFilled(min, min + new Vector2(width, height), U(CharonTheme.CardBg), height / 2f);
        dl.AddRectFilled(new Vector2(Math.Max(min.X, start), min.Y),
            new Vector2(Math.Min(min.X + width, start + band), min.Y + height), U(CharonTheme.Accent), height / 2f);
        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>Wrapped at the card width: plain wrapping collapses in a window that sizes itself.</summary>
    private static void Wrapped(string text, Vector4 colour, float width)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextColored(colour, text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>Tank blue, healer green, damage red, crafter or gatherer violet; teal when the job is unknown.</summary>
    private static Vector4 RoleColour(KnownCharacter c) => c.Job.Length == 0
        ? CharonTheme.Accent
        : c.Role switch
        {
            1 => CharonTheme.AccentBlue,
            4 => CharonTheme.AccentMint,
            2 or 3 => CharonTheme.AccentRose,
            _ => CharonTheme.AccentViolet,
        };

    private static uint U(Vector4 colour) => ImGui.GetColorU32(colour);
}
