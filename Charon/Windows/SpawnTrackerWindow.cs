using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Services.Game;

namespace Charon.Windows;

/// <summary>
/// The spawn log: every watched mob that has appeared, newest first. Opened from the Spawns
/// section (and automatically on a sighting, if that is switched on); its visibility is
/// persisted, so a window left open comes back after a reload.
/// </summary>
public sealed class SpawnTrackerWindow : Window
{
    private readonly SpawnScanner _scanner;

    public SpawnTrackerWindow(SpawnScanner scanner)
        : base("Charon — Spawns##CharonSpawnTracker")
    {
        BgAlpha = CharonTheme.PanelAlpha;
        _scanner = scanner;

        Size = new Vector2(340, 260);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(260, 140),
            MaximumSize = new Vector2(700, 900),
        };
        IsOpen = false;
    }

    public override void Draw()
    {
        var history = _scanner.Watcher.History;

        ImGui.TextColored(CharonTheme.TextSecondary, $"{history.Count} sighting(s)");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear##spawnlog"))
            _scanner.Watcher.ClearHistory();

        ImGui.Separator();

        if (history.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled,
                "Nothing seen yet. Add mob names in the Spawns section.");
        }
        else if (ImGui.BeginTable("spawnlog", 3,
                     ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Mob", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Range", ImGuiTableColumnFlags.WidthFixed, 55f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var sighting in history)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(CharonTheme.TextDisabled, sighting.SeenUtc.ToLocalTime().ToString("HH:mm:ss"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(sighting.Name);
                ImGui.TableNextColumn();
                ImGui.TextColored(CharonTheme.TextSecondary, $"{sighting.Distance:F0}y");
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextColored(CharonTheme.TextDisabled, _scanner.Status);
    }
}
