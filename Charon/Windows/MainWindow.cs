using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Features.AutoAccept;
using Charon.Features.AutoPillion;
using Charon.Features.GroupManagement;
using Charon.Features.HealWatch;
using Charon.Services;

namespace Charon.Windows;

/// <summary>
/// Charon's window, Daedalus-config style: sidebar navigation on the left (grey small-cap
/// category headers, gold selection with a left accent bar over a faint gold wash), content
/// page on the right. Sections: General (auto accept + follow teleport), Auto Pillion
/// (settings + rider list + collapsible debug details), Heal Watch, Trusted Characters, Debug.
/// </summary>
public sealed class MainWindow : Window
{
    private enum Section
    {
        General,
        AutoPillion,
        HealWatch,
        GroupMgmt,
        TrustedList,
        Debug,
    }

    private const float SidebarWidth = 140f;
    private static readonly Vector4 AccentWash = new(0.85f, 0.65f, 0.20f, 0.10f);

    private readonly CharonConfig _config;
    private readonly Action _save;
    private readonly WhitelistService _whitelist;
    private readonly IDaedalusRosterProvider _roster;
    private readonly PillionManager _pillion;
    private readonly GroupInviteManager _inviteManager;
    private readonly HealWatchManager _healWatch;
    private readonly InviteManager _groupInvites;
    private readonly Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> _rawSeatOccupancy;
    private readonly Func<string> _boardingStatus;
    private readonly Func<string> _followStatus;
    private readonly Func<string> _healStatus;
    private readonly Func<int> _partySize;
    private readonly Func<string, bool> _isInParty;
    private readonly Func<string> _localName;

    private Section _section = Section.General;
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

    /// <summary>Mock rider rows shown in the Auto Pillion section while no session is live.</summary>
    private static readonly (int Seat, SeatStatus Status, string Name)[] MockRiders =
    [
        (1, SeatStatus.Filled, "Styx"),
        (2, SeatStatus.InvitePending, "Lethe"),
        (3, SeatStatus.Available, ""),
    ];

