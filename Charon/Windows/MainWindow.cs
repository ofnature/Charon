using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Charon.Features.AutoAccept;
using Charon.Features.AutoPillion;
using Charon.Features.Follow;
using Charon.Features.Gear;
using Charon.Features.GroupManagement;
using Charon.Features.HealWatch;
using Charon.Features.Loot;
using Charon.Services;
using Charon.Services.Game;

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
        FleetLeader,
        Follow,
        FcChest,
        Gear,
        Collect,
        TrustedList,
        Debug,
    }

    /// <summary>Sender-side follow command callbacks, wired from the plugin.</summary>
    public sealed record FollowCommands(
        Action<string> Follow,
        Action<string> Stop,
        Action FollowAll,
        Action StopAll);

    /// <summary>
    /// Fleet-leader callbacks. <paramref name="SetLeader"/> designates the leader here AND pushes it
    /// to every other box, so the name is only ever chosen once.
    /// </summary>
    public sealed record FleetCommands(
        Action<string> SetLeader,
        Action LeaveDuty);

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
    private readonly FcChestManager _fcChest;
    private readonly GearManager _gear;
    private readonly FollowManager _followManager;
    private readonly Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> _rawSeatOccupancy;
    private readonly Func<string> _boardingStatus;
    private readonly Func<string> _followStatus;
    private readonly Func<string> _revivalStatus;
    private readonly Func<string> _healStatus;
    private readonly Func<string> _followFleetStatus;
    private readonly Func<string> _dutyPopStatus;
    private readonly Func<string> _tradeStatus;
    private readonly Func<string> _gearStatus;
    private readonly Func<string> _dutyExitStatus;
    private readonly Func<string> _accountStatus;
    private readonly Func<string> _collectStatus;
    private readonly CollectionScanner _collection;
    private readonly Func<int> _partySize;
    private readonly Func<string, bool> _isInParty;
    private readonly Func<string> _localName;
    private readonly FollowCommands _followCommands;
    private readonly FleetCommands _fleetCommands;

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
        FcChestManager fcChest,
        GearManager gear,
        FollowManager followManager,
        Func<IReadOnlyList<(int Seat, uint EntityId, string Name)>> rawSeatOccupancy,
        Func<string> boardingStatus,
        Func<string> followStatus,
        Func<string> revivalStatus,
        Func<string> healStatus,
        Func<string> followFleetStatus,
        Func<string> dutyPopStatus,
        Func<string> tradeStatus,
        Func<string> gearStatus,
        Func<string> dutyExitStatus,
        Func<string> accountStatus,
        Func<string> collectStatus,
        CollectionScanner collection,
        Func<int> partySize,
        Func<string, bool> isInParty,
        Func<string> localName,
        FollowCommands followCommands,
        FleetCommands fleetCommands)
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
        _fcChest = fcChest;
        _gear = gear;
        _followManager = followManager;
        _rawSeatOccupancy = rawSeatOccupancy;
        _boardingStatus = boardingStatus;
        _followStatus = followStatus;
        _revivalStatus = revivalStatus;
        _healStatus = healStatus;
        _followFleetStatus = followFleetStatus;
        _dutyPopStatus = dutyPopStatus;
        _tradeStatus = tradeStatus;
        _gearStatus = gearStatus;
        _dutyExitStatus = dutyExitStatus;
        _accountStatus = accountStatus;
        _collectStatus = collectStatus;
        _collection = collection;
        _partySize = partySize;
        _isInParty = isInParty;
        _localName = localName;
        _followCommands = followCommands;
        _fleetCommands = fleetCommands;

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
        DrawNavItem("Fleet Leader", Section.FleetLeader, _config.FleetLeaderName.Length > 0);
        DrawNavItem("Follow", Section.Follow, _followManager.Following);
        DrawNavItem("FC Chest", Section.FcChest, null);
        DrawNavItem("Gear", Section.Gear, _config.GearIpcExecuteEnabled);
        DrawNavItem("Collect", Section.Collect, null);
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
            case Section.FleetLeader: DrawFleetLeaderSection(); break;
            case Section.Follow: DrawFollowSection(); break;
            case Section.FcChest: DrawFcChestSection(); break;
            case Section.Gear: DrawGearSection(); break;
            case Section.Collect: DrawCollectSection(); break;
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

        var autoCommence = _config.AutoCommenceDutyEnabled;
        if (ImGui.Checkbox("Auto-commence duty pops##accept", ref autoCommence))
        {
            _config.AutoCommenceDutyEnabled = autoCommence;
            _save();
        }
        CharonTheme.HelpMarker("Click Commence on the Duty Ready popup — but ONLY when every other\n"
                               + "party member is a trusted LAN toon (your fleet queueing together).\n"
                               + "A solo/roulette pop, or any stranger in the party, is left for you.");

        var autoTrade = _config.AutoTradeEnabled;
        if (ImGui.Checkbox("Mirror LAN toon trades##accept", ref autoTrade))
        {
            _config.AutoTradeEnabled = autoTrade;
            _save();
        }
        CharonTheme.HelpMarker("When a trusted LAN toon clicks Trade, this toon clicks Trade too and\n"
                               + "answers the \"Complete trade?\" prompt. Only mirrors — it never commits\n"
                               + "before the partner does, and never cancels. A trade with anyone who is\n"
                               + "NOT a LAN toon is left entirely alone.");

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

        // The receiving half of a raise: without this an unattended toon never answers the prompt,
        // so the raise is spent and the bot stays down. Belongs beside Raise even though it acts on
        // THIS toon rather than others.
        var acceptRevival = _config.AutoAcceptRevival;
        if (ImGui.Checkbox("Accept revival when raised##healwatch", ref acceptRevival))
        {
            _config.AutoAcceptRevival = acceptRevival;
            _save();
        }
        CharonTheme.HelpMarker("Answer the revival prompt on THIS toon when a raise lands.\n"
                               + "Unattended toons have nobody to click it, so the raise is wasted\n"
                               + "and they stay on the floor. Only ever fires while dead with a\n"
                               + "raise incoming — never guesses at other dialogs.");

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

    // --- Fleet Follow ---

    private void DrawFollowSection()
    {
        DrawPageHeader("Fleet Follow");

        // This box's own follow state (it may have been commanded to follow someone).
        if (_followManager.Following)
        {
            ImGui.TextColored(CharonTheme.StatusGreen, $"● This toon: {ScrambleIn(_followFleetStatus())}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##followself"))
                _followCommands.Stop(_localName());
        }
        else
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "This toon: not following anyone");
        }

        // Follow settings.
        var distance = _config.FollowDistance;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Follow Distance##follow", ref distance, 1.0f, 8.0f, "%.1f y"))
        {
            _config.FollowDistance = distance;
            _save();
        }
        CharonTheme.HelpMarker("How close a follower trails its leader before it stops moving.");

        var leash = _config.FollowCombatLeash;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Combat Leash##follow", ref leash, 2.0f, 30.0f, "%.0f y"))
        {
            _config.FollowCombatLeash = leash;
            _save();
        }
        CharonTheme.HelpMarker("Slack while in ordinary combat: the follower holds position until you get\n"
                               + "this far away, so a melee toon can stay on its target instead of being\n"
                               + "dragged out of range. Once you pass it, the toon closes all the way back\n"
                               + "to Follow Distance. Set at or below Follow Distance to follow tightly.\n"
                               + "Boss fights are unaffected — those hand movement to BMR entirely.");

        var stopInBoss = _config.FollowStopInBossFight;
        if (ImGui.Checkbox("Stop in boss fights##follow", ref stopInBoss))
        {
            _config.FollowStopInBossFight = stopInBoss;
            _save();
        }
        CharonTheme.HelpMarker("Pause following only while IN COMBAT during a BMR boss module (both true) —\n"
                               + "pre-pull and normal (non-boss) combat keep following. When it pauses,\n"
                               + "movement is handed to BossMod for the fight, then resumes automatically.");

        var reachCheck = _config.FollowReachabilityCheck;
        if (ImGui.Checkbox("Skip unreachable leaders##follow", ref reachCheck))
        {
            _config.FollowReachabilityCheck = reachCheck;
            _save();
        }
        CharonTheme.HelpMarker("Check the navmesh before pathing. If the leader took a portal or teleport\n"
                               + "stone and landed somewhere you can't walk to, hold instead of running at\n"
                               + "a wall — and resume the moment they're reachable again.");

        var takePortals = _config.FollowTakePortals;
        if (ImGui.Checkbox("Take the leader's portal##follow", ref takePortals))
        {
            _config.FollowTakePortals = takePortals;
            _save();
        }
        CharonTheme.HelpMarker("When the leader ports out of reach (raid arena transitions), walk to the\n"
                               + "spot they ported FROM and click the same portal. Only fires while the\n"
                               + "leader is unreachable — never clicks anything during normal following.");

        ImGui.Spacing();

        // Sender controls — command the fleet to follow this toon.
        var roster = _roster.GetLanPartyMembers();
        var localName = _localName();
        var onlineCount = roster.Count(t => t.IsOnline && !t.CharacterName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        var canCommand = onlineCount > 0 && _roster.IsAvailable;

        if (!canCommand) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, CharonTheme.AccentGold);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CharonTheme.AccentGold);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, CharonTheme.AccentDim);
        ImGui.PushStyleColor(ImGuiCol.Text, CharonTheme.BgDeep);
        if (ImGui.Button("Follow Me (All)", new Vector2(-1f, 0f)) && canCommand)
            _followCommands.FollowAll();
        ImGui.PopStyleColor(4);
        if (!canCommand) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_roster.IsAvailable
                ? "Tell every online LAN toon to follow this character (over the LAN relay)."
                : "Daedalus LAN roster/relay unavailable");

        ImGui.SameLine();
        if (ImGui.Button("Stop All##follow"))
            _followCommands.StopAll();

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextSecondary, $"LAN Party ({onlineCount} online)");

        if (roster.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "No LAN roster — is Daedalus running with the LAN coordinator on?");
        }
        else if (ImGui.BeginTable("followparty", 3,
                     ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##dot", ImGuiTableColumnFlags.WidthFixed, 16f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 160f);
            ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthStretch);

            foreach (var toon in roster)
            {
                var isSelf = toon.CharacterName.Equals(localName, StringComparison.OrdinalIgnoreCase);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(toon.IsOnline ? CharonTheme.StatusGreen : CharonTheme.StatusGrey,
                    toon.IsOnline ? "●" : "○");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Display(toon.CharacterName));
                ImGui.TableNextColumn();

                if (isSelf)
                {
                    ImGui.TextColored(CharonTheme.TextDisabled, "You");
                }
                else if (!toon.IsOnline)
                {
                    ImGui.TextColored(CharonTheme.TextDisabled, "Offline");
                }
                else
                {
                    if (ImGui.SmallButton($"Follow##f{toon.CharacterName}"))
                        _followCommands.Follow(toon.CharacterName);
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Stop##s{toon.CharacterName}"))
                        _followCommands.Stop(toon.CharacterName);
                }
            }

            ImGui.EndTable();
        }

        if (!_roster.IsAvailable)
            ImGui.TextColored(CharonTheme.TextDisabled,
                "Cross-box follow needs the Daedalus LAN relay. /charon follow <name> drives this box locally.");
    }

    // --- Fleet Leader ---

    /// <summary>
    /// Designate one toon as fleet leader and give it fleet-wide commands. The designation is what
    /// makes the commands safe: every box only obeys the leader it has configured, so a stray
    /// broadcast from an alt can't drag the fleet out of a duty.
    /// </summary>
    private void DrawFleetLeaderSection()
    {
        DrawPageHeader("Fleet Leader");

        var localName = _localName();
        var leader = _config.FleetLeaderName;
        var isLeader = leader.Length > 0 && leader.Equals(localName, StringComparison.OrdinalIgnoreCase);

        if (leader.Length == 0)
        {
            ImGui.TextColored(CharonTheme.StatusYellow, "No fleet leader set.");
            ImGui.TextColored(CharonTheme.TextDisabled,
                "Set the SAME toon on every box — that's the only toon whose commands are obeyed.");
        }
        else
        {
            ImGui.TextColored(isLeader ? CharonTheme.StatusGreen : CharonTheme.TextSecondary,
                isLeader ? $"● Fleet leader: {Display(leader)} (this toon)" : $"Fleet leader: {Display(leader)}");
        }

        ImGui.Spacing();

        // Pick from the LAN roster. Choosing here BROADCASTS to every box, so the leader only has
        // to be chosen once instead of set by hand on eight clients.
        var roster = _roster.GetLanPartyMembers();
        ImGui.SetNextItemWidth(200f);
        if (ImGui.BeginCombo("Fleet leader##pickleader", leader.Length > 0 ? Display(leader) : "(none)"))
        {
            if (ImGui.Selectable("(none)", leader.Length == 0))
                SetFleetLeader(string.Empty);

            // This toon first, even when the LAN roster is unavailable.
            if (localName.Length > 0 && !roster.Any(t => t.CharacterName.Equals(localName, StringComparison.OrdinalIgnoreCase)))
            {
                if (ImGui.Selectable($"{Display(localName)} (this toon)", isLeader))
                    SetFleetLeader(localName);
            }

            foreach (var toon in roster)
            {
                if (toon.CharacterName.Length == 0)
                    continue;

                var isThisToon = toon.CharacterName.Equals(localName, StringComparison.OrdinalIgnoreCase);
                var label = isThisToon
                    ? $"{Display(toon.CharacterName)} (this toon)"
                    : Display(toon.CharacterName);

                if (ImGui.Selectable($"{label}##leaderopt{toon.CharacterName}",
                        toon.CharacterName.Equals(leader, StringComparison.OrdinalIgnoreCase)))
                    SetFleetLeader(toon.CharacterName);

                if (!toon.IsOnline)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(CharonTheme.TextDisabled, "(offline)");
                }
            }

            ImGui.EndCombo();
        }
        CharonTheme.HelpMarker("Picking here sets the fleet leader on THIS box and broadcasts it to\n"
                               + "every other Charon on the LAN, so you only choose once.\n"
                               + "Clearing it is local only.");

        if (roster.Count == 0)
            ImGui.TextColored(CharonTheme.TextDisabled,
                "No LAN roster — only this toon is listed. Is Daedalus running with the LAN coordinator on?");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // --- Fleet commands (leader only) ---
        ImGui.TextColored(CharonTheme.TextSecondary, "Fleet commands");

        if (!isLeader)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, leader.Length == 0
                ? "Set a fleet leader to use these."
                : $"Only {Display(leader)} can issue these. This toon obeys them.");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, CharonTheme.AccentGold);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CharonTheme.AccentGold);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, CharonTheme.AccentDim);
            ImGui.PushStyleColor(ImGuiCol.Text, CharonTheme.BgDeep);
            if (ImGui.Button("Leave Duty (My Party)", new Vector2(-1f, 0f)))
                ImGui.OpenPopup("fleetLeaveDutyConfirm");
            ImGui.PopStyleColor(4);
            CharonTheme.HelpMarker("Leave the current duty on this toon and everyone in YOUR PARTY.\n"
                                   + "Toons in a different group — off running their own dungeon —\n"
                                   + "are not affected, and a party holding anyone outside the fleet\n"
                                   + "stays put.");

            if (ImGui.BeginPopupModal("fleetLeaveDutyConfirm", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextUnformatted("Leave the current duty on everyone in your party?");
                ImGui.TextColored(CharonTheme.TextSecondary,
                    "Fleet toons in a DIFFERENT group are not affected.");
                ImGui.TextColored(CharonTheme.TextSecondary,
                    "A party holding anyone outside the fleet stays put.");
                ImGui.Spacing();

                if (ImGui.Button("Leave Duty", new Vector2(120f, 0)))
                {
                    _fleetCommands.LeaveDuty();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(120f, 0)))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        }

        ImGui.Spacing();
        var obey = _config.FleetLeaveDutyEnabled;
        if (ImGui.Checkbox("Obey the fleet leader's Leave Duty", ref obey))
        {
            _config.FleetLeaveDutyEnabled = obey;
            _save();
        }
        CharonTheme.HelpMarker("Untick on a toon that should never be pulled out of a duty automatically.");

        var promote = _config.FleetAutoPromoteLeader;
        if (ImGui.Checkbox("Give party lead back to the fleet leader", ref promote))
        {
            _config.FleetAutoPromoteLeader = promote;
            _save();
        }
        CharonTheme.HelpMarker("A disconnect moves party leadership to another member — usually a bot —\n"
                               + "and it never comes back on its own. When this toon is holding lead and\n"
                               + "the fleet leader is back online in the party, it hands it over.\n"
                               + "Only the current party leader can promote, so this acts on whichever\n"
                               + "box inherited it.");

        ImGui.Spacing();
        DrawStatusLine($"Last: {ScrambleIn(_dutyExitStatus())}", CharonTheme.TextDisabled);

        if (!_roster.IsAvailable)
            ImGui.TextColored(CharonTheme.TextDisabled,
                "Fleet commands need the Daedalus LAN relay — without it only this toon responds.");
    }

    /// <summary>Designate the leader — the plugin applies it here and broadcasts it to the fleet.</summary>
    private void SetFleetLeader(string characterName) => _fleetCommands.SetLeader(characterName);

    // --- FC Chest Management ---

    private void DrawFcChestSection()
    {
        DrawPageHeader("FC Chest Management");

        var autoOpen = _config.FcChestWindowAutoOpen;
        if (ImGui.Checkbox("Pop a window when the FC chest opens", ref autoOpen))
        {
            _config.FcChestWindowAutoOpen = autoOpen;
            _save();
        }
        CharonTheme.HelpMarker("Automatically open a small FC Chest window next to the game's chest,\n"
                               + "so the entrust/withdraw tools are right there.");

        ImGui.Spacing();
        FcChestView.DrawBody(_config, _save, _fcChest);
    }

    // --- Gear Equipper ---

    private void DrawGearSection()
    {
        DrawPageHeader("Gear Equipper");

        var upgrades = _gear.GetUpgradePreview();
        var busy = _gear.Busy; // snapshot: a button press flips this mid-draw and unbalances BeginDisabled

        ImGui.TextColored(CharonTheme.TextSecondary,
            upgrades.Count == 0
                ? "No upgrades available — wearing the best gear in the bags and armoury."
                : $"{upgrades.Count} {(upgrades.Count == 1 ? "upgrade" : "upgrades")} available:");

        if (upgrades.Count > 0)
            DrawUpgradeTable(upgrades);

        ImGui.Spacing();

        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Equip upgrades") && !busy)
            _gear.StartEquipPass();
        if (busy) ImGui.EndDisabled();
        CharonTheme.HelpMarker("Equips the list above, one piece at a time, re-checking after each.\n"
                               + "Upgrades sitting in your bags move into the armoury first, so the\n"
                               + "gear they replace lands in the armoury instead of your bags.");

        ImGui.Spacing();
        DrawArmouryCleanup(busy);
        DrawKeepList();

        if (_gear.Status.Length > 0 && _gear.Status != "idle")
            ImGui.TextColored(CharonTheme.StatusYellow, _gear.Status);
        if (_gear.LastOperation.Length > 0)
            ImGui.TextColored(CharonTheme.TextDisabled, _gear.LastOperation);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var armouryOnly = _config.GearArmouryOnly;
        if (ImGui.Checkbox("Armoury only (skip the main bags)", ref armouryOnly))
        {
            _config.GearArmouryOnly = armouryOnly;
            _save();
        }
        CharonTheme.HelpMarker("Off by default: dungeon and SealBreaker loot lands in your main bags,\n"
                               + "so those need scanning too. Tick this to consider armoury gear only.");

        var updateGearset = _config.GearUpdateGearsetAfterPass;
        if (ImGui.Checkbox("Update the active gearset after equipping", ref updateGearset))
        {
            _config.GearUpdateGearsetAfterPass = updateGearset;
            _save();
        }
        CharonTheme.HelpMarker("Saves the newly worn pieces onto your current gearset, so swapping\n"
                               + "jobs and back keeps the upgrades.");

        var ipcEnabled = _config.GearIpcEnabled;
        if (ImGui.Checkbox("Allow other plugins to ask (IPC)", ref ipcEnabled))
        {
            _config.GearIpcEnabled = ipcEnabled;
            _save();
        }
        CharonTheme.HelpMarker("Exposes the upgrade count and equip request to SealBreaker, which uses\n"
                               + "them after a duty and before Expert Delivery so drops get worn, not\n"
                               + "turned in.");

        var executeEnabled = _config.GearIpcExecuteEnabled;
        if (ImGui.Checkbox("Let other plugins actually equip", ref executeEnabled))
        {
            _config.GearIpcExecuteEnabled = executeEnabled;
            _save();
        }
        CharonTheme.HelpMarker("Off = preview only: requests are logged and declined, and the caller\n"
                               + "falls back to the game's Equip Recommended. Turn this on once the\n"
                               + "preview list above matches what you'd equip by hand.");

        if (!_config.GearIpcExecuteEnabled)
            ImGui.TextColored(CharonTheme.StatusYellow,
                "Preview mode — the button above still works; only plugin requests are declined.");
    }

    /// <summary>
    /// Armoury cleanup: the full list of what would leave, each row vetoable. A vetoed item stays
    /// listed (greyed, ticked) so the veto can be undone — it must never just vanish.
    /// </summary>
    private void DrawArmouryCleanup(bool busy)
    {
        var rows = _gear.GetCleanupPreview();
        var evicting = rows.Count(r => !r.Kept);

        if (!ImGui.CollapsingHeader($"Armoury cleanup — {evicting} to remove###gearCleanup"))
            return;

        if (rows.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled,
                "Nothing to clean: every armoury item belongs to a gearset.");
            ImGui.TextColored(CharonTheme.TextDisabled,
                "(If you've just logged in, open your gearset list once so the game loads it.)");
            return;
        }

        ImGui.TextColored(CharonTheme.TextSecondary,
            "These armoury items aren't in any saved gearset. Tick Keep to protect one.");

        if (ImGui.BeginTable("gearCleanupRows", 3,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit
                | ImGuiTableFlags.ScrollY, new Vector2(0, 160)))
        {
            ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 42f);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Stacks", ImGuiTableColumnFlags.WidthFixed, 50f);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var kept = row.Kept;
                if (ImGui.Checkbox($"##keep{row.ItemId}", ref kept))
                    SetItemKept(row.ItemId, kept);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(kept
                        ? "Protected — cleanup will leave this in the armoury"
                        : "Protect this item from cleanup (every stack of it)");

                ImGui.TableNextColumn();
                ImGui.TextColored(row.Kept ? CharonTheme.TextDisabled : CharonTheme.TextSecondary, row.Name);
                if (row.ExpBonus.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(CharonTheme.AccentGold, "[EXP]");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{row.ExpBonus}\nProtected by default — untick Keep to let it go.");
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(CharonTheme.TextDisabled, row.StackCount.ToString());
            }

            ImGui.EndTable();
        }

        if (busy || evicting == 0) ImGui.BeginDisabled();
        if (ImGui.Button($"Move {evicting} to bags") && !busy && evicting > 0)
            ImGui.OpenPopup("gearCleanupConfirm");
        if (busy || evicting == 0) ImGui.EndDisabled();
        CharonTheme.HelpMarker("Moves the unticked items above back into your bags.\n"
                               + "Gearset gear is never touched, and soul crystals always stay put.");

        // Confirm modal — on a main's armoury this is one click from moving hundreds of items,
        // and the only things standing between them and your bags are your saved gearsets.
        if (ImGui.BeginPopupModal("gearCleanupConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Move {evicting} armoury item(s) to your bags?");
            ImGui.TextColored(CharonTheme.TextSecondary,
                "Everything not referenced by a saved gearset goes, including glamour");
            ImGui.TextColored(CharonTheme.TextSecondary,
                "pieces and gear for jobs you have no gearset for.");

            // Bag space is the practical limit — 283 items do not fit in four bags, and the run
            // would stop partway with full bags. Say so BEFORE the click, not after.
            var bagSpace = _gear.CountFreeBagSlots();
            if (evicting > bagSpace)
                ImGui.TextColored(CharonTheme.StatusYellow,
                    $"Only {bagSpace} free bag slot(s) — it will move what fits and stop.");

            ImGui.Spacing();
            if (ImGui.Button("Confirm", new Vector2(120f, 0)))
            {
                _gear.StartArmouryCleanup();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120f, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        var keptCount = rows.Count - evicting;
        if (keptCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDisabled,
                $"{keptCount} kept");
        }
    }

    /// <summary>
    /// The keep list in full — including items NOT currently in the armoury, which never appear in
    /// the cleanup preview. Without this a stray Keep click is invisible and permanent: the item
    /// leaves the armoury, its row disappears, and the protection silently persists forever.
    /// </summary>
    private void DrawKeepList()
    {
        var kept = _gear.GetKeptItems();

        if (!ImGui.CollapsingHeader($"Protected from cleanup — {kept.Count} item(s)###gearKeepList"))
            return;

        if (kept.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "Nothing protected. Tick Keep on a cleanup row to add one.");
            return;
        }

        ImGui.TextColored(CharonTheme.TextSecondary,
            "Armoury cleanup will never move these. Ticked one by mistake? Remove it here.");

        if (ImGui.BeginTable("gearKeepRows", 3,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit
                | ImGuiTableFlags.ScrollY, new Vector2(0, 140)))
        {
            ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 28f);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableHeadersRow();

            foreach (var row in kept)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"x##unkeep{row.ItemId}"))
                    SetItemKept(row.ItemId, false);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Stop protecting this item");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Name);
                if (row.ExpBonus.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(CharonTheme.AccentGold, "[EXP]");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{row.ExpBonus}\nProtected by default.");
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(row.InArmoury ? CharonTheme.TextSecondary : CharonTheme.TextDisabled,
                    row.InArmoury ? "armoury" : "elsewhere");
            }

            ImGui.EndTable();
        }

        var missingDefaults = ExpBonusItems.ItemIds
            .Where(id => !_config.GearNeverEvictItemIds.Contains(id))
            .ToList();
        if (missingDefaults.Count == 0)
            return;

        if (ImGui.Button($"Restore EXP gear protection ({missingDefaults.Count})"))
        {
            foreach (var id in missingDefaults)
                _config.GearNeverEvictItemIds.Add(id);
            _save();
            _gear.InvalidatePreview();
        }
        CharonTheme.HelpMarker("Re-protects the EXP-bonus gear (Brand-new Ring, the pre-order\n"
                               + "earrings, and friends) that ships protected by default.");
    }

    /// <summary>Add/remove an item from the never-evict list and refresh the preview at once.</summary>
    private void SetItemKept(uint itemId, bool kept)
    {
        if (kept)
        {
            if (!_config.GearNeverEvictItemIds.Contains(itemId))
                _config.GearNeverEvictItemIds.Add(itemId);
        }
        else
        {
            _config.GearNeverEvictItemIds.Remove(itemId);
        }

        _save();
        _gear.InvalidatePreview(); // otherwise the tick doesn't show for up to half a second
    }

    private static void DrawUpgradeTable(IReadOnlyList<GearUpgrade> upgrades)
    {
        if (!ImGui.BeginTable("gearUpgrades", 4,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 75f);
        ImGui.TableSetupColumn("Wearing", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Upgrade", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("ilvl", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableHeadersRow();

        foreach (var upgrade in upgrades)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.TextSecondary, upgrade.Slot.ToString());
            ImGui.TableNextColumn();
            if (upgrade.Replacing == null)
                ImGui.TextColored(CharonTheme.TextDisabled, "(empty)");
            else
                ImGui.TextUnformatted(upgrade.Replacing.Name);
            ImGui.TableNextColumn();
            ImGui.TextColored(CharonTheme.AccentGold, upgrade.Item.Name);
            ImGui.TableNextColumn();
            if (upgrade.IlvlGain > 0)
            {
                ImGui.TextColored(CharonTheme.StatusGreen, $"+{upgrade.IlvlGain}");
            }
            else
            {
                // Same item level, better stats for this job — the usual case at max level.
                ImGui.TextColored(CharonTheme.AccentGold, "stats");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Same item level, but a better stat spread for this job.");
            }
        }

        ImGui.EndTable();
    }

    // --- Collect (unlearned collectibles) ---

    /// <summary>
    /// Collectibles sitting unlearned in the bags, each with its own Collect button. These arrive
    /// with no looting involved — MSQ rewards, trust runs, AutoDuty runs — so an unattended toon
    /// accumulates them for weeks. Nothing is consumed without a click.
    /// </summary>
    private void DrawCollectSection()
    {
        DrawPageHeader("Collect");

        var rows = _collection.GetUnlearned();

        ImGui.TextColored(CharonTheme.TextSecondary, rows.Count == 0
            ? "Nothing unlearned in the bags."
            : $"{rows.Count} collectible(s) in the bags you haven't learned:");
        CharonTheme.HelpMarker("Mounts, minions, Triple Triad cards, orchestrion rolls, emotes and "
                               + "hairstyles you don't own yet. Duplicates never appear — the game "
                               + "won't relearn one, so anything worth selling stays untouched.");

        if (rows.Count > 0 && ImGui.BeginTable("collectRows", 3,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit
                | ImGuiTableFlags.ScrollY, new Vector2(0, 220)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 74f);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Name);
                ImGui.TableNextColumn();
                ImGui.TextColored(CharonTheme.TextSecondary, row.Category);
                ImGui.TableNextColumn();
                var here = _collection.CanCollectHere(row);
                if (!here) ImGui.BeginDisabled();
                if (ImGui.SmallButton($"Collect##collect{row.Container}_{row.Slot}") && here)
                    _collection.TryCollect(row.ItemId, row.ActionKind, highQuality: false);
                if (!here) ImGui.EndDisabled();
                if (!here && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Only usable in the Occult Crescent (South Horn or North Horn)");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.Button("Refresh"))
            _collection.Invalidate();
        CharonTheme.HelpMarker("The list refreshes on its own every second — this is only for impatience.");

        ImGui.Spacing();
        DrawStatusLine(_collection.Status, CharonTheme.TextDisabled);
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

    /// <summary>
    /// A Debug status line that WRAPS. These lines are the primary in-game diagnostic and they grow
    /// long (a status plus a reason plus a name), so clipping at the window edge hides exactly the
    /// part that explains why something isn't acting.
    /// </summary>
    private static void DrawStatusLine(string text, Vector4? color = null)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color ?? CharonTheme.TextSecondary);
        ImGui.PushTextWrapPos(0f); // wrap at the content region edge
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private void DrawDebugSection()
    {
        DrawPageHeader("Debug");

        DrawStatusLine($"Account: {_accountStatus()}");
        DrawStatusLine($"Daedalus IPC: {(_roster.IsAvailable ? "connected" : "unavailable — manual whitelist only")}");
        DrawStatusLine($"Boarding: {ScrambleIn(_boardingStatus())}");
        DrawStatusLine($"Follow: {ScrambleIn(_followStatus())}");
        DrawStatusLine($"Fleet Follow: {ScrambleIn(_followFleetStatus())}");
        DrawStatusLine($"Heal Watch: {ScrambleIn(_healStatus())}");
        DrawStatusLine($"Revival prompt: {_revivalStatus()}");
        DrawStatusLine($"Duty pop: {_dutyPopStatus()}");
        DrawStatusLine($"Trade: {ScrambleIn(_tradeStatus())}");
        DrawStatusLine($"Gear: {_gearStatus()}");
        DrawStatusLine($"Collect: {_collectStatus()}");
        DrawStatusLine($"Fleet duty exit: {ScrambleIn(_dutyExitStatus())}");
        if (_inviteManager.AcceptPending)
            DrawStatusLine("Invite accept pending (delay running)", CharonTheme.StatusYellow);

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
