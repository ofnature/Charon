using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Features.AutoAccept;
using Charon.Features.AutoPillion;
using Charon.Services;

namespace Charon.Windows;

/// <summary>
/// Charon's single window: Auto Pillion section, Auto Accept section with the trusted-character
/// table, and a collapsed Debug section. Compact (AlwaysAutoResize), dark theme with gold accents.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly CharonConfig _config;
    private readonly Action _save;
    private readonly WhitelistService _whitelist;
    private readonly IDaedalusRosterProvider _roster;
    private readonly PillionManager _pillion;
    private readonly GroupInviteManager _inviteManager;
    private readonly Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> _rawSeatOccupancy;
    private readonly Func<string> _boardingStatus;
    private readonly Func<string> _followStatus;

    private string _addName = string.Empty;
    private string _addWorld = string.Empty;
    private bool _addOpen;

    // Underworld-themed aliases for the scramble toggle — assigned first-seen, stable per session.
    // Cosmetic and DRAW-TIME ONLY: logic, logs, and game commands always use real names.
    private static readonly string[] AliasPool =
    [
        "Styx", "Acheron", "Lethe", "Cocytus", "Phlegethon", "Erebus", "Nyx", "Thanatos",
        "Hypnos", "Orpheus", "Eurydice", "Persephone", "Minos", "Aeacus", "Rhadamanthus", "Moros",
    ];

    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(
        CharonConfig config,
        Action save,
        WhitelistService whitelist,
        IDaedalusRosterProvider roster,
        PillionManager pillion,
        GroupInviteManager inviteManager,
        Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> rawSeatOccupancy,
        Func<string> boardingStatus,
        Func<string> followStatus)
        : base("Charon##CharonMain", ImGuiWindowFlags.AlwaysAutoResize)
    {
        _config = config;
        _save = save;
        _whitelist = whitelist;
        _roster = roster;
        _pillion = pillion;
        _inviteManager = inviteManager;
        _rawSeatOccupancy = rawSeatOccupancy;
        _boardingStatus = boardingStatus;
        _followStatus = followStatus;
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        try
        {
            DrawAutoPillionSection();
            ImGui.Spacing();
            DrawFollowTeleportSection();
            ImGui.Spacing();
            DrawAutoAcceptSection();
            ImGui.Spacing();
            DrawDebugSection();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private void DrawAutoPillionSection()
    {
        DrawSectionTitle("■ Auto Pillion", _config.AutoPillionEnabled);

        var enabled = _config.AutoPillionEnabled;
        if (ImGui.Checkbox("Enabled##pillion", ref enabled))
        {
            _config.AutoPillionEnabled = enabled;
            _save();
        }
        CharonTheme.HelpMarker("Scan seats when you mount a multi-passenger mount and assign\n"
                               + "party members to open seats — no seat-2 spam.");

        var delay = _config.PillionDelay;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Invite Delay##pillion", ref delay, 0.0f, 5.0f, "%.1f s"))
        {
            _config.PillionDelay = delay;
            _save();
        }
        CharonTheme.HelpMarker("Wait after mounting before the first invite,\nso the mount animation can finish.");

        var timeout = _config.SeatTimeout;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Seat Timeout##pillion", ref timeout, 1.0f, 15.0f, "%.1f s"))
        {
            _config.SeatTimeout = timeout;
            _save();
        }
        CharonTheme.HelpMarker("Unanswered invites mark the seat declined after this long.\nDeclined seats are never re-invited.");

        var lanOnly = _config.LanMembersOnly;
        if (ImGui.Checkbox("LAN Members Only##pillion", ref lanOnly))
        {
            _config.LanMembersOnly = lanOnly;
            _save();
        }
        CharonTheme.HelpMarker("Only invite Daedalus LAN party members;\nskip the manual whitelist for pillion seats.");

        // Session status line
        if (_pillion.SessionActive)
        {
            ImGui.TextColored(CharonTheme.TextSecondary,
                $"Current mount: {_pillion.PassengerSeats + 1}-person ({_pillion.SeatsAvailable} seats available)");
            ImGui.TextColored(CharonTheme.TextSecondary,
                $"Seats filled: {_pillion.SeatsFilled}/{_pillion.PassengerSeats}");
        }
        else
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "No multi-passenger mount active");
        }
    }

    private void DrawFollowTeleportSection()
    {
        DrawSectionTitle("■ Follow Teleport", _config.FollowTeleportEnabled);

        var enabled = _config.FollowTeleportEnabled;
        if (ImGui.Checkbox("Enabled##follow", ref enabled))
        {
            _config.FollowTeleportEnabled = enabled;
            _save();
        }
        CharonTheme.HelpMarker("When a trusted party member teleports to another zone, follow them\n"
                               + "to an unlocked aetheryte there (small random delay per toon).\n"
                               + "Same group only. Normal teleport gil costs apply.");
    }

    private void DrawAutoAcceptSection()
    {
        DrawSectionTitle("■ Auto Accept Invites", _config.AutoAcceptEnabled);

        var enabled = _config.AutoAcceptEnabled;
        if (ImGui.Checkbox("Enabled##accept", ref enabled))
        {
            _config.AutoAcceptEnabled = enabled;
            _save();
        }
        CharonTheme.HelpMarker("Auto-accept group invites from trusted characters only.\n"
                               + "Unknown inviters are ignored (never declined) — the dialog\n"
                               + "stays up for you to decide.");

        var lanTrust = _config.LanAutoWhitelist;
        if (ImGui.Checkbox("Auto-trust LAN Party Members##accept", ref lanTrust))
        {
            _config.LanAutoWhitelist = lanTrust;
            _save();
        }
        CharonTheme.HelpMarker("Trust every toon currently in the Daedalus LAN party roster.");

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextSecondary, "Trusted Characters");
        DrawWhitelistTable();
        DrawWhitelistButtons();
    }

    private void DrawWhitelistTable()
    {
        var lanMembers = _roster.GetLanPartyMembers();

        if (_whitelist.Entries.Count == 0 && lanMembers.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "No trusted characters yet.");
            return;
        }

        if (!ImGui.BeginTable("whitelist", 5,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("##dot", ImGuiTableColumnFlags.WidthFixed, 16f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 70f);

        // LAN roster first — trusted live via the LAN toggle, shown for visibility.
        foreach (var toon in lanMembers)
        {
            var inManualList = _whitelist.Find(toon.CharacterName, toon.World) != null;
            if (inManualList)
                continue; // the manual row below covers it

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(toon.IsOnline ? CharonTheme.StatusGreen : CharonTheme.StatusGrey, "●");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Display(toon.CharacterName));
            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextSecondary, toon.World.Length > 0 ? toon.World : "—");
            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.AccentGold, "[LAN]");
            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextDisabled, _config.LanAutoWhitelist ? "auto" : "off");
        }

        foreach (var entry in _whitelist.Entries.ToArray())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(entry.Enabled ? CharonTheme.StatusGreen : CharonTheme.StatusGrey,
                entry.Enabled ? "●" : "○");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Display(entry.CharacterName));
            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextSecondary, entry.World);
            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextSecondary, "[Manual]");
            ImGui.TableNextColumn();

            var id = $"{entry.CharacterName}@{entry.World}";
            var enabled = entry.Enabled;
            if (ImGui.SmallButton($"{(enabled ? "off" : "on")}##tgl{id}"))
                _whitelist.SetEnabled(entry.CharacterName, entry.World, !enabled);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(enabled ? "Disable without removing" : "Re-enable");
            ImGui.SameLine();
            if (ImGui.SmallButton($"x##rm{id}"))
                _whitelist.Remove(entry.CharacterName, entry.World);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove from whitelist");
        }

        ImGui.EndTable();
    }

    private void DrawWhitelistButtons()
    {
        if (ImGui.Button(_addOpen ? "Cancel" : "+ Add Character"))
        {
            _addOpen = !_addOpen;
            _addName = string.Empty;
            _addWorld = string.Empty;
        }

        ImGui.SameLine();
        var lanMembers = _roster.GetLanPartyMembers();
        var canImport = lanMembers.Count > 0;
        if (!canImport) ImGui.BeginDisabled();
        if (ImGui.Button("Import from LAN") && canImport)
            _whitelist.ImportFromLan(lanMembers);
        if (!canImport) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(canImport
                ? "Add every current LAN party toon to the manual whitelist"
                : "Daedalus LAN roster unavailable");

        ImGui.SameLine();
        var scramble = _config.ScrambleNames;
        if (ImGui.Checkbox("Scramble", ref scramble))
        {
            _config.ScrambleNames = scramble;
            _save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Replace character names with aliases in this window.\nCosmetic only — for screenshots.");

        if (!_addOpen)
            return;

        ImGui.SetNextItemWidth(140f);
        ImGui.InputTextWithHint("##addname", "Forename Surname", ref _addName, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputTextWithHint("##addworld", "World", ref _addWorld, 32);
        ImGui.SameLine();
        if (ImGui.Button("Add##confirm"))
        {
            if (_whitelist.Add(_addName, _addWorld))
            {
                _addOpen = false;
                _addName = string.Empty;
                _addWorld = string.Empty;
            }
        }
    }

    private void DrawDebugSection()
    {
        DrawSectionTitle("■ Debug", false, showDot: false);

        if (!ImGui.TreeNode("Details##debug"))
            return;

        ImGui.TextColored(CharonTheme.TextSecondary,
            $"Daedalus IPC: {(_roster.IsAvailable ? "connected" : "unavailable — manual whitelist only")}");
        ImGui.TextColored(CharonTheme.TextSecondary, $"Boarding: {ScrambleIn(_boardingStatus())}");
        ImGui.TextColored(CharonTheme.TextSecondary, $"Follow: {ScrambleIn(_followStatus())}");
        if (_inviteManager.AcceptPending)
            ImGui.TextColored(CharonTheme.StatusYellow, "Invite accept pending (delay running)");

        if (_pillion.SessionActive)
        {
            ImGui.TextColored(CharonTheme.TextSecondary, $"Mount id: {_pillion.MountId}");
            foreach (var seat in _pillion.Seats)
            {
                var color = seat.Status switch
                {
                    SeatStatus.Filled => CharonTheme.StatusGreen,
                    SeatStatus.InvitePending => CharonTheme.StatusYellow,
                    SeatStatus.Declined => CharonTheme.StatusRed,
                    _ => CharonTheme.StatusGrey,
                };
                var occupant = seat.AssignedName.Length > 0 ? $" — {Display(seat.AssignedName)}" : string.Empty;
                ImGui.TextColored(color, $"Seat {seat.Index}: {seat.Status}{occupant}");
            }

            // Raw game state, straight from MountContainer — for diagnosing seat-index mapping.
            ImGui.Spacing();
            ImGui.TextColored(CharonTheme.TextSecondary, "Raw seat data (game)");
            foreach (var (seatIndex, entityId, name) in _rawSeatOccupancy())
            {
                // Entity ids identify characters too — mask them while scrambling.
                var id = _config.ScrambleNames ? "0x········" : $"0x{entityId:X8}";
                ImGui.TextColored(CharonTheme.TextDisabled,
                    entityId == 0
                        ? $"#{seatIndex}: empty"
                        : $"#{seatIndex}: {id} {(name.Length > 0 ? Display(name) : "(unresolved)")}");
            }
        }

        if (_inviteManager.AcceptLog.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(CharonTheme.TextSecondary, "Accepted invites");
            foreach (var entry in _inviteManager.AcceptLog)
            {
                ImGui.TextColored(CharonTheme.TextDisabled,
                    $"{entry.TimeUtc:HH:mm:ss}  {Display(entry.CharacterName)}@{entry.World}  [{entry.Source}]");
            }
        }

        ImGui.TreePop();
    }

    /// <summary>Session-stable alias: the same character always maps to the same underworld name.</summary>
    private string AliasFor(string characterName)
    {
        if (_aliases.TryGetValue(characterName, out var alias))
            return alias;

        alias = AliasPool[_aliases.Count % AliasPool.Length];
        if (_aliases.Count >= AliasPool.Length)
            alias += $" {_aliases.Count / AliasPool.Length + 1}"; // pool exhausted — suffix

        _aliases[characterName] = alias;
        return alias;
    }

    /// <summary>Display name honoring the scramble toggle.</summary>
    private string Display(string characterName) =>
        _config.ScrambleNames && characterName.Length > 0 ? AliasFor(characterName) : characterName;

    /// <summary>
    /// Replaces every known character name inside free text (e.g. the boarding status line).
    /// Known names: LAN roster, manual whitelist, and anyone already aliased this session.
    /// </summary>
    private string ScrambleIn(string text)
    {
        if (!_config.ScrambleNames || text.Length == 0)
            return text;

        foreach (var toon in _roster.GetLanPartyMembers())
        {
            if (toon.CharacterName.Length > 0 && text.Contains(toon.CharacterName, StringComparison.Ordinal))
                text = text.Replace(toon.CharacterName, AliasFor(toon.CharacterName), StringComparison.Ordinal);
        }

        foreach (var entry in _whitelist.Entries)
        {
            if (entry.CharacterName.Length > 0 && text.Contains(entry.CharacterName, StringComparison.Ordinal))
                text = text.Replace(entry.CharacterName, AliasFor(entry.CharacterName), StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Section header row: gold title left, status dot right-aligned.</summary>
    private static void DrawSectionTitle(string title, bool active, bool showDot = true)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, CharonTheme.AccentGold);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();

        if (showDot)
        {
            var label = active ? "● Active" : "● Disabled";
            var rightX = ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(label).X;
            if (rightX > ImGui.GetCursorPosX())
                ImGui.SameLine(rightX);
            else
                ImGui.SameLine();
            ImGui.TextColored(active ? CharonTheme.StatusGreen : CharonTheme.StatusGrey, label);
        }

        ImGui.Separator();
    }
}
