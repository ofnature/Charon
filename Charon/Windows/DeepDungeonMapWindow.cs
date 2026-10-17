using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
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
    private readonly IObjectTable _objectTable;
    private readonly PomanderTracker _pomanders;

    public DeepDungeonMapWindow(DeepDungeonReader reader, IObjectTable objectTable, PomanderTracker pomanders)
        : base("Charon — Deep Dungeon##CharonDeepDungeonMap")
    {
        BgAlpha = CharonTheme.PanelAlpha;
        _reader = reader;
        _objectTable = objectTable;
        _pomanders = pomanders;

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

        ImGui.TextColored(CharonTheme.Accent, $"Floor {snapshot.Floor}");
        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextSecondary, $"· {known} rooms · {revealed} revealed");
        DrawFloorPanel(snapshot);

        // Pomanders in force here, and the ones already queued for the next floor (Affluence, Flight,
        // Alteration and Devotion act on the floor AFTER the one they were used on).
        var here = PomanderLog.Describe(_pomanders.Uses.ActiveOn(snapshot.Floor));
        var next = PomanderLog.Describe(_pomanders.Uses.Pending(snapshot.Floor));
        if (here.Length > 0)
            ImGui.TextColored(CharonTheme.TextSecondary, $"Used: {here}");
        if (next.Length > 0)
            ImGui.TextColored(CharonTheme.StatusYellow, $"Used for next floor: {next}");
        ImGui.Spacing();

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos() + new Vector2(4f, 4f);
        var pitch = CellSize + CellGap;

        // Which room we are in, and which way we face — the game's own floor widget draws the
        // same arrow. The room comes from the party table (we are one of its entries); the
        // heading is the live object-table rotation.
        var local = _objectTable.LocalPlayer;
        var localRoom = -1;
        if (local != null)
        {
            foreach (var member in snapshot.Party)
            {
                if (member.EntityId == local.EntityId)
                {
                    localRoom = member.Room;
                    break;
                }
            }
        }

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
                drawList.AddRect(topLeft, bottomRight, ImGui.GetColorU32(CharonTheme.Accent), 4f, ImDrawFlags.None, 2f);
            else
                drawList.AddRect(topLeft, bottomRight, ImGui.GetColorU32(CharonTheme.TextDisabled), 4f);

            var center = (topLeft + bottomRight) / 2f;

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

            // Party pips along the top edge. We are drawn as the heading arrow instead, so a pip
            // for ourselves would just be the same information twice.
            var partyPip = 0;
            foreach (var member in snapshot.Party)
            {
                if (member.Room != cell.Index || member.EntityId == 0
                    || (local != null && member.EntityId == local.EntityId))
                    continue;

                var pipPos = topLeft + new Vector2(6f + partyPip * 9f, 6f);
                drawList.AddCircleFilled(pipPos, 3.5f, ImGui.GetColorU32(new Vector4(0.35f, 0.75f, 1f, 1f)));
                partyPip++;
            }

            if (local != null && cell.Index == localRoom)
                DrawHeading(drawList, center, local.Rotation);

            // Cairn markers sit in the top-right corner and draw AFTER the arrow: in the middle of the
            // room they were hidden by it exactly when you stood at the cairn. Bright = active, dim = not
            // yet — the same answer as the line above the map.
            var marker = topLeft + new Vector2(CellSize - 11f, 1f);
            if (cell.IsPassage)
            {
                drawList.AddText(marker, ImGui.GetColorU32(FloorMap.CairnActive(snapshot.PassageProgress)
                    ? CharonTheme.StatusGreen : CharonTheme.TextDisabled), "P");
                marker.Y += 14f;
            }

            if (cell.IsReturn)
                drawList.AddText(marker, ImGui.GetColorU32(FloorMap.CairnActive(snapshot.ReturnProgress)
                    ? CharonTheme.StatusYellow : CharonTheme.TextDisabled), "R");

            // The votive candelabra (Pilgrim's Traverse) — bottom-right, clear of the cairn markers above.
            if (cell.IsVotive)
                drawList.AddText(topLeft + new Vector2(CellSize - 11f, CellSize - 16f),
                    ImGui.GetColorU32(CharonTheme.AccentSoft), "V");
        }

        ImGui.Dummy(new Vector2(FloorMap.GridSize * pitch, FloorMap.GridSize * pitch));
        ImGui.PushStyleColor(ImGuiCol.Text, CharonTheme.TextDisabled);
        ImGui.TextWrapped(
            "P passage · R return (bright = open) · V candelabra · outline: the game's flag room · arrow: you · dots: chests (bottom) / party (top)");
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// NecroLens's floor panel (MIT, adapted — see <see cref="FloorClock"/>): respawn countdown, trap and
    /// cairn status, the time spent on each floor of the set, and the floor effects with "(+)" marking one
    /// already queued for the next floor.
    /// </summary>
    private void DrawFloorPanel(DeepDungeonReader.Snapshot snapshot)
    {
        var now = DateTime.UtcNow;
        var clock = _pomanders.Clock;

        var interval = FloorClock.RespawnSeconds(snapshot.ContentId);
        if (interval is int seconds && FloorClock.HasRespawns(snapshot.ContentId, snapshot.Floor) && clock.Floor == snapshot.Floor)
        {
            var left = FloorClock.SecondsToRespawn(clock.FloorStartUtc, now, seconds);
            ImGui.TextColored(left <= 10 ? CharonTheme.StatusYellow : CharonTheme.TextSecondary,
                $"Respawns in {FloorClock.Format(TimeSpan.FromSeconds(left))} (every {seconds}s)");
        }
        else
        {
            ImGui.TextColored(CharonTheme.TextDisabled, interval == null ? "Respawns: unknown floor set" : "No respawns on this floor");
        }

        var active = _pomanders.Uses.ActiveOn(snapshot.Floor);
        var traps = FloorClock.Traps(active);
        ImGui.TextColored(CharonTheme.TextSecondary, "Traps:");
        ImGui.SameLine();
        ImGui.TextColored(traps switch
        {
            TrapStatus.Inactive => CharonTheme.StatusGreen,
            TrapStatus.Visible => CharonTheme.StatusYellow,
            _ => CharonTheme.StatusRed,
        }, traps.ToString());
        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextSecondary, " · Passage");
        ImGui.SameLine();
        var passageOpen = FloorMap.CairnActive(snapshot.PassageProgress);
        ImGui.TextColored(passageOpen ? CharonTheme.StatusGreen : CharonTheme.StatusRed, FloorMap.CairnText(snapshot.PassageProgress));
        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextSecondary, $"· Return {FloorMap.CairnText(snapshot.ReturnProgress)}");

        var pending = _pomanders.Uses.Pending(snapshot.Floor);
        if (ImGui.BeginTable("##ddPanel", 3, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("a");
            ImGui.TableSetupColumn("b");
            ImGui.TableSetupColumn("effects");

            var floors = FloorClock.FloorSet(snapshot.Floor);
            for (var row = 0; row < 6; row++)
            {
                ImGui.TableNextRow();
                for (var col = 0; col < 2; col++)
                {
                    ImGui.TableNextColumn();
                    var i = col * 5 + row;
                    if (row < 5)
                        DrawFloorTime(floors[i], clock.TimeOn(floors[i], now), floors[i] == snapshot.Floor);
                }

                ImGui.TableNextColumn();
                var (name, items) = FloorClock.Effects[row];
                ImGui.TextColored(FloorClock.Has(active, items) ? CharonTheme.TextPrimary : CharonTheme.TextDisabled, name);
                if (FloorClock.Has(pending, items))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(CharonTheme.StatusGreen, "(+)");
                }
            }

            ImGui.EndTable();
        }
    }

    private static void DrawFloorTime(int floor, TimeSpan? time, bool current)
    {
        var text = $"{floor:000}: {(time is { } t ? FloorClock.Format(t) : "--:--")}";
        ImGui.TextColored(current ? CharonTheme.StatusYellow : time == null ? CharonTheme.TextDisabled : CharonTheme.TextSecondary, text);
    }

    /// <summary>
    /// The "you are here, facing this way" arrow, in the middle of the room we occupy.
    ///
    /// A character's rotation gives the world heading as (sin, cos) over the XZ plane — the same
    /// conversion the ESP's facing cones use. World +Z is south and the grid is drawn north-up
    /// (a room's North connection is its -Y edge), so that heading maps straight onto screen XY
    /// with no rotation of its own. If the arrow ever points the wrong way, THAT is the evidence
    /// the grid is not north-up, which nothing else on this map would reveal.
    /// </summary>
    private static void DrawHeading(ImDrawListPtr drawList, Vector2 center, float rotation)
    {
        var dir = new Vector2(MathF.Sin(rotation), MathF.Cos(rotation));
        var normal = new Vector2(-dir.Y, dir.X);
        var color = ImGui.GetColorU32(new Vector4(0.45f, 0.85f, 1f, 1f));

        drawList.AddTriangleFilled(
            center + dir * 9f,
            center - dir * 5f + normal * 6f,
            center - dir * 5f - normal * 6f,
            color);
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
