using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Features.AutoPillion;

namespace Charon.Windows;

/// <summary>
/// The driver's glance: a small window listing every passenger seat of the mount the local
/// player is driving, and who is in it. Opened by the plugin ONLY while the local player owns a
/// mounted multi-seat mount (the same cached snapshot the boarding logic reads), so it can never
/// linger after a dismount. When an auto-pillion session is running, empty seats also show the
/// invite state (invited / timed out) so a hole in the roster explains itself.
/// </summary>
public sealed class PillionRidersWindow : Window
{
    private readonly Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> _occupancy;
    private readonly PillionManager _pillion;

    public PillionRidersWindow(
        Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> occupancy,
        PillionManager pillion)
        : base("Charon — Riders##CharonPillionRiders")
    {
        _occupancy = occupancy;
        _pillion = pillion;

        Flags = ImGuiWindowFlags.AlwaysAutoResize;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void Draw()
    {
        var rows = _occupancy();
        if (rows.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "Not driving a multi-seat mount.");
            return;
        }

        var filled = 0;
        foreach (var row in rows)
        {
            if (row.EntityId != 0)
                filled++;
        }

        ImGui.TextColored(filled == rows.Count ? CharonTheme.StatusGreen : CharonTheme.AccentGold,
            $"Riders {filled} / {rows.Count}");
        ImGui.Spacing();

        foreach (var (seat, entityId, name) in rows)
        {
            if (entityId != 0)
            {
                ImGui.TextColored(CharonTheme.StatusGreen, $"Seat {seat}");
                ImGui.SameLine();
                ImGui.TextUnformatted(name.Length > 0 ? name : "occupied");
                continue;
            }

            ImGui.TextColored(CharonTheme.StatusGrey, $"Seat {seat}");
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDisabled, DescribeEmptySeat(seat));
        }
    }

    /// <summary>An empty seat during an active session says what the manager is doing about it.</summary>
    private string DescribeEmptySeat(int seat)
    {
        if (!_pillion.SessionActive)
            return "empty";

        foreach (var s in _pillion.Seats)
        {
            if (s.Index != seat)
                continue;
            return s.Status switch
            {
                SeatStatus.InvitePending => $"invited {s.AssignedName}",
                SeatStatus.Declined => "invite timed out",
                _ => "empty",
            };
        }

        return "empty";
    }
}
