using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Features.Retainers;
using Charon.Services.Game;

namespace Charon.Windows;

/// <summary>
/// Pops up beside the game's retainer list so the venture chores are one click away. Auto-opened
/// and closed by the RetainerList addon lifecycle (see CharonPlugin), the same way the FC chest
/// window rides the chest addon.
///
/// The button ARMS the assist; it never starts a round. You pick the retainer, Charon handles the
/// window that opens, and control comes straight back. Stop is always visible and lands on the very
/// next tick, because nothing is ever queued.
/// </summary>
public sealed class VentureOverlay : Window
{
    private readonly RetainerReader _retainers;
    private readonly VentureRunner _runner;

    public VentureOverlay(RetainerReader retainers, VentureRunner runner)
        : base("Charon — Ventures##CharonVentures")
    {
        _retainers = retainers;
        _runner = runner;

        Size = new Vector2(320, 300);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(280, 200),
            MaximumSize = new Vector2(620, 900),
        };
    }

    public override void Draw()
    {
        var now = DateTime.UtcNow;
        var rows = VentureBoard.Compose(now, _retainers.Read(now));

        ImGui.TextColored(CharonTheme.TextSecondary, VentureBoard.Summarize(_retainers.Loaded, rows));
        ImGui.Spacing();

        if (rows.Count > 0 && ImGui.BeginTable("ventureRows", 2,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit,
                new Vector2(0, Math.Min(rows.Count * 24f + 28f, 200f))))
        {
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Venture", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Name);
                ImGui.TableNextColumn();
                var (text, colour) = Describe(row);
                ImGui.TextColored(colour, text);
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();

        if (_runner.Armed)
        {
            if (ImGui.Button("Stop", new Vector2(-1, 0)))
                _runner.Stop();
            CharonTheme.HelpMarker("Stops on the next tick. Nothing is queued up, so there is\n"
                                   + "never a backlog to drain — closing the retainer window\n"
                                   + "stops it just as well.");
        }
        else
        {
            if (ImGui.Button("Handle the retainer I open", new Vector2(-1, 0)))
                _runner.Arm();
            CharonTheme.HelpMarker("YOU pick the retainer. Charon then works the window that opens:\n"
                                   + "a finished venture is collected and sent straight back out on\n"
                                   + "the same venture, and an idle retainer gets a quick exploration.\n\n"
                                   + "It never opens a bell, never picks a retainer for you, and never\n"
                                   + "moves on to the next one by itself — so it cannot lock you out\n"
                                   + "of your own character.");
        }

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextDisabled, _runner.Status);
    }

    private static (string Text, Vector4 Colour) Describe(VentureRow row) => row.State switch
    {
        VentureState.Ready => ("ready", CharonTheme.StatusGreen),
        VentureState.Idle => ("idle", CharonTheme.StatusYellow),
        VentureState.Running => (VentureBoard.Describe(row.Remaining ?? TimeSpan.Zero), CharonTheme.TextSecondary),
        _ => ("unknown", CharonTheme.TextDisabled),
    };
}
