using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Features.DeepDungeon;
using Charon.Services.Game;

namespace Charon.Windows;

/// <summary>
/// The deep-dungeon floor map: the full 5×5 room layout with connections, passage, return,
/// chests and party positions — including rooms the game has not revealed yet (they draw dim).
/// Opened by the plugin ONLY while a deep-dungeon instance is active, so it can never linger
/// outside one. The data is one struct read (<see cref="DeepDungeonReader"/>).
/// </summary>
public sealed class DeepDungeonMapWindow : Window
{
    private const float CellSize = 34f;
    private const float CellGap = 10f;

    private readonly DeepDungeonReader _reader;

    public DeepDungeonMapWindow(DeepDungeonReader reader)
        : base("Charon — Deep Dungeon##CharonDeepDungeonMap")
    {
        _reader = reader;

        Flags = ImGuiWindowFlags.AlwaysAutoResize;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void Draw()
    {
        var snapshot = _reader.GetSnapshot();
        if (!snapshot.Active)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "Not in a deep dungeon.");
            return;
        }

        var cells = FloorMap.Build(snapshot.Rooms);
        var (known, revealed) = FloorMap.Counts(cells);

        ImGui.TextColored(CharonTheme.AccentGold, $"Floor {snapshot.Floor}");
        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextSecondary, $"· {known} rooms · {revealed} revealed");
        ImGui.TextColored(CharonTheme.TextSecondary,
            $"Passage {snapshot.PassageProgress}% · Return {snapshot.ReturnProgress}%");
        ImGui.Spacing();

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos() + new Vector2(4f, 4f);
        var pitch = CellSize + CellGap;

        // Connections first, so rooms draw over the line ends.
        foreach (var cell in cells)
        {
            if (!cell.Exists)
                continue;

            var center = origin + new Vector2(cell.X * pitch + CellSize / 2f, cell.Y * pitch + CellSize / 2f);
            var connColor = ImGui.GetColorU32(CharonTheme.TextDisabled);
            var half = CellSize / 2f;
            var reach = half + CellGap / 2f + 1f;

            // Each side draws its own half-line; the neighbour draws the other half, so a
            // one-way flag mismatch is visible instead of silently bridged.
            if (cell.North)
                drawList.AddLine(center - new Vector2(0, half), center - new Vector2(0, reach), connColor, 3f);
            if (cell.South)
                drawList.AddLine(center + new Vector2(0, half), center + new Vector2(0, reach), connColor, 3f);
            if (cell.West)
                drawList.AddLine(center - new Vector2(half, 0), center - new Vector2(reach, 0), connColor, 3f);
            if (cell.East)
                drawList.AddLine(center + new Vector2(half, 0), center + new Vector2(reach, 0), connColor, 3f);
        }

        foreach (var cell in cells)
        {
            if (!cell.Exists)
                continue;

            var topLeft = origin + new Vector2(cell.X * pitch, cell.Y * pitch);
            var bottomRight = topLeft + new Vector2(CellSize, CellSize);

            // Unrevealed rooms draw dim — the layout is knowable before the game shows it, and
            // the dim/bright split makes the distinction visible at a glance.
            var fill = cell.IsRevealed
                ? ImGui.GetColorU32(new Vector4(0.28f, 0.26f, 0.20f, 0.95f))
                : ImGui.GetColorU32(new Vector4(0.16f, 0.16f, 0.18f, 0.75f));
            drawList.AddRectFilled(topLeft, bottomRight, fill, 4f);

            if (cell.IsHome)
                drawList.AddRect(topLeft, bottomRight, ImGui.GetColorU32(CharonTheme.AccentGold), 4f, ImDrawFlags.None, 2f);
            else
                drawList.AddRect(topLeft, bottomRight, ImGui.GetColorU32(CharonTheme.TextDisabled), 4f);

            var center = (topLeft + bottomRight) / 2f;
            if (cell.IsPassage)
                drawList.AddText(center - new Vector2(4f, 8f), ImGui.GetColorU32(CharonTheme.StatusGreen), "P");
            else if (cell.IsReturn)
                drawList.AddText(center - new Vector2(4f, 8f), ImGui.GetColorU32(CharonTheme.StatusYellow), "R");

            // Chest pips along the bottom edge of the room.
            var pip = 0;
            foreach (var chest in snapshot.Chests)
            {
                if (chest.Room != cell.Index)
                    continue;

                var pipPos = topLeft + new Vector2(5f + pip * 8f, CellSize - 8f);
                drawList.AddCircleFilled(pipPos, 3f, ChestColor(chest.Type));
                pip++;
            }

            // Party pips along the top edge.
            var partyPip = 0;
            foreach (var member in snapshot.Party)
            {
                if (member.Room != cell.Index || member.EntityId == 0)
                    continue;

                var pipPos = topLeft + new Vector2(6f + partyPip * 9f, 6f);
                drawList.AddCircleFilled(pipPos, 3.5f, ImGui.GetColorU32(new Vector4(0.35f, 0.75f, 1f, 1f)));
                partyPip++;
            }
        }

        ImGui.Dummy(new Vector2(FloorMap.GridSize * pitch, FloorMap.GridSize * pitch));
        ImGui.TextColored(CharonTheme.TextDisabled, "P passage · R return · dots: chests (bottom) / party (top)");
    }

    /// <summary>Chest colors by type id — the ids are drawn distinctly rather than named, since
    /// their meanings are not yet verified; the tooltip legend calls them chests, no more.</summary>
    private static uint ChestColor(byte type) => type switch
    {
        1 => ImGui.GetColorU32(new Vector4(1f, 0.84f, 0.25f, 1f)),   // gold-ish
        2 => ImGui.GetColorU32(new Vector4(0.80f, 0.80f, 0.85f, 1f)), // silver-ish
        3 => ImGui.GetColorU32(new Vector4(0.80f, 0.55f, 0.30f, 1f)), // bronze-ish
        _ => ImGui.GetColorU32(new Vector4(0.6f, 0.9f, 0.9f, 1f)),
    };
}