    public MainWindow(
        CharonConfig config,
        Action save,
        WhitelistService whitelist,
        IDaedalusRosterProvider roster,
        PillionManager pillion,
        GroupInviteManager inviteManager,
        HealWatchManager healWatch,
        InviteManager groupInvites,
        Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> rawSeatOccupancy,
        Func<string> boardingStatus,
        Func<string> followStatus,
        Func<string> healStatus,
        Func<int> partySize,
        Func<string, bool> isInParty,
        Func<string> localName)
        : base("Charon##CharonMain")
    {
        _config = config;
        _save = save;
        _whitelist = whitelist;
        _roster = roster;
        _pillion = pillion;
        _inviteManager = inviteManager;
        _healWatch = healWatch;
        _groupInvites = groupInvites;
        _rawSeatOccupancy = rawSeatOccupancy;
        _boardingStatus = boardingStatus;
        _followStatus = followStatus;
        _healStatus = healStatus;
        _partySize = partySize;
        _isInParty = isInParty;
        _localName = localName;

        Size = new Vector2(600, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 340),
            MaximumSize = new Vector2(900, 800),
        };
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);
        try
        {
            DrawSidebar();
            ImGui.SameLine();
            DrawContent();
        }
        finally
        {
            ImGui.PopStyleVar(2);
        }
    }

    // --- Sidebar ---

    private void DrawSidebar()
    {
        ImGui.BeginChild("##CharonSidebar", new Vector2(SidebarWidth, 0), true);

        DrawCategoryHeader("FEATURES");
        DrawNavItem("General", Section.General, _config.AutoAcceptEnabled || _config.FollowTeleportEnabled);
        DrawNavItem("Auto Pillion", Section.AutoPillion, _config.AutoPillionEnabled);
        DrawNavItem("Heal Watch", Section.HealWatch, _config.HealWatchEnabled);
        ImGui.Spacing();

        DrawCategoryHeader("FLEET");
        DrawNavItem("Group Mgmt", Section.GroupMgmt, null);
        DrawNavItem("Trusted List", Section.TrustedList, null);
        ImGui.Spacing();

        DrawCategoryHeader("SYSTEM");
        DrawNavItem("Debug", Section.Debug, null);

        ImGui.EndChild();
    }

    private static void DrawCategoryHeader(string label)
    {
        ImGui.TextColored(CharonTheme.StatusGrey, label);
    }

    /// <summary>Nav row: gold selection wash + 2px left accent bar (Daedalus sidebar identity).</summary>
    private void DrawNavItem(string label, Section section, bool? active)
    {
        var isSelected = _section == section;

        if (isSelected)
        {
            var cursorPos = ImGui.GetCursorScreenPos();
            var regionAvail = ImGui.GetContentRegionAvail();
            var drawList = ImGui.GetWindowDrawList();
            var rowMax = new Vector2(cursorPos.X + regionAvail.X, cursorPos.Y + ImGui.GetTextLineHeightWithSpacing());
            drawList.AddRectFilled(cursorPos, rowMax, ImGui.GetColorU32(AccentWash));
            drawList.AddRectFilled(cursorPos, new Vector2(cursorPos.X + 2f, rowMax.Y), ImGui.GetColorU32(CharonTheme.AccentGold));
        }

        ImGui.Indent(10);
        ImGui.PushStyleColor(ImGuiCol.Text, isSelected ? CharonTheme.AccentGold : CharonTheme.TextSecondary);
        ImGui.PushStyleColor(ImGuiCol.Header, AccentWash);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, AccentWash);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, AccentWash);

        if (ImGui.Selectable($"  {label}##{section}", isSelected, ImGuiSelectableFlags.None,
                new Vector2(SidebarWidth - 25, 0)))
            _section = section;

        ImGui.PopStyleColor(4);
        ImGui.Unindent(10);

        // Feature-state dot flush right on the row (green = enabled, grey = off).
        if (active != null)
        {
            ImGui.SameLine(SidebarWidth - 18);
            ImGui.TextColored(active.Value ? CharonTheme.StatusGreen : CharonTheme.TextDisabled, "●");
        }
    }

    // --- Content ---

    private void DrawContent()
    {
        ImGui.BeginChild("##CharonContent", new Vector2(0, 0), true);

        switch (_section)
        {
            case Section.General: DrawGeneralSection(); break;
            case Section.AutoPillion: DrawAutoPillionSection(); break;
            case Section.HealWatch: DrawHealWatchSection(); break;
            case Section.GroupMgmt: DrawGroupSection(); break;
            case Section.TrustedList: DrawTrustedSection(); break;
            case Section.Debug: DrawDebugSection(); break;
        }

        ImGui.EndChild();
    }

    private static void DrawPageHeader(string title)
    {
        ImGui.TextColored(CharonTheme.AccentGold, title);
        ImGui.Separator();
        ImGui.Spacing();
    }

    // --- General: Auto Accept + Follow Teleport ---

    private void DrawGeneralSection()
    {
        DrawPageHeader("General");

        ImGui.TextColored(CharonTheme.TextSecondary, "Auto Accept Invites");
        var acceptEnabled = _config.AutoAcceptEnabled;
        if (ImGui.Checkbox("Enabled##accept", ref acceptEnabled))
        {
            _config.AutoAcceptEnabled = acceptEnabled;
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
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(CharonTheme.TextSecondary, "Follow Teleport");
        var followEnabled = _config.FollowTeleportEnabled;
        if (ImGui.Checkbox("Enabled##follow", ref followEnabled))
        {
            _config.FollowTeleportEnabled = followEnabled;
            _save();
        }
        CharonTheme.HelpMarker("When a trusted party member teleports to another zone, follow them\n"
                               + "(accepts the native teleport offer; falls back to teleporting to an\n"
                               + "unlocked aetheryte in their new zone). Same group only.");

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextDisabled,
            $"Daedalus IPC: {(_roster.IsAvailable ? "connected" : "unavailable — manual whitelist only")}");
    }

    // --- Auto Pillion ---

    private void DrawAutoPillionSection()
    {
        DrawPageHeader("Auto Pillion");

        var enabled = _config.AutoPillionEnabled;
        if (ImGui.Checkbox("Enabled##pillion", ref enabled))
        {
            _config.AutoPillionEnabled = enabled;
            _save();
        }
        CharonTheme.HelpMarker("Scan seats when a trusted party member mounts a multi-passenger mount\n"
                               + "and board automatically — no seat-2 spam.");

        var lanOnly = _config.LanMembersOnly;
        if (ImGui.Checkbox("LAN Members Only##pillion", ref lanOnly))
        {
            _config.LanMembersOnly = lanOnly;
            _save();
        }
        CharonTheme.HelpMarker("Only ride with / invite Daedalus LAN party members;\nskip the manual whitelist for pillion.");

        var delay = _config.PillionDelay;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Invite Delay##pillion", ref delay, 0.0f, 5.0f, "%.1f s"))
        {
            _config.PillionDelay = delay;
            _save();
        }
        CharonTheme.HelpMarker("Wait after mounting before boarding starts,\nso the mount animation can finish.");

        var timeout = _config.SeatTimeout;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Seat Timeout##pillion", ref timeout, 1.0f, 15.0f, "%.1f s"))
        {
            _config.SeatTimeout = timeout;
            _save();
        }
        CharonTheme.HelpMarker("Unanswered seat assignments are marked declined after this long.\nDeclined seats are never re-invited.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawRiderList();
    }

    /// <summary>
    /// Mount rider list: live session seats when mounted, mock preview rows otherwise —
    /// the section stays designable/inspectable without a mount. Debug internals live in
    /// the collapsible Details tree below it.
    /// </summary>
    private void DrawRiderList()
    {
        var live = _pillion.SessionActive;
        ImGui.TextColored(CharonTheme.TextSecondary, "Mount Riders");
        if (live)
        {
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDisabled,
                $"{_pillion.PassengerSeats + 1}-person mount · {_pillion.SeatsFilled}/{_pillion.PassengerSeats} filled");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDisabled, "(mock preview — mount a multi-seat mount for live data)");
        }

        if (ImGui.BeginTable("riders", 3,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Seat", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Rider", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            if (live)
            {
                foreach (var seat in _pillion.Seats)
                    DrawRiderRow(seat.Index, seat.Status, seat.AssignedName);
            }
            else
            {
                foreach (var (seatIndex, status, name) in MockRiders)
                    DrawRiderRow(seatIndex, status, name);
            }

            ImGui.EndTable();
        }

        // Collapsible debug internals for this feature.
        if (ImGui.TreeNode("Details##pillionDebug"))
        {
            ImGui.TextColored(CharonTheme.TextDisabled, $"Boarding: {ScrambleIn(_boardingStatus())}");
            if (live)
                ImGui.TextColored(CharonTheme.TextDisabled, $"Mount id: {_pillion.MountId}");

            ImGui.TextColored(CharonTheme.TextDisabled, "Raw seat data (game)");
            var raw = _rawSeatOccupancy();
            if (raw.Count == 0)
            {
                ImGui.TextColored(CharonTheme.TextDisabled, "  (not mounted)");
            }
            else
            {
                foreach (var (seatIndex, entityId, name) in raw)
                {
                    var id = _config.ScrambleNames ? "0x········" : $"0x{entityId:X8}";
                    ImGui.TextColored(CharonTheme.TextDisabled,
                        entityId == 0
                            ? $"  #{seatIndex}: empty"
                            : $"  #{seatIndex}: {id} {(name.Length > 0 ? Display(name) : "(unresolved)")}");
                }
            }

            ImGui.TreePop();
        }
    }

    private void DrawRiderRow(int seatIndex, SeatStatus status, string name)
    {
        var color = status switch
        {
            SeatStatus.Filled => CharonTheme.StatusGreen,
            SeatStatus.InvitePending => CharonTheme.StatusYellow,
            SeatStatus.Declined => CharonTheme.StatusRed,
            _ => CharonTheme.StatusGrey,
        };

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(CharonTheme.TextSecondary, $"#{seatIndex}");
        ImGui.TableNextColumn();
        ImGui.TextColored(color, status.ToString());
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(name.Length > 0 ? Display(name) : "—");
    }

    // --- Heal Watch ---

    private void DrawHealWatchSection()
    {
        DrawPageHeader("Heal Watch");

        var enabled = _config.HealWatchEnabled;
        if (ImGui.Checkbox("Enabled##healwatch", ref enabled))
        {
            _config.HealWatchEnabled = enabled;
            _save();
        }
        CharonTheme.HelpMarker("On a healer job, top up fleet toons from the Daedalus LAN vitals —\n"
                               + "including toons OUTSIDE your party. Stands down automatically while\n"
                               + "the Daedalus rotation is enabled.");

        var thresholdPct = _config.HealThreshold * 100f;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Heal Below##healwatch", ref thresholdPct, 30f, 95f, "%.0f%%"))
        {
            _config.HealThreshold = thresholdPct / 100f;
            _save();
        }
        CharonTheme.HelpMarker("Heal anyone at or below this HP fraction (live HP is re-checked\nbefore every cast).");

        var emergencyPct = _config.EmergencyThreshold * 100f;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Emergency##healwatch", ref emergencyPct, 10f, 60f, "%.0f%%"))
        {
            _config.EmergencyThreshold = emergencyPct / 100f;
            _save();
        }
        CharonTheme.HelpMarker("At or below this, a toon jumps the queue.");

        var outOfParty = _config.HealOutOfPartyOnly;
        if (ImGui.Checkbox("Out-of-party only##healwatch", ref outOfParty))
        {
            _config.HealOutOfPartyOnly = outOfParty;
            _save();
        }
        CharonTheme.HelpMarker("Skip toons in our own party — healing them is the rotation's job.");

        var maintainHot = _config.HealMaintainHot;
        if (ImGui.Checkbox("Maintain HoT / Shield##healwatch", ref maintainHot))
        {
            _config.HealMaintainHot = maintainHot;
            _save();
        }
        CharonTheme.HelpMarker("Keep the job's HoT/shield on damaged toons (WHM Regen, SCH Galvanize,\n"
                               + "AST Aspected Benefic). Recasts only when the status is about to expire —\n"
                               + "never clips a running one.");

        var raiseDead = _config.HealRaiseDead;
        if (ImGui.Checkbox("Raise dead toons##healwatch", ref raiseDead))
        {
            _config.HealRaiseDead = raiseDead;
            _save();
        }
        CharonTheme.HelpMarker("Hardcast raise on dead fleet toons (no swiftcast).\nSkips anyone who already has a raise pending.");

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextSecondary, ScrambleIn(_healStatus()));

        DrawHealLog();
    }

    private void DrawHealLog()
    {
        if (_healWatch.HealLog.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextSecondary, "Recent casts");
        foreach (var heal in _healWatch.HealLog)
        {
            var kind = heal.Kind switch
            {
                HealKind.Hot => "[HoT]",
                HealKind.Raise => "[RAISE]",
                _ => heal.Emergency ? "[EMERGENCY]" : "[heal]",
            };
            var color = heal.Kind == HealKind.Raise || heal.Emergency
                ? CharonTheme.StatusRed
                : CharonTheme.TextDisabled;
            ImGui.TextColored(color, $"{heal.TimeUtc:HH:mm:ss}  {Display(heal.Name)}  {kind}");
        }
    }

    // --- Group Management ---

    private void DrawGroupSection()
    {
        DrawPageHeader("Group Management");

        var partySize = Math.Max(_partySize(), 1); // solo counts as a party of one
        var full = partySize >= InviteManager.MaxPartySize;
        var roster = _roster.GetLanPartyMembers();
        var localName = _localName();
        var onlineCount = roster.Count(t => t.IsOnline);

        ImGui.TextColored(CharonTheme.TextSecondary, $"Group: {partySize}/{InviteManager.MaxPartySize}");
        if (_groupInvites.PendingCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.StatusYellow, $"· {_groupInvites.PendingCount} invites in flight");
        }

        ImGui.Spacing();

        // Mass invite — gold accent, full width; disabled at 8/8 or with nothing to invite.
        var canMass = !full && onlineCount > 0 && _roster.IsAvailable;
        if (!canMass) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, CharonTheme.AccentGold);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CharonTheme.AccentGold);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, CharonTheme.AccentDim);
        ImGui.PushStyleColor(ImGuiCol.Text, CharonTheme.BgDeep);
        if (ImGui.Button("Mass Invite All", new Vector2(-1f, 0f)) && canMass)
            _groupInvites.InviteAll(roster, localName, _partySize(), _isInParty, DateTime.UtcNow);
        ImGui.PopStyleColor(4);
        if (!canMass) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(full
                ? "Party is full (8/8)"
                : _roster.IsAvailable
                    ? "Invite every online LAN toon not already grouped (staggered).\nTheir Charon auto-accept does the rest."
                    : "Daedalus LAN roster unavailable");

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextSecondary, $"LAN Party ({onlineCount} online)");

        if (roster.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "No LAN roster — is Daedalus running with the LAN coordinator on?");
        }
        else if (ImGui.BeginTable("lanparty", 4,
                     ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##dot", ImGuiTableColumnFlags.WidthFixed, 16f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthStretch);

            foreach (var toon in roster)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(toon.IsOnline ? CharonTheme.StatusGreen : CharonTheme.StatusGrey,
                    toon.IsOnline ? "●" : "○");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Display(toon.CharacterName));
                ImGui.TableNextColumn();
                ImGui.TextColored(CharonTheme.TextSecondary, toon.World.Length > 0 ? toon.World : "—");
                ImGui.TableNextColumn();

                var isSelf = toon.CharacterName.Equals(localName, StringComparison.OrdinalIgnoreCase);
                if (isSelf)
                    ImGui.TextColored(CharonTheme.TextDisabled, "You");
                else if (_isInParty(toon.CharacterName))
                    ImGui.TextColored(CharonTheme.StatusGreen, "In Group");
                else if (!toon.IsOnline)
                    ImGui.TextColored(CharonTheme.TextDisabled, "Offline");
                else if (full)
                    ImGui.TextColored(CharonTheme.TextDisabled, "Party full");
                else if (ImGui.SmallButton($"Invite##inv{toon.CharacterName}"))
                    _groupInvites.InviteSingle(toon, DateTime.UtcNow);
            }

            ImGui.EndTable();
        }

        if (_groupInvites.InviteLog.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(CharonTheme.TextSecondary, "Invites sent");
            foreach (var entry in _groupInvites.InviteLog)
            {
                ImGui.TextColored(entry.Success ? CharonTheme.TextDisabled : CharonTheme.StatusRed,
                    $"{entry.TimeUtc:HH:mm:ss}  {ScrambleIn(entry.Detail)}");
            }
        }
    }

    // --- Trusted Characters ---

    private void DrawTrustedSection()
    {
        DrawPageHeader("Trusted Characters");
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
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthStretch);

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
            ImGui.SetTooltip("Replace character names with aliases everywhere in this window.\nCosmetic only — for screenshots.");

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

    // --- Debug ---

    private void DrawDebugSection()
    {
        DrawPageHeader("Debug");

        ImGui.TextColored(CharonTheme.TextSecondary,
            $"Daedalus IPC: {(_roster.IsAvailable ? "connected" : "unavailable — manual whitelist only")}");
        ImGui.TextColored(CharonTheme.TextSecondary, $"Boarding: {ScrambleIn(_boardingStatus())}");
        ImGui.TextColored(CharonTheme.TextSecondary, $"Follow: {ScrambleIn(_followStatus())}");
        ImGui.TextColored(CharonTheme.TextSecondary, $"Heal Watch: {ScrambleIn(_healStatus())}");
        if (_inviteManager.AcceptPending)
            ImGui.TextColored(CharonTheme.StatusYellow, "Invite accept pending (delay running)");

        DrawHealLog();

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
    }

    // --- Scramble helpers ---

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
}
