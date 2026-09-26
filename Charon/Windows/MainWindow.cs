using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Charon.Features.AutoAccept;
using Charon.Features.AutoPillion;
using Charon.Features.Follow;
using Charon.Features.Gear;
using Charon.Features.GroupManagement;
using Charon.Features.HealWatch;
using Charon.Features.Leveling;
using Charon.Features.Loot;
using Charon.Features.GrandCompany;
using Charon.Features.Retainers;
using Charon.Features.Weeklies;
using Charon.Services;
using Charon.Services.Game;
using Charon.Windows.Components;

namespace Charon.Windows;

/// <summary>
/// Charon's window in the Argus idiom (see sketches/004-board-teal-selected): a compact identity strip
/// across the top (obol mark, wordmark, status pill, cog), an icon sidebar with category labels and
/// badges, and a content page per section. The landing page is the fleet board — every background
/// feature's status line with a state dot and its reason for being idle, the LAN fleet as a table, and
/// this character's switches next to it. Section pages keep their existing controls.
/// </summary>
public sealed class MainWindow : Window
{
    private enum Section
    {
        Board,
        General,
        AutoPillion,
        HealWatch,
        QuickKill,
        Spawns,
        GroupMgmt,
        FleetLeader,
        Follow,
        FcChest,
        Gear,
        Collect,
        Loot,
        TrustedList,
        GilCapping,
        Retainers,
        GcDailies,
        Weeklies,
        DomanDonate,
        Tweaks,
        DeepDungeon,
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

    private readonly CharonConfig _config;
    private readonly Action _save;

    /// <summary>The retainer plans and the item catalog, shared with the board and the bell overlay.</summary>
    private readonly RetainerPlanner _retainerPlanner;

    /// <summary>Opens the standalone board — the working surface, from where the decisions are made.</summary>
    private readonly Action _openRetainerBoard;

    /// <summary>The retainer contents store: what each retainer holds, and the two passes that use it.</summary>
    private readonly RetainerContentsReader _retainerContents;

    /// <summary>The Grand Company delivery board (Supply / Provisioning / Expert Delivery).</summary>
    private readonly GcDailiesReader _gcDailies;

    /// <summary>Live state for the TWEAKS toggle: the timer it saw and whether a nudge landed.</summary>
    private readonly AfkGuard _afkGuard;

    private string _retainerFarmInput = string.Empty;
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
    private readonly Func<string> _sprintStatus;
    private readonly Func<string> _navStatus;
    private readonly Func<string> _qolStatus;
    private readonly Func<string> _lootStatus;
    private readonly Func<string> _levelingStatus;
    private readonly QuickKillExecutor _quickKill;
    private readonly Func<ulong> _localContentId;
    private readonly SpawnScanner _spawnScanner;
    private readonly GilCapSeller _gilSeller;
    private readonly DomanDonator _doman;
    private readonly WeekliesReader _weeklies;
    private readonly RetainerReader _retainers;
    private readonly VentureRunner _ventureRunner;
    private readonly Func<bool> _isFreeTrial;
    private readonly LootWatcher _lootWatcher;
    private readonly CollectionScanner _collection;
    private readonly Func<int> _partySize;
    private readonly Func<string, bool> _isInParty;
    private readonly Func<string> _localName;
    private readonly FollowCommands _followCommands;
    private readonly Func<string, string?> _reportedFollowLeader;
    private readonly FleetCommands _fleetCommands;

    private Section _section = Section.General;
    private string _spawnName = string.Empty;
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
        Func<string> sprintStatus,
        Func<string> navStatus,
        Func<string> qolStatus,
        Func<string> lootStatus,
        Func<string> levelingStatus,
        QuickKillExecutor quickKill,
        Func<ulong> localContentId,
        SpawnScanner spawnScanner,
        GilCapSeller gilSeller,
        DomanDonator domanDonator,
        WeekliesReader weeklies,
        RetainerReader retainers,
        VentureRunner ventureRunner,
        RetainerPlanner retainerPlanner,
        Action openRetainerBoard,
        RetainerContentsReader retainerContents,
        GcDailiesReader gcDailies,
        AfkGuard afkGuard,
        Func<bool> isFreeTrial,
        LootWatcher lootWatcher,
        CollectionScanner collection,
        Func<int> partySize,
        Func<string, bool> isInParty,
        Func<string> localName,
        FollowCommands followCommands,
        Func<string, string?> reportedFollowLeader,
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
        _sprintStatus = sprintStatus;
        _navStatus = navStatus;
        _qolStatus = qolStatus;
        _lootStatus = lootStatus;
        _levelingStatus = levelingStatus;
        _quickKill = quickKill;
        _localContentId = localContentId;
        _spawnScanner = spawnScanner;
        _gilSeller = gilSeller;
        _doman = domanDonator;
        _weeklies = weeklies;
        _retainers = retainers;
        _ventureRunner = ventureRunner;
        _retainerPlanner = retainerPlanner;
        _openRetainerBoard = openRetainerBoard;
        _retainerContents = retainerContents;
        _gcDailies = gcDailies;
        _afkGuard = afkGuard;
        _isFreeTrial = isFreeTrial;
        _lootWatcher = lootWatcher;
        _collection = collection;
        _partySize = partySize;
        _isInParty = isInParty;
        _localName = localName;
        _followCommands = followCommands;
        _reportedFollowLeader = reportedFollowLeader;
        _fleetCommands = fleetCommands;

        Size = new Vector2(1080, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(940, 560),
            MaximumSize = new Vector2(1600, 1200),
        };
    }

    public override void Draw()
    {
        using var style = Styling.PushWindowStyle();

        DrawHeader();
        Styling.VSpace(5f);

        var sidebarWidth = Layout.SidebarWidth * ImGuiHelpers.GlobalScale;
        using (ImRaii.Child("##CharonSidebar", new Vector2(sidebarWidth, -1), false))
            DrawSidebar();

        ImGui.SameLine();

        using (ImRaii.Child("##CharonContent", new Vector2(-1, -1), false))
            DrawContent();
    }

    // --- Identity strip ---

    /// <summary>
    /// The header bar: gradient wash, the obol mark, the wordmark, the live status pill, the account
    /// line and a shortcut to the status log. Mirrors Argus's identity strip.
    /// </summary>
    private void DrawHeader()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Layout.HeaderHeight * scale);
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + size;
        var dl = ImGui.GetWindowDrawList();

        var left = Vector4.Lerp(CharonTheme.CardBg, CharonTheme.Accent, 0.16f);
        var right = Vector4.Lerp(CharonTheme.CardBg, CharonTheme.AccentBlue, 0.06f);
        dl.AddRectFilledMultiColor(origin, end,
            ImGui.GetColorU32(left), ImGui.GetColorU32(right), ImGui.GetColorU32(right), ImGui.GetColorU32(left));
        dl.AddRect(origin, end, ImGui.GetColorU32(CharonTheme.WithAlpha(CharonTheme.AccentSoft, 0.30f)),
            CharonTheme.CardRounding * scale, ImDrawFlags.None, 1.2f * scale);

        DrawObol(origin + new Vector2(28f, 30f) * scale, scale, dl);

        var textX = origin.X + 56f * scale;
        ImGui.SetWindowFontScale(1.35f);
        dl.AddText(new Vector2(textX, origin.Y + 9f * scale), ImGui.GetColorU32(CharonTheme.TextStrong), "CHARON");
        var wordmarkWidth = ImGui.CalcTextSize("CHARON").X;
        ImGui.SetWindowFontScale(0.70f);
        dl.AddText(new Vector2(textX + wordmarkWidth + 10f * scale, origin.Y + 16f * scale),
            ImGui.GetColorU32(CharonTheme.AccentSoft), "FERRYMAN OF THE FLEET");
        dl.AddText(new Vector2(textX, origin.Y + 34f * scale),
            ImGui.GetColorU32(CharonTheme.TextDim), _accountStatus());
        ImGui.SetWindowFontScale(1f);

        var (pill, detail, color) = HeaderStatus();
        var pillSize = Pill.Measure(pill);
        Pill.DrawAt(new Vector2(end.X - 15f * scale - pillSize.X, origin.Y + 8f * scale), pill, color);

        var button = 22f * scale;
        var buttonX = end.X - 15f * scale - button;
        ImGui.SetWindowFontScale(0.78f);
        var detailSize = ImGui.CalcTextSize(detail);
        dl.AddText(new Vector2(buttonX - 8f * scale - detailSize.X, origin.Y + 36f * scale),
            ImGui.GetColorU32(CharonTheme.TextDim), detail);
        ImGui.SetWindowFontScale(1f);

        ImGui.SetCursorScreenPos(new Vector2(buttonX, origin.Y + 35f * scale));
        if (Buttons.Icon(FontAwesomeIcon.Scroll, "##charon_statuslog", button, "Status log (System → Debug)"))
            _section = Section.Debug;

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }

    /// <summary>The mark: a minted obol — the coin Charon is paid — ringed by slow-orbiting eyes.</summary>
    private static void DrawObol(Vector2 center, float scale, ImDrawListPtr dl)
    {
        var phase = CharonTheme.Phase(3400.0) * MathF.PI * 2f;
        var orbit = 15f * scale;
        const int marks = 8;
        for (var i = 0; i < marks; i++)
        {
            var angle = phase + i * MathF.PI * 2f / marks;
            var p = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * orbit;
            var alpha = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin(angle * 2f + phase));
            dl.AddCircleFilled(p, 1.6f * scale, ImGui.GetColorU32(CharonTheme.WithAlpha(CharonTheme.AccentSoft, alpha)));
        }

        var breath = 0.85f + 0.15f * CharonTheme.Pulse(2600.0);
        dl.AddCircleFilled(center, 9f * scale, ImGui.GetColorU32(Vector4.Lerp(CharonTheme.CardBg, CharonTheme.Accent, 0.55f)));
        dl.AddCircle(center, 9f * scale, ImGui.GetColorU32(CharonTheme.AccentSoft), 28, 1.4f * scale);
        dl.AddCircleFilled(center, 3.6f * scale * breath, ImGui.GetColorU32(CharonTheme.AccentBlue));
        dl.AddCircleFilled(center + new Vector2(-1.3f, -1.3f) * scale, 1f * scale, ImGui.GetColorU32(CharonTheme.TextStrong));
    }

    /// <summary>
    /// What the pill says. Amber = chores waiting for this character, cyan = a long-running job is in
    /// flight, mint = nothing to do. Every number comes from a reader Charon already trusts.
    /// </summary>
    private (string Label, string Detail, Vector4 Color) HeaderStatus()
    {
        var busy = _gilSeller.Busy ? "gil trip" : _doman.Busy ? "doman" : null;
        var toons = _roster.GetLanPartyMembers().Count;
        var detail = _roster.IsAvailable
            ? $"relay up · {toons} toon{(toons == 1 ? string.Empty : "s")}"
            : "no Daedalus relay";

        if (busy != null)
            return ($"BUSY · {busy.ToUpperInvariant()}", detail, CharonTheme.AccentCyan);

        var pending = WeeklyChoresLeft();
        return pending > 0
            ? ($"{pending} WAITING", detail, CharonTheme.AccentAmber)
            : ("ALL QUIET", detail, CharonTheme.AccentMint);
    }

    /// <summary>
    /// Weekly chores left before the reset: custom deliveries not yet spent plus an unspent Doman
    /// budget. Allied society allowances are dailies, so they stay out of the pill and live on the
    /// board's "needs you" card.
    /// </summary>
    private int WeeklyChoresLeft()
    {
        var snapshot = _weeklies.Read(DateTime.UtcNow);
        var count = snapshot.DeliveriesLoaded ? Math.Max(0, 6 - snapshot.DeliveriesUsed) : 0;
        if (_doman.DonationAvailable)
            count++;
        return count;
    }

    /// <summary>"(watching 3 toons)" out of the Quick Kill status — 0 when the line carries no count.</summary>
    private int QuickKillWatchCount()
    {
        var status = _quickKill.Status;
        var marker = status.IndexOf("watching ", StringComparison.Ordinal);
        if (marker < 0)
            return 0;

        var digits = status[(marker + "watching ".Length)..];
        var space = digits.IndexOf(' ');
        if (space > 0)
            digits = digits[..space];
        return int.TryParse(digits, out var count) ? count : 0;
    }

    // --- Sidebar ---

    private void DrawSidebar()
    {
        Styling.VSpace(2f);

        if (SidebarTab.Draw("Board", FontAwesomeIcon.ThLarge, _section == Section.Board))
            _section = Section.Board;

        DrawCategoryHeader("Features");
        if (SidebarTab.Draw("General", FontAwesomeIcon.SlidersH, _section == Section.General))
            _section = Section.General;
        if (SidebarTab.Draw("Auto Pillion", FontAwesomeIcon.Users, _section == Section.AutoPillion))
            _section = Section.AutoPillion;
        var watchNames = _config.SpawnWatchNames.Count;
        if (SidebarTab.Draw("Spawns", FontAwesomeIcon.Search, _section == Section.Spawns,
                watchNames > 0 ? watchNames.ToString() : null))
            _section = Section.Spawns;

        DrawCategoryHeader("Power level");
        if (SidebarTab.Draw("Heal Watch", FontAwesomeIcon.Heart, _section == Section.HealWatch,
                _healStatus().StartsWith("idle", StringComparison.OrdinalIgnoreCase) ? null : "on",
                CharonTheme.AccentMint))
            _section = Section.HealWatch;
        var watching = QuickKillWatchCount();
        if (SidebarTab.Draw("Quick Kill", FontAwesomeIcon.Crosshairs, _section == Section.QuickKill,
                watching > 0 ? watching.ToString() : null))
            _section = Section.QuickKill;

        DrawCategoryHeader("Fleet");
        if (SidebarTab.Draw("Group Mgmt", FontAwesomeIcon.UserPlus, _section == Section.GroupMgmt))
            _section = Section.GroupMgmt;
        if (SidebarTab.Draw("Fleet Leader", FontAwesomeIcon.Crown, _section == Section.FleetLeader))
            _section = Section.FleetLeader;
        if (SidebarTab.Draw("Follow", FontAwesomeIcon.Route, _section == Section.Follow,
                _followManager.Following ? "live" : null, CharonTheme.AccentCyan))
            _section = Section.Follow;
        if (SidebarTab.Draw("FC Chest", FontAwesomeIcon.BoxOpen, _section == Section.FcChest))
            _section = Section.FcChest;
        if (SidebarTab.Draw("Gear", FontAwesomeIcon.Tshirt, _section == Section.Gear))
            _section = Section.Gear;
        if (SidebarTab.Draw("Collect", FontAwesomeIcon.Gem, _section == Section.Collect))
            _section = Section.Collect;
        if (SidebarTab.Draw("Loot", FontAwesomeIcon.Dice, _section == Section.Loot))
            _section = Section.Loot;
        if (SidebarTab.Draw("Trusted List", FontAwesomeIcon.UserShield, _section == Section.TrustedList))
            _section = Section.TrustedList;

        DrawCategoryHeader("Gil");
        if (SidebarTab.Draw("FT Gil Capping", FontAwesomeIcon.Coins, _section == Section.GilCapping,
                _gilSeller.Busy ? "busy" : null))
            _section = Section.GilCapping;
        // The badge is retainers sitting READY at the bell: the one number worth seeing from here.
        var readyRetainers = ReadyRetainerCount();
        if (SidebarTab.Draw("Retainers", FontAwesomeIcon.Bell, _section == Section.Retainers,
                readyRetainers > 0 ? readyRetainers.ToString() : null, CharonTheme.AccentMint))
            _section = Section.Retainers;

        DrawCategoryHeader("Grand Company");
        var gcReady = _gcDailies.Plans(_gcDailies.Read()).Count(p => p.Ready);
        if (SidebarTab.Draw("Dailies", FontAwesomeIcon.ClipboardCheck, _section == Section.GcDailies,
                gcReady > 0 ? gcReady.ToString() : null, CharonTheme.AccentMint))
            _section = Section.GcDailies;

        DrawCategoryHeader("Weeklies");
        // A count badge = something is still left to do before a reset — a glance says "go spend it".
        var chores = WeeklyChoresLeft();
        if (SidebarTab.Draw("Weeklies", FontAwesomeIcon.CalendarCheck, _section == Section.Weeklies,
                chores > 0 ? chores.ToString() : null, CharonTheme.AccentMint))
            _section = Section.Weeklies;
        if (SidebarTab.Draw("Doman Donate", FontAwesomeIcon.Landmark, _section == Section.DomanDonate,
                _doman.Busy ? "busy" : null))
            _section = Section.DomanDonate;

        DrawCategoryHeader("Tweaks");
        if (SidebarTab.Draw("Tweaks", FontAwesomeIcon.Magic, _section == Section.Tweaks))
            _section = Section.Tweaks;

        DrawCategoryHeader("Dungeon");
        if (SidebarTab.Draw("Deep Dungeon", FontAwesomeIcon.Map, _section == Section.DeepDungeon))
            _section = Section.DeepDungeon;

        DrawCategoryHeader("System");
        if (SidebarTab.Draw("Debug", FontAwesomeIcon.Bug, _section == Section.Debug))
            _section = Section.Debug;
    }

    private static void DrawCategoryHeader(string label)
        => Styling.SectionLabel(label);

    // --- Content ---

    private void DrawContent()
    {
        switch (_section)
        {
            case Section.Board: DrawBoardSection(); break;
            case Section.General: DrawGeneralSection(); break;
            case Section.AutoPillion: DrawAutoPillionSection(); break;
            case Section.HealWatch: DrawHealWatchSection(); break;
            case Section.QuickKill: DrawQuickKillSection(); break;
            case Section.Spawns: DrawSpawnsSection(); break;
            case Section.GroupMgmt: DrawGroupSection(); break;
            case Section.FleetLeader: DrawFleetLeaderSection(); break;
            case Section.Follow: DrawFollowSection(); break;
            case Section.FcChest: DrawFcChestSection(); break;
            case Section.Gear: DrawGearSection(); break;
            case Section.Collect: DrawCollectSection(); break;
            case Section.Loot: DrawLootSection(); break;
            case Section.TrustedList: DrawTrustedSection(); break;
            case Section.GilCapping: DrawGilCappingSection(); break;
            case Section.Retainers: DrawRetainersSection(); break;
            case Section.GcDailies: DrawGcDailiesSection(); break;
            case Section.Weeklies: DrawWeekliesSection(); break;
            case Section.DomanDonate: DrawDomanSection(); break;
            case Section.Tweaks: DrawTweaksSection(); break;
            case Section.DeepDungeon: DrawDeepDungeonSection(); break;
            case Section.Debug: DrawDebugSection(); break;
        }
    }

    /// <summary>
    /// Page header: title, optional muted subtitle, hairline. Every section keeps calling this with a
    /// title alone until it gets a subtitle of its own (the Argus convention).
    /// </summary>
    private static void DrawPageHeader(string title, string? subtitle = null)
    {
        Styling.TextScaled(title, CharonTheme.TextStrong, 1.30f);
        if (!string.IsNullOrEmpty(subtitle))
            Styling.TextWrapped(subtitle, CharonTheme.TextMuted);
        ImGui.Spacing();
        var width = ImGui.GetContentRegionAvail().X;
        var y = ImGui.GetCursorScreenPos().Y;
        ImGui.GetWindowDrawList().AddLine(ImGui.GetCursorScreenPos(), new Vector2(ImGui.GetCursorScreenPos().X + width, y),
            ImGui.GetColorU32(CharonTheme.Hairline), 1f);
        ImGui.Spacing();
    }

    // --- Fleet board (landing page) ---

    /// <summary>
    /// The landing page. Stat tiles across the top, then every background feature's status line with a
    /// state dot and its own reason for being idle, next to this character's switches, the LAN fleet as
    /// a table and a "needs you" card. The live rows are the same strings the Debug page shows — they
    /// are promoted to the front page because a feature whose state is invisible costs a test cycle to
    /// diagnose.
    /// </summary>
    private void DrawBoardSection()
    {
        var scale = ImGuiHelpers.GlobalScale;
        DrawPageHeader("Fleet board",
            "Dot green = enabled, amber = working right now, grey = off. Each line carries its own reason for being idle.");

        DrawBoardTiles();
        Styling.VSpace(3f);

        var gap = 5f * scale;
        var available = ImGui.GetContentRegionAvail().X;
        var rightWidth = MathF.Min(340f * scale, available * 0.42f);
        var leftWidth = MathF.Max(240f * scale, available - rightWidth - gap);

        using (ImRaii.Child("##charon_board_left", new Vector2(leftWidth, -1), false))
        {
            DrawFleetCard();
            Styling.VSpace(4f);
            DrawLiveCard();
        }

        ImGui.SameLine(0, gap);

        using (ImRaii.Child("##charon_board_right", new Vector2(rightWidth, -1), false))
        {
            DrawSwitchesCard();
            Styling.VSpace(6f);
            DrawActionsCard();
            Styling.VSpace(6f);
            DrawNeedsYouCard();
        }
    }

    private void DrawBoardTiles()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var gap = 5f * scale;
        var width = (ImGui.GetContentRegionAvail().X - gap * 5f) / 6f;

        var leader = _followManager.LeaderName.Length > 0 ? _followManager.LeaderName : "Idle";
        StatTile.Draw("Follow", FitText(leader, width - 18f * scale), Shorten(_followStatus()),
            CharonTheme.AccentCyan, width,
            "Fleet follow: who this box follows, and what the nav layer is doing about it.");
        ImGui.SameLine(0, gap);

        var setting = _config.QuickKillFor(_localContentId());
        var role = !setting.Enabled ? "Off" : setting.Mode == 1 ? "Tag" : "Kill";
        var watching = QuickKillWatchCount();
        StatTile.Draw("Quick kill", role, watching > 0 ? $"watching {watching}" : Shorten(_quickKill.Status),
            CharonTheme.Accent, width,
            "Per character. Kill aims your rotation for the carry; Tag gives each engaged mob one ranged hit.");
        ImGui.SameLine(0, gap);

        StatTile.Draw("Heal watch", _config.HealWatchEnabled ? "On" : "Off", Shorten(_healStatus()),
            CharonTheme.AccentMint, width,
            "Tops up fleet toons from the LAN roster's vitals, out-of-party ones included.");
        ImGui.SameLine(0, gap);

        var provider = _config.NavProvider == 1 ? "Ariadne" : "vnavmesh";
        StatTile.Draw("Movement", provider,
            _navStatus().Contains(": ready", StringComparison.Ordinal) ? "ready" : "not ready",
            CharonTheme.AccentBlue, width, "Which navigation plugin drives every Charon movement.");
        ImGui.SameLine(0, gap);

        var chores = WeeklyChoresLeft();
        StatTile.Draw("Weeklies", chores > 0 ? $"{chores} left" : "done", Shorten(_weeklies.Status),
            CharonTheme.AccentAmber, width,
            "Custom deliveries and the Doman donation left before the Tuesday reset.");
        ImGui.SameLine(0, gap);

        StatTile.Draw("Retainers", Shorten(_retainers.Status), Shorten(_ventureRunner.Status),
            CharonTheme.AccentViolet, width,
            "Read-only until the venture assist is armed at a bell.");
    }

    private void DrawLiveCard()
    {
        var contentId = _localContentId();
        Styling.SectionLabel("Live");
        using var card = Panel.Begin();

        LiveRow("Follow", _followStatus(), _followManager.Following);
        LiveRow("Fleet follow", _followFleetStatus(), _followManager.Following);
        LiveRow("Boarding", _boardingStatus(), _config.AutoPillionEnabled);
        LiveRow("Heal watch", _healStatus(), _config.HealWatchEnabled);
        LiveRow("Quick kill", _quickKill.Status, _config.QuickKillFor(contentId).Enabled);
        LiveRow("Sprint", _sprintStatus(), _config.AutoSprintEnabled);
        LiveRow("Gear", _gearStatus(), _config.GearIpcExecuteEnabled);
        LiveRow("Loot", _lootStatus(), _config.LootRollEnabled);
        LiveRow("Collect", _collectStatus(), _config.AutoCollectEnabled);
        LiveRow("Nav", _navStatus(), true);
        LiveRow("Gil capping", _gilSeller.Status, _gilSeller.Busy, _gilSeller.Busy);
        LiveRow("Doman", _doman.Status, _doman.DonationAvailable);
        LiveRow("Weeklies", _weeklies.Status, WeeklyChoresLeft() > 0);
        LiveRow("Retainers", $"{_retainers.Status} · {_ventureRunner.Status}",
            _retainers.Loaded && VentureBoard.AnythingToDo(VentureBoard.Compose(DateTime.UtcNow, _retainers.Read(DateTime.UtcNow))));
        LiveRow("Spawns", _spawnScanner.Status, _config.SpawnTrackerEnabled && _config.SpawnWatchNames.Count > 0);
        LiveRow("Duty pop", _dutyPopStatus(), _config.AutoCommenceDutyEnabled);
        LiveRow("Trade", _tradeStatus(), _config.AutoTradeEnabled);
        LiveRow("Duty exit", _dutyExitStatus(), _config.FleetLeaderName.Length > 0);
        LiveRow("Revival", _revivalStatus(), _config.AutoAcceptRevival);
        LiveRow("Tweaks", _qolStatus(), _config.AutoOpenChestsEnabled || _config.AutoQteEnabled
            || _config.AutoCommendEnabled || _config.AutoTurnInEnabled);
        LiveRow("Leveling", _levelingStatus(), _config.LevelingIpcEnabled);
    }

    /// <summary>
    /// One feature line: state dot, name, then the status string fitted to the remaining width with the
    /// full text on hover. Same content as the Debug page, laid out to be scanned rather than read.
    /// </summary>
    private void LiveRow(string name, string status, bool on, bool busy = false)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var color = busy ? CharonTheme.AccentAmber : on ? CharonTheme.AccentMint : CharonTheme.TextMuted;

        ImGui.TextColored(color, "●");
        ImGui.SameLine(0, 6f * scale);
        ImGui.TextColored(CharonTheme.TextStrong, name);

        var cursorX = ImGui.GetCursorPosX();
        var columnX = 118f * scale;
        var room = ImGui.GetContentRegionAvail().X - (columnX - cursorX);
        var fitted = FitText(status, MathF.Max(40f, room));

        ImGui.SameLine(columnX);
        ImGui.TextColored(CharonTheme.TextDim, fitted);
        if (fitted.Length != status.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(status);
    }

    private void DrawFleetCard()
    {
        Styling.SectionLabel("Fleet");
        using var card = Panel.Begin();

        if (!_roster.IsAvailable)
        {
            Styling.TextWrapped("Daedalus is not answering, so the LAN roster — and with it every other "
                                + "box — is unknown. Manual whitelist entries still work.", CharonTheme.TextMuted);
            return;
        }

        var members = _roster.GetLanPartyMembers();
        if (members.Count == 0)
        {
            Styling.TextWrapped("The LAN roster is empty: no other toon is reporting in.", CharonTheme.TextMuted);
            return;
        }

        using var table = ImRaii.Table("##charon_fleet_table", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Toon", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Follow", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Vitals", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Relay", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableHeadersRow();

        foreach (var toon in members)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            Styling.Text(Display(toon.CharacterName), CharonTheme.TextStrong);

            // Follow state is local to each box and broadcast on charon.follow; "—" means that box has
            // not reported a leader (it may not be running Charon at all).
            ImGui.TableNextColumn();
            var reported = _reportedFollowLeader(toon.CharacterName);
            Styling.Text(reported != null ? $"follows {Display(reported)}" : "—", CharonTheme.TextDim);

            ImGui.TableNextColumn();
            Styling.Text(toon.Hp > 0f ? $"{toon.Hp * 100f:0}%" : "—",
                toon.Hp is > 0f and < 0.35f ? CharonTheme.AccentRose : CharonTheme.TextDim);

            ImGui.TableNextColumn();
            Styling.Text(toon.IsOnline ? "online" : "offline",
                toon.IsOnline ? CharonTheme.AccentMint : CharonTheme.TextMuted);
        }
    }

    private void DrawSwitchesCard()
    {
        Styling.SectionLabel("This character");
        using var group = SettingsGroup.Begin(string.Empty);

        // Quick Kill is per character (the config file is shared by every client on a machine), so its
        // role is a three-way picker rather than a checkbox.
        var setting = _config.QuickKillFor(_localContentId());
        group.Row("Quick kill", "Kill: this toon is the carry — aim its rotation at whatever is fighting "
                                + "the fleet, plus one ranged opener while out of combat. Tag: this toon is being "
                                + "carried — one ranged hit per engaged mob. Stored per character.",
            126f, () =>
            {
                var current = !setting.Enabled ? 0 : setting.Mode == 1 ? 2 : 1;
                var picked = Segmented.Draw(new[] { "Off", "Kill", "Tag" }, current);
                if (picked == current)
                    return;

                setting.Enabled = picked != 0;
                setting.Mode = picked == 2 ? 1 : 0;
                _save();
            }, 20f);

        var heal = _config.HealWatchEnabled;
        if (group.Toggle("Heal watch", "Emergency heal, raise and HoT upkeep for fleet toons, from the LAN "
                                       + "roster's vitals. Stands down while Daedalus is rotating.", ref heal))
        {
            _config.HealWatchEnabled = heal;
            _save();
        }

        var sprint = _config.AutoSprintEnabled;
        if (group.Toggle("Sprint out of combat", "Sprint when moving and not in combat. Never in combat: "
                                                 + "the rotation owns the action queue there.", ref sprint))
        {
            _config.AutoSprintEnabled = sprint;
            _save();
        }

        var collect = _config.AutoCollectEnabled;
        if (group.Toggle("Auto-collect", "Consume bag collectibles the game says are unlearned — never "
                                         + "fashion accessories or barding, which are worth gil unlearned.", ref collect))
        {
            _config.AutoCollectEnabled = collect;
            _save();
        }

        var advance = _config.TextAdvanceEnabled;
        if (group.Toggle("Advance dialogue", "Click the talk box for you. On a hand-played box this eats the "
                                             + "story; Odysseus can borrow it with a timed lease instead.", ref advance))
        {
            _config.TextAdvanceEnabled = advance;
            _save();
        }

        var teleport = _config.FollowTeleportEnabled;
        if (group.Toggle("Follow teleport", "Accept a trusted leader's teleport offer, or follow them to a new "
                                            + "zone through an attuned aetheryte.", ref teleport))
        {
            _config.FollowTeleportEnabled = teleport;
            _save();
        }
    }

    private void DrawActionsCard()
    {
        Styling.SectionLabel("Actions");
        using var card = Panel.Begin();

        var scale = ImGuiHelpers.GlobalScale;
        var gap = 5f * scale;
        var half = (ImGui.GetContentRegionAvail().X - gap) * 0.5f;

        // The leader to command: whatever we are following now, else the persisted one.
        var leader = _followManager.LeaderName.Length > 0 ? _followManager.LeaderName : _config.FollowLeaderName;
        if (Buttons.Action(leader.Length > 0 ? $"Follow {Display(leader)}" : "Follow (no leader)", leader.Length > 0, half))
            _followCommands.Follow(leader);
        ImGui.SameLine(0, gap);
        if (Buttons.Action("Stop", _followManager.Following, half))
            _followCommands.Stop(leader);
        Styling.VSpace(3f);

        if (Buttons.Action("Follow all", true, half))
            _followCommands.FollowAll();
        ImGui.SameLine(0, gap);
        if (Buttons.Action("Stop all", true, half))
            _followCommands.StopAll();
        Styling.VSpace(3f);

        if (Buttons.Action("Leave duty (fleet)", _config.FleetLeaderName.Length > 0, half,
                CharonTheme.AccentAmber))
            _fleetCommands.LeaveDuty();
        ImGui.SameLine(0, gap);
        if (Buttons.Action("Status log", true, half, CharonTheme.AccentBlue))
            _section = Section.Debug;
    }

    private void DrawNeedsYouCard()
    {
        Styling.SectionLabel("Needs you");
        using var card = Panel.Begin();

        var weekly = _weeklies.Read(DateTime.UtcNow);
        var enclave = _doman.ReadEnclaveStateOrCache(DateTime.UtcNow);

        var deliveriesLeft = weekly.DeliveriesLoaded ? Math.Max(0, 6 - weekly.DeliveriesUsed) : -1;
        NeedsYouRow(card, "Custom deliveries",
            deliveriesLeft < 0 ? "not loaded" : deliveriesLeft == 0 ? "done" : $"{deliveriesLeft} left",
            deliveriesLeft > 0);

        var tribesLeft = weekly.TribesLoaded ? weekly.TribeAllowanceLeft : -1;
        NeedsYouRow(card, "Allied societies",
            tribesLeft < 0 ? "not loaded" : tribesLeft == 0 ? "done today" : $"{tribesLeft} left today",
            tribesLeft > 0);

        var domanAvailable = _doman.DonationAvailable;
        NeedsYouRow(card, "Doman donation",
            !domanAvailable ? "done this week"
                : enclave.Loaded && enclave.BudgetRemaining > 0 ? $"{enclave.BudgetRemaining:N0} gil of budget"
                : "available",
            domanAvailable);

        NeedsYouRow(card, "Spawns watched", _config.SpawnWatchNames.Count.ToString(), false);
    }

    /// <summary>
    /// One "needs you" line: label on the left, value hard right-aligned to the card's own right edge.
    /// The value is fitted (with a hover tooltip for the full text) so a long figure can never push
    /// itself under the card border.
    /// </summary>
    private static void NeedsYouRow(Panel panel, string label, string value, bool waiting)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.TextColored(waiting ? CharonTheme.TextStrong : CharonTheme.TextDim, label);
        var rowY = ImGui.GetItemRectMin().Y;
        var labelEnd = ImGui.GetItemRectMax().X;

        var fitted = FitText(value, panel.RightEdge - labelEnd - 8f * scale);
        ImGui.SetCursorScreenPos(new Vector2(panel.RightEdge - ImGui.CalcTextSize(fitted).X, rowY));
        ImGui.TextColored(waiting ? CharonTheme.AccentAmber : CharonTheme.TextDim, fitted);
        if (fitted.Length != value.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(value);
    }

    /// <summary>First clause of a status line, for a tile subtitle: everything before " · " or " — ".</summary>
    private static string Shorten(string status)
    {
        var cut = status.Length;
        foreach (var separator in new[] { " · ", " — " })
        {
            var at = status.IndexOf(separator, StringComparison.Ordinal);
            if (at >= 0 && at < cut)
                cut = at;
        }

        return status[..cut];
    }

    /// <summary>Trim to fit a width, with an ellipsis when anything was dropped (full text goes in a tooltip).</summary>
    private static string FitText(string text, float maxWidth) => Styling.FitText(text, maxWidth);

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

        ImGui.TextColored(CharonTheme.TextSecondary, "Movement");
        var provider = _config.NavProvider == 1 ? 1 : 0;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.Combo("Provider##nav", ref provider, "vnavmesh\0Ariadne\0"))
        {
            _config.NavProvider = provider;
            _save();
        }
        CharonTheme.HelpMarker("Which navigation plugin drives all Charon movement — fleet follow,\n"
                               + "boarding walks, vendor trips. Everything routes through one client,\n"
                               + "so the switch covers every feature at once. Takes effect instantly.");
        ImGui.TextColored(CharonTheme.TextDisabled, _navStatus());

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

        var notifyFull = _config.PillionFullNotify;
        if (ImGui.Checkbox("Notify when the mount is full##pillion", ref notifyFull))
        {
            _config.PillionFullNotify = notifyFull;
            _save();
        }
        CharonTheme.HelpMarker("Pop a notification once every passenger seat is taken, so you know "
                               + "the fleet is aboard without counting riders. Shows on the mount "
                               + "owner's screen only, once per mount-up.");

        var ridersWindow = _config.PillionRidersWindowEnabled;
        if (ImGui.Checkbox("Riders window while driving##pillion", ref ridersWindow))
        {
            _config.PillionRidersWindowEnabled = ridersWindow;
            _save();
        }
        CharonTheme.HelpMarker("A small window listing every seat and who is in it, shown while\n"
                               + "you drive a multi-seat mount. Closes itself on dismount.");

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
        ImGui.PushStyleColor(ImGuiCol.Button, CharonTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CharonTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, CharonTheme.AccentSoft);
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

        var autoSprint = _config.AutoSprintEnabled;
        if (ImGui.Checkbox("Sprint out of combat##follow", ref autoSprint))
        {
            _config.AutoSprintEnabled = autoSprint;
            _save();
        }
        CharonTheme.HelpMarker("Sprint whenever this toon is moving and out of combat, so followers "
                               + "keep up instead of walking. Never in combat (the rotation owns the "
                               + "action queue there) and never while mounted.");

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
        ImGui.PushStyleColor(ImGuiCol.Button, CharonTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CharonTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, CharonTheme.AccentSoft);
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
        else if (ImGui.BeginTable("followparty", 4,
                     ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##dot", ImGuiTableColumnFlags.WidthFixed, 16f);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 160f);
            ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("Following", ImGuiTableColumnFlags.WidthStretch);

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

                // Who that toon is following, as reported by its own box. Null means it hasn't
                // reported recently — shown as unknown rather than guessed at, since a stale leader
                // is worse than admitting we don't know.
                ImGui.TableNextColumn();
                var following = _reportedFollowLeader(toon.CharacterName);
                if (following == null)
                    ImGui.TextColored(CharonTheme.TextDisabled, toon.IsOnline ? "?" : "");
                else if (following.Length == 0)
                    ImGui.TextColored(CharonTheme.TextDisabled, "—");
                else
                    ImGui.TextColored(CharonTheme.StatusGreen, $"→ {Display(following)}");
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
            ImGui.PushStyleColor(ImGuiCol.Button, CharonTheme.Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, CharonTheme.Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, CharonTheme.AccentSoft);
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
                    ImGui.TextColored(CharonTheme.Accent, "[EXP]");
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
                    ImGui.TextColored(CharonTheme.Accent, "[EXP]");
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
            ImGui.TextColored(CharonTheme.Accent, upgrade.Item.Name);
            ImGui.TableNextColumn();
            if (upgrade.IlvlGain > 0)
            {
                ImGui.TextColored(CharonTheme.StatusGreen, $"+{upgrade.IlvlGain}");
            }
            else
            {
                // Same item level, better stats for this job — the usual case at max level.
                ImGui.TextColored(CharonTheme.Accent, "stats");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Same item level, but a better stat spread for this job.");
            }
        }

        ImGui.EndTable();
    }

    // --- Loot (read-only preview) ---

    /// <summary>
    /// What Charon WOULD roll on the current loot window. Nothing is clicked — this exists to check
    /// item resolution and the rules against real drops before rolling is ever enabled.
    /// </summary>
    private void DrawLootSection()
    {
        DrawPageHeader("Loot");

        ImGui.TextColored(CharonTheme.StatusYellow, "Read-only: decisions are shown, nothing is rolled.");
        ImGui.Spacing();

        var watching = _config.LootRollEnabled;
        if (ImGui.Checkbox("Work out rolls for loot", ref watching))
        {
            _config.LootRollEnabled = watching;
            _save();
        }
        CharonTheme.HelpMarker("Evaluate the loot window and show what each item would get. "
                               + "Turning this off stops it thinking about loot entirely.");

        var gap = _config.LootPassBelowIlvlGap;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderInt("Pass below (item levels)##loot", ref gap, 0, 100))
        {
            _config.LootPassBelowIlvlGap = gap;
            _save();
        }
        CharonTheme.HelpMarker("Gear more than this many item levels below what this job wears is passed on.");

        ImGui.Spacing();
        var pending = _lootWatcher.Pending;
        if (pending.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "No loot pending.");
        }
        else if (ImGui.BeginTable("lootRows", 3,
                     ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Would", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthFixed, 220f);
            ImGui.TableHeadersRow();

            foreach (var row in pending)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Name);
                ImGui.TableNextColumn();
                ImGui.TextColored(RollColour(row.Action), row.Action.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(CharonTheme.TextSecondary, row.Reason);
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawStatusLine(_lootStatus(), CharonTheme.TextDisabled);
    }

    private static Vector4 RollColour(Charon.Features.Loot.RollAction action) => action switch
    {
        Charon.Features.Loot.RollAction.Need => CharonTheme.StatusGreen,
        Charon.Features.Loot.RollAction.Greed => CharonTheme.Accent,
        Charon.Features.Loot.RollAction.Pass => CharonTheme.TextDisabled,
        _ => CharonTheme.TextSecondary,
    };

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
                               + "won't relearn one, so anything worth selling stays untouched.\n\n"
                               + "The exception is Bozjan field records: the game offers no way "
                               + "to check whether one is already registered, so they are always "
                               + "listed, marked, and never auto-collected.");

        var auto = _config.AutoCollectEnabled;
        if (ImGui.Checkbox("Auto-collect", ref auto))
        {
            _config.AutoCollectEnabled = auto;
            _save();
        }
        CharonTheme.HelpMarker("Learns these on its own — out of combat, one every 1.5s.\n"
                               + "NEVER fashion accessories or chocobo barding: an unlearned one can\n"
                               + "be worth millions and collecting consumes it, so those two kinds\n"
                               + "always keep the manual button. Bozjan field records are skipped\n"
                               + "too — nothing can tell whether one is already registered.\n"
                               + "Anything the game refuses is skipped for the session (Refresh retries).");
        if (auto)
        {
            ImGui.SameLine();
            ImGui.TextColored(CharonTheme.TextDisabled, _collection.AutoStatus);
        }

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
                // No unlock check exists for this kind, so "unlearned" is an assumption, not a
                // fact. Saying so beats hiding the item AND beats implying a certainty we lack.
                var unverified = Charon.Features.Loot.CollectibleKinds.UnverifiedUnlock.Contains(row.ActionKind);
                if (unverified)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(CharonTheme.TextDisabled, "(registration unknown)");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("The game exposes no way to ask whether this record is already\n"
                                         + "registered, so Charon can't tell a new one from a duplicate.\n"
                                         + "Check the Field Records menu before using it.");
                }
                ImGui.TableNextColumn();
                ImGui.TextColored(CharonTheme.TextSecondary, row.Category);
                ImGui.TableNextColumn();
                var here = _collection.CanCollectHere(row);
                var manualOnly = Charon.Features.Loot.CollectibleKinds.ManualOnly.Contains(row.ActionKind);
                if (!here) ImGui.BeginDisabled();
                if (ImGui.SmallButton($"Collect##collect{row.Container}_{row.Slot}") && here)
                    _collection.TryCollect(row.ItemId, row.ActionKind, highQuality: false);
                if (!here) ImGui.EndDisabled();
                if (!here && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Only usable in the Occult Crescent (South Horn or North Horn)");
                else if (manualOnly && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Manual only — this kind can be worth real gil unlearned,\nand collecting consumes it. Auto-collect never touches it.");
                else if (unverified && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Manual only — Charon can't verify whether this one is already\nregistered, so auto-collect never touches it.");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.Button("Refresh"))
        {
            _collection.Invalidate();
            _collection.ResetAutoRefusals();
        }
        CharonTheme.HelpMarker("The list refreshes on its own every second — this is only for impatience.");

        ImGui.Spacing();
        DrawStatusLine(_collection.Status, CharonTheme.TextDisabled);
    }

    // --- Power level: Quick Kill ---

    /// <summary>
    /// Two roles on one per-box toggle: KILL for the carry (aim its rotation at whatever is fighting
    /// the fleet) and TAG for a toon being carried (one ranged hit per mob).
    /// </summary>
    private void DrawQuickKillSection()
    {
        DrawPageHeader("Quick Kill");

        // Per CHARACTER: every client on a PC shares one Charon.json, so a plain field would make
        // the Machinist carry and a hand-played toon on the same PC share one role.
        var contentId = _localContentId();
        var setting = _config.QuickKillFor(contentId);
        ImGui.TextColored(CharonTheme.TextSecondary, contentId == 0
            ? "Log in to set this character's role."
            : $"For {Display(_localName())} only — every character keeps its own setting.");

        var enabled = setting.Enabled;
        if (ImGui.Checkbox("Enabled##quickkill", ref enabled) && contentId != 0)
        {
            setting.Enabled = enabled;
            _save();
        }
        CharonTheme.HelpMarker("Only ever acts on mobs ALREADY fighting your party or a fleet toon,\n"
                               + "so it never pulls anything. Set per character: pick the role this toon\n"
                               + "plays below.");

        ImGui.Spacing();
        var mode = setting.Mode;
        if (ImGui.RadioButton("Kill — this toon is the carry##qkmode", mode == 0) && contentId != 0)
        {
            setting.Mode = 0;
            _save();
        }
        CharonTheme.HelpMarker("Targets whatever is fighting the fleet — nearest first, and it sticks\n"
                               + "with a mob until it dies — so this toon's own rotation (Daedalus,\n"
                               + "RSR...) kills it. Rotations only fire once their toon is in combat,\n"
                               + "so if this one isn't yet, Quick Kill opens with ONE ranged shot and\n"
                               + "the rotation does everything after that.\n\n"
                               + "It never goes after a mob nothing has engaged: hitting one first\n"
                               + "would take the claim, and the EXP, away from the toons you carry.\n"
                               + "Works across parties, so the carry can stay OUT of their group.\n"
                               + "Pair it with Follow so this toon stays in range of them.");

        if (ImGui.RadioButton("Tag — this toon is being carried##qkmode", mode == 1) && contentId != 0)
        {
            setting.Mode = 1;
            _save();
        }
        CharonTheme.HelpMarker("Every mob fighting the party or the fleet gets ONE ranged hit from\n"
                               + "this toon so it joins the kill, and is then left alone. The toon\n"
                               + "never walks toward a mob and never keeps attacking.\n\n"
                               + "Beastmasters tag with Capture (10y): the beast is also marked, and\n"
                               + "if it dies while marked the Beastmaster forges a pact with it.\n\n"
                               + "A mob counts as tagged once it is on this toon's enmity list. Stands\n"
                               + "down while the Daedalus rotation is enabled (it is already attacking).");

        ImGui.Spacing();
        if (mode == 1)
            DrawStatusLine($"Tag action: {_quickKill.TagDescription}", CharonTheme.TextSecondary);
        DrawStatusLine(_quickKill.Status, CharonTheme.TextDisabled);
    }

    // --- Spawns ---

    /// <summary>The watchlist editor; the sightings themselves live in the spawn log window.</summary>
    private void DrawSpawnsSection()
    {
        DrawPageHeader("Spawns");

        var enabled = _config.SpawnTrackerEnabled;
        if (ImGui.Checkbox("Watch for mobs by name##spawn", ref enabled))
        {
            _config.SpawnTrackerEnabled = enabled;
            _save();
        }
        CharonTheme.HelpMarker("Logs a watched mob the first time it turns up near you, with the\n"
                               + "time and how far away it was. Read-only — nothing is targeted or\n"
                               + "attacked.\n\n"
                               + "A client cannot tell a fresh spawn from a mob that simply came\n"
                               + "into render range: both arrive the same way. Each mob is logged\n"
                               + "once per zone visit, so walking past one twice logs it once.");

        var autoOpen = _config.SpawnWindowAutoOpen;
        if (ImGui.Checkbox("Pop the log open on a sighting##spawn", ref autoOpen))
        {
            _config.SpawnWindowAutoOpen = autoOpen;
            _save();
        }

        if (ImGui.Button("Open spawn log"))
        {
            _config.SpawnWindowVisible = true;
            _save();
        }
        ImGui.SameLine();
        ImGui.TextColored(CharonTheme.TextDisabled, $"{_spawnScanner.Watcher.History.Count} logged");

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextSecondary, "Watchlist");
        ImGui.TextColored(CharonTheme.TextDisabled, "Matched anywhere in the name, ignoring case.");

        ImGui.SetNextItemWidth(190f);
        var submitted = ImGui.InputTextWithHint("##spawnname", "Mob name (or part of one)", ref _spawnName, 48,
            ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add##spawn") || submitted) && _spawnName.Trim().Length > 0)
        {
            AddWatchName(_spawnName);
            _spawnName = string.Empty;
        }

        if (_config.SpawnWatchNames.Count == 0)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "Nothing watched yet.");
        }
        else
        {
            var remove = -1;
            for (var i = 0; i < _config.SpawnWatchNames.Count; i++)
            {
                if (ImGui.SmallButton($"Remove##spawnrm{i}"))
                    remove = i;
                ImGui.SameLine();
                ImGui.TextUnformatted(_config.SpawnWatchNames[i]);
            }

            if (remove >= 0)
            {
                _config.SpawnWatchNames.RemoveAt(remove);
                _save();
            }
        }

        ImGui.Spacing();
        if (ImGui.TreeNode("Add from nearby"))
        {
            var nearby = _spawnScanner.NearbyNames();
            if (nearby.Count == 0)
            {
                ImGui.TextColored(CharonTheme.TextDisabled, "No mobs in range.");
            }
            else
            {
                foreach (var name in nearby)
                {
                    var watched = Charon.Features.Spawns.SpawnWatcher.IsWatched(name, _config.SpawnWatchNames);
                    if (watched)
                    {
                        ImGui.TextColored(CharonTheme.StatusGreen, "watched");
                    }
                    else if (ImGui.SmallButton($"Watch##add{name}"))
                    {
                        AddWatchName(name);
                    }

                    ImGui.SameLine();
                    ImGui.TextUnformatted(Display(name));
                }
            }

            ImGui.TreePop();
        }

        ImGui.Spacing();
        DrawStatusLine(_spawnScanner.Status, CharonTheme.TextDisabled);
    }

    private void AddWatchName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0
            || _config.SpawnWatchNames.Exists(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase)))
            return;

        _config.SpawnWatchNames.Add(trimmed);
        _save();
    }

    // --- WEEKLIES ---

    /// <summary>
    /// The board's raw inputs, gathered from live game state. The Doman "loaded" flag is widened
    /// by Charon's own donation record: even when the enclave manager can't be read, a donation
    /// this box performed itself is a definite answer, not an unknown.
    /// </summary>
    private IReadOnlyList<WeekliesBoard.Item> ComposeWeekliesBoard()
    {
        var now = DateTime.UtcNow;
        var snap = _weeklies.Read(now);
        var enclave = _doman.ReadEnclaveStateOrCache(now);
        var domanDone = _doman.DonatedThisWeek;
        return WeekliesBoard.Compose(now,
            enclave.Loaded || domanDone, domanDone, enclave.BudgetRemaining, enclave.FromCache,
            snap.DeliveriesLoaded, snap.DeliveriesUsed,
            snap.TribesLoaded, snap.TribeAllowanceLeft);
    }

    private bool AnyWeeklyPending()
    {
        foreach (var item in ComposeWeekliesBoard())
        {
            if (item.State == WeekliesBoard.ItemState.Pending)
                return true;
        }

        return false;
    }

    /// <summary>What this character can still spend before the next reset, at a glance.</summary>
    private void DrawWeekliesSection()
    {
        DrawPageHeader("Weeklies");

        DrawStatusLine("Weekly and daily allowances this character has not used yet. Read from the\n"
                       + "game's own state — the same sources as the Timers window.");
        ImGui.Spacing();

        if (ImGui.BeginTable("##weeklies", 3, ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Task", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Resets", ImGuiTableColumnFlags.WidthFixed, 130f);

            foreach (var item in ComposeWeekliesBoard())
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var (dot, color) = item.State switch
                {
                    WeekliesBoard.ItemState.Done => ("done", CharonTheme.StatusGreen),
                    WeekliesBoard.ItemState.Pending => ("to do", CharonTheme.StatusYellow),
                    _ => ("?", CharonTheme.StatusGrey),
                };
                ImGui.TextColored(color, dot);
                ImGui.SameLine();
                ImGui.TextUnformatted(item.Name);
                ImGui.TableNextColumn();
                ImGui.TextColored(CharonTheme.TextSecondary, item.Detail);
                ImGui.TableNextColumn();
                ImGui.TextColored(CharonTheme.TextDisabled, item.Reset);
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawStatusLine("Doman donations run from the Doman Donate section below; Custom Deliveries\n"
                       + "and Allied Society dailies run through Odysseus.", CharonTheme.TextDisabled);
    }

    // --- GIL: FT Gil Capping ---

    /// <summary>
    /// One button for the whole errand: split the exact stack, walk to the nearest gil vendor
    /// via vnavmesh, open the shop and sell to meet-or-exceed the 300k free-trial cap.
    /// </summary>
    private void DrawGilCappingSection()
    {
        DrawPageHeader("FT Gil Capping");

        var freeTrial = _isFreeTrial();
        if (!freeTrial)
        {
            ImGui.TextColored(CharonTheme.StatusYellow,
                "This account is not a free trial — no 300k cap applies here.");
            ImGui.Spacing();
        }

        var gil = GilCapSeller.CurrentGil();
        var headroom = GilCapSeller.FreeTrialGilCap - gil;
        var (itemName, price) = _gilSeller.ItemInfo(_config.GilItemId);
        var held = GilCapSeller.CountInBags(_config.GilItemId);
        var wanted = StackSplitCalculator.QuantityToReach(headroom, price, held);

        DrawStatusLine($"Gil: {gil:N0} / {GilCapSeller.FreeTrialGilCap:N0}"
                       + (headroom > 0 ? $" — {headroom:N0} short" : " — at the cap"));
        DrawStatusLine($"{itemName}: {held:N0} in bags · sells for {price:N0} each"
                       + (wanted > 0 ? $" · would sell {wanted}" : ""));
        ImGui.Spacing();

        if (_gilSeller.Busy)
        {
            if (ImGui.Button("Stop", new Vector2(120, 26)))
                _gilSeller.Cancel();
        }
        else
        {
            var canRun = freeTrial && headroom > 0 && held > 0;
            if (!canRun) ImGui.BeginDisabled();
            if (ImGui.Button($"Sell {itemName} to cap##gilsell", new Vector2(220, 26)))
                _gilSeller.RequestTrip(_config.GilItemId);
            if (!canRun) ImGui.EndDisabled();
            CharonTheme.HelpMarker("Splits the exact quantity, walks to the nearest gil vendor\n"
                                   + "(vnavmesh), opens the shop and sells — one duckbone over the\n"
                                   + "cap rather than one short. The game just prints a chat line\n"
                                   + "when you pass the cap; nothing needs dismissing.");
        }

        ImGui.Spacing();
        DrawStatusLine(_gilSeller.Status, CharonTheme.TextDisabled);
    }

    // --- GIL: Doman Donate ---

    /// <summary>
    /// The Doman donation page. The content is <see cref="DomanView"/>, shared with the pop-up window that
    /// rides the donation basket, so the section and the window can never disagree about the flow.
    /// </summary>
    private void DrawDomanSection()
    {
        DrawPageHeader("Doman Donate");

        var popUp = _config.DomanWindowEnabled;
        if (ImGui.Checkbox("Pop up with the donation basket##doman", ref popUp))
        {
            _config.DomanWindowEnabled = popUp;
            _save();
        }
        CharonTheme.HelpMarker("A window with these two steps, opening when you stand at the Doman\n"
                               + "Enclave donation basket. It also stays up while a step is running or\n"
                               + "a split stack is waiting, since Prepare has to close the basket.\n"
                               + "Closing it by hand keeps it closed for that basket.");

        ImGui.Spacing();
        DomanView.DrawBody(_config, _save, _doman, _gilSeller);
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
            ImGui.TextColored(CharonTheme.Accent, "[LAN]");
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

    /// <summary>Game-wide augmentations — the Pandora-port QoL toggles.</summary>

    /// <summary>
    /// The retainer settings manager: every plan in one place, so a retainer can be set up without being at
    /// a bell. The board is the working surface (actions, the ranked venture list); this is where the
    /// decisions behind those actions are made — and where a retainer can be set up long before it is out.
    /// </summary>
    private void DrawRetainersSection()
    {
        DrawPageHeader("Retainers", "who runs what — modes, the farm list, and the two surfaces");

        var rows = _retainers.Read(DateTime.UtcNow);
        var ventures = _retainerPlanner.Ventures;
        var targets = _retainerPlanner.FarmTargets();
        var prices = _retainerPlanner.Prices();

        DrawStatusLine($"{rows.Count} retainers · {rows.Count(RetainerReady)} ready · {ventures.Count} ventures known"
                       + $" · {prices.Count(p => p.Value > 0)} item prices known");
        DrawStatusLine($"reader: {_retainers.Status} · catalog: {_retainerPlanner.Status}", CharonTheme.TextDisabled);
        ImGui.Spacing();

        using (var group = SettingsGroup.Begin("Surfaces"))
        {
            var board = _config.RetainerWindowVisible;
            if (group.Toggle("Retainer board", "The standalone window: a row per retainer with its actions, and the "
                                               + "full venture list ranked by what each venture pays per hour.",
                    ref board))
            {
                _config.RetainerWindowVisible = board;
                _save();
            }

            var overlay = _config.RetainerOverlayEnabled;
            if (group.Toggle("At the bell", "A panel beside the game's retainer list: what each retainer is doing, "
                                            + "what it will be sent on, and Send/Collect on the spot. It exists only "
                                            + "while the list is open, so it cannot appear uninvited.",
                    ref overlay))
            {
                _config.RetainerOverlayEnabled = overlay;
                _save();
            }

            group.Row("Open the board", "Same window, opened from here instead of the bell.", 92f, () =>
            {
                if (Buttons.Action("Open", true, 92f))
                    _openRetainerBoard();
            });
        }

        using (var group = SettingsGroup.Begin("Auto assign"))
        {
            group.Row("Default mode", "What a retainer with no mode of its own does. Off by default: nothing is sent "
                                      + "anywhere until you say so — per retainer, or here.", 268f, () =>
            {
                var current = DefaultModeIndex(_retainerPlanner.DefaultMode);
                var picked = Segmented.Draw(["best it can do", "farm list only", "off"], current,
                    "best it can do: the highest gil/hour this retainer qualifies for today.\n"
                    + "farm list only: whatever the farm list asks for, and nothing else.");
                if (picked != current)
                {
                    _retainerPlanner.SetDefaultMode(picked switch
                    {
                        0 => VentureAssignment.BestValue,
                        1 => VentureAssignment.FromFarm,
                        _ => VentureAssignment.Off,
                    });
                }
            });

            group.Note("A mode decides WHAT gets chosen when a send happens — it never sends anyone out. "
                       + "Every send is a button: one retainer at a time, one click per tick, Stop always reachable.");
        }

        Styling.SectionLabel("Per retainer");
        Styling.VSpace(2f);

        if (rows.Count == 0)
        {
            Styling.Text(_retainers.Loaded ? "No retainers on this character." : _retainers.Status,
                CharonTheme.TextDisabled);
            ImGui.Spacing();
            return;
        }

        if (ImGui.BeginTable("retainerSettings", 5,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 112f);
            ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 64f);
            ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 196f);
            ImGui.TableSetupColumn("Runs next", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##best", ImGuiTableColumnFlags.WidthFixed, 62f);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                var key = _retainerPlanner.Key(_localContentId(), row.Name);
                var mode = _retainerPlanner.Mode(key);
                var option = _retainerPlanner.Resolve(row, key);
                var profile = _retainerPlanner.Profile(row);

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                Styling.Text(row.Name, CharonTheme.TextSecondary);

                ImGui.TableNextColumn();
                Styling.Text(profile.Job.Length > 0 ? $"{profile.Job} {row.Level}" : row.Level.ToString(),
                    CharonTheme.TextDim);

                ImGui.TableNextColumn();
                var index = ModeIndex(mode);
                var picked = Segmented.Draw(["best", "pick", "farm", "off"], index,
                    "best — the highest gil/hour this retainer qualifies for.\n"
                    + "pick — one venture, chosen in the card below.\n"
                    + "farm — only what the farm list asks for.\n"
                    + "off — nothing is ever chosen for it.");
                if (picked != index)
                {
                    _retainerPlanner.SetMode(key, picked switch
                    {
                        0 => VentureAssignment.BestValue,
                        1 => VentureAssignment.Picked,
                        2 => VentureAssignment.FromFarm,
                        _ => VentureAssignment.Off,
                    });
                }

                ImGui.TableNextColumn();
                Styling.Text(
                    option != null
                        ? $"{option.Venture.Name} · x{option.QuantityPerRun}{(option.TierKnown ? string.Empty : "?")}"
                        : mode == VentureAssignment.Off ? "— off" : "nothing qualifies",
                    option != null ? CharonTheme.TextDim : CharonTheme.TextMuted);

                ImGui.TableNextColumn();
                if (Buttons.Action("Best", true, 56f))
                    _retainerPlanner.SetMode(key, VentureAssignment.BestValue);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Send this one on the best it can do");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();

        var picking = rows
            .Where(r => _retainerPlanner.Mode(_retainerPlanner.Key(_localContentId(), r.Name)) == VentureAssignment.Picked)
            .ToList();

        if (picking.Count > 0)
        {
            using var group = SettingsGroup.Begin("Picked ventures");
            foreach (var row in picking)
            {
                var key = _retainerPlanner.Key(_localContentId(), row.Name);
                var currentId = _retainerPlanner.Picked(key);
                var options = VentureCatalog.Rank(_retainerPlanner.Profile(row), ventures, prices)
                    .Where(o => o.Runnable)
                    .Take(24)
                    .ToList();

                group.Row(row.Name, "Only ventures this retainer can actually run, best value first.", 250f, () =>
                {
                    var preview = options.FirstOrDefault(o => o.Venture.TaskId == currentId)?.Venture.Name
                                  ?? (currentId == 0 ? "choose a venture…" : $"venture {currentId}");

                    ImGui.SetNextItemWidth(250f * ImGuiHelpers.GlobalScale);
                    if (ImGui.BeginCombo($"##pick{key}", preview))
                    {
                        foreach (var option in options)
                        {
                            if (ImGui.Selectable(
                                    $"{option.Venture.Name} · x{option.QuantityPerRun}##{option.Venture.TaskId}",
                                    option.Venture.TaskId == currentId))
                            {
                                _retainerPlanner.SetPicked(key, option.Venture.TaskId);
                            }
                        }

                        ImGui.EndCombo();
                    }
                });
            }

            group.Note("A blocked venture never appears here: a pick that cannot run is a plan that quietly does nothing.");
        }

        using (var group = SettingsGroup.Begin("Retainer contents"))
        {
            group.Note("What each retainer holds, as last SEEN. The client only has a retainer's bags once its "
                       + "window has been opened at a bell, so this store fills as you visit them — and an "
                       + "unopened retainer is unknown, never empty. Other plugins read it over IPC.");
            group.Row("Refresh every retainer", "A pass that captures each retainer as you open it at a bell: "
                                                + "the status line names the next one. Nothing is selected for "
                                                + "you — Charon does not click retainers.", 200f, () =>
            {
                if (_retainerContents.Busy)
                {
                    if (Buttons.Action("Stop", true, 92f, CharonTheme.AccentRose))
                        _retainerContents.Stop("stopped");
                }
                else if (Buttons.Action("Refresh all", true, 200f))
                {
                    _retainerContents.ArmRefresh();
                }
            });
        }

        using (var group = SettingsGroup.Begin("Farm list"))
        {
            group.Row("Add an item", "A name or an item id, with an optional wanted count — \"Manganese Ore x500\". "
                                     + "The board's Farm tab shows who brings what back and how many runs it takes.",
                250f, () =>
                {
                    ImGui.SetNextItemWidth(186f * ImGuiHelpers.GlobalScale);
                    ImGui.InputTextWithHint("##farmAddMain", "item or id…", ref _retainerFarmInput, 96);
                    ImGui.SameLine();
                    if (Buttons.Action("Add", _retainerFarmInput.Trim().Length > 0, 56f))
                    {
                        _retainerPlanner.AddFarmTarget(_retainerFarmInput);
                        _retainerFarmInput = string.Empty;
                    }
                });

            foreach (var target in targets)
            {
                var label = target.Name.Length > 0 ? target.Name : $"item {target.ItemId}";
                group.Row(label, target.Wanted is { } wanted ? $"{wanted:N0} wanted" : null, 28f, () =>
                {
                    if (ImGui.SmallButton($"✕##rmMain{target.ItemId}"))
                        _retainerPlanner.RemoveFarmTarget(target);
                });
            }

            group.Note(targets.Count == 0
                ? "Nothing on the farm list — that is the whole item-location database question answered by the "
                  + "game's own sheets, which is why an item only has to be named here."
                : $"{targets.Count} item(s). Nobody is sent anywhere by this list: it decides what a send chooses.");
        }

        ImGui.Spacing();
        DrawStatusLine($"Retainers: {_retainerPlanner.Status} · {_ventureRunner.Status}", CharonTheme.TextDisabled);
    }

    /// <summary>The side-of-the-sidebar index for a mode, in the order the segmented control shows them.</summary>
    private static int ModeIndex(VentureAssignment mode) => mode switch
    {
        VentureAssignment.BestValue => 0,
        VentureAssignment.Picked => 1,
        VentureAssignment.FromFarm => 2,
        _ => 3,
    };

    /// <summary>Picked has nothing to pick as a global default, so it reads as off here.</summary>
    private static int DefaultModeIndex(VentureAssignment mode) => mode switch
    {
        VentureAssignment.BestValue => 0,
        VentureAssignment.FromFarm => 1,
        _ => 2,
    };

    private static bool RetainerReady(RetainerVenture row) =>
        row.CompleteUtc is { } done && done <= DateTime.UtcNow;

    private int ReadyRetainerCount() => _retainers.Read(DateTime.UtcNow).Count(RetainerReady);

    private void DrawTweaksSection()
    {
        DrawPageHeader("Tweaks");

        var openChests = _config.AutoOpenChestsEnabled;
        if (ImGui.Checkbox("Auto-open treasure chests##qol", ref openChests))
        {
            _config.AutoOpenChestsEnabled = openChests;
            _save();
        }
        CharonTheme.HelpMarker("Walk within reach of a chest and open it. Out of combat only, never\n"
                               + "in high-end duties, and never a chest someone already opened.\n"
                               + "Ported from Pandora's Box (BSD-3-Clause).");
        if (openChests)
        {
            ImGui.Indent();
            var chestRange = _config.ChestOpenRange;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Open range (yalms)##qol", ref chestRange, 2f, 8f, "%.1f"))
            {
                _config.ChestOpenRange = chestRange;
                _save();
            }
            CharonTheme.HelpMarker("How close before the open fires. The game enforces its own\n"
                                   + "interact limit — past it, the chest simply opens as soon as\n"
                                   + "you get near enough.");
            ImGui.Unindent();
        }

        var autoQte = _config.AutoQteEnabled;
        if (ImGui.Checkbox("Auto Active Time Maneuver##qol", ref autoQte))
        {
            _config.AutoQteEnabled = autoQte;
            _save();
        }
        CharonTheme.HelpMarker("Mash the button when an ATM appears, so an unattended toon never\n"
                               + "fails one. Direct Chat is parked off during the mash and restored\n"
                               + "after. Ported from Pandora's Box (BSD-3-Clause).");

        var autoCommend = _config.AutoCommendEnabled;
        if (ImGui.Checkbox("Auto-commendation after duty##qol", ref autoCommend))
        {
            _config.AutoCommendEnabled = autoCommend;
            _save();
        }
        CharonTheme.HelpMarker("Commend a party member when the end-of-duty banner appears.\n"
                               + "Never people you queued WITH (the game refuses premades), never\n"
                               + "in PvP. Ported from Pandora's Box (BSD-3-Clause).");
        if (autoCommend)
        {
            ImGui.Indent();
            var prio = _config.CommendPriority;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.Combo("Priority##commend", ref prio, "Tank first Healer first DPS first No priority "))
            {
                _config.CommendPriority = prio;
                _save();
            }

            var hideChat = _config.CommendHideChat;
            if (ImGui.Checkbox("Hide chat message##commend", ref hideChat))
            {
                _config.CommendHideChat = hideChat;
                _save();
            }

            var exclDeaths = _config.CommendExcludeDeaths;
            if (ImGui.Checkbox("Exclude members that died##commend", ref exclDeaths))
            {
                _config.CommendExcludeDeaths = exclDeaths;
                _save();
            }
            ImGui.Unindent();
        }

        var autoTurnIn = _config.AutoTurnInEnabled;
        if (ImGui.Checkbox("Auto-select turn-ins##qol", ref autoTurnIn))
        {
            _config.AutoTurnInEnabled = autoTurnIn;
            _save();
        }
        CharonTheme.HelpMarker("When an item turn-in window opens (quest hand-ins, supply missions),\n"
                               + "fill every slot automatically. Ported from Pandora's Box\n"
                               + "(BSD-3-Clause).");
        if (autoTurnIn)
        {
            ImGui.Indent();
            var confirm = _config.AutoTurnInConfirm;
            if (ImGui.Checkbox("Automatically confirm##turnin", ref confirm))
            {
                _config.AutoTurnInConfirm = confirm;
                _save();
            }
            CharonTheme.HelpMarker("Also press Hand Over once filled. Off by default — handing\n"
                                   + "items over is a decision, filling the window is not.");
            ImGui.Unindent();
        }

        var textAdvance = _config.TextAdvanceEnabled;
        if (ImGui.Checkbox("Auto-advance dialogue##qol", ref textAdvance))
        {
            _config.TextAdvanceEnabled = textAdvance;
            _save();
        }
        CharonTheme.HelpMarker("Click through quest dialogue (Talk boxes) automatically. Off by\n"
                               + "default — on a box a human is playing this eats the story. Odysseus\n"
                               + "can also switch it on over IPC with a self-expiring lease while it\n"
                               + "runs quests, regardless of this toggle.");

        ImGui.Spacing();
        DrawAfkGuardBlock();
    }

    /// <summary>Deep-dungeon specific tools: the floor map and the ESP overlay.</summary>
    private void DrawDeepDungeonSection()
    {
        DrawPageHeader("Deep Dungeon");

        var ddMap = _config.DeepDungeonMapEnabled;
        if (ImGui.Checkbox("Deep dungeon floor map##qol", ref ddMap))
        {
            _config.DeepDungeonMapEnabled = ddMap;
            _save();
        }
        CharonTheme.HelpMarker("A floor map window shown only inside deep dungeons: the full 5x5\n"
                               + "room layout with connections, passage, return, chests and party\n"
                               + "positions — including rooms the game hasn't revealed yet (dim).");

        var ddEsp = _config.DeepDungeonEspEnabled;
        if (ImGui.Checkbox("Deep dungeon ESP overlay##qol", ref ddEsp))
        {
            _config.DeepDungeonEspEnabled = ddEsp;
            _save();
        }
        CharonTheme.HelpMarker("Draws chests, passage, return, revealed traps and mob aggro ranges\n"
                               + "over the world while in a deep dungeon. Aggro shapes follow how each\n"
                               + "mob notices you: circle = proximity, circle+core = sound, cone =\n"
                               + "sight (NecroLens's dataset, MIT). Patrols get a facing arrow.");
        if (ddEsp)
        {
            ImGui.Indent();
            var espMobs = _config.DeepDungeonEspMobs;
            if (ImGui.Checkbox("Mob aggro ranges##ddesp", ref espMobs))
            {
                _config.DeepDungeonEspMobs = espMobs;
                _save();
            }

            var espNames = _config.DeepDungeonEspMobNames;
            if (ImGui.Checkbox("Mob names##ddesp", ref espNames))
            {
                _config.DeepDungeonEspMobNames = espNames;
                _save();
            }

            var espChests = _config.DeepDungeonEspChests;
            if (ImGui.Checkbox("Chests and floor objects##ddesp", ref espChests))
            {
                _config.DeepDungeonEspChests = espChests;
                _save();
            }
            CharonTheme.HelpMarker("Coffers, hoards, passage and return, within 35 yalms.");

            var espTraps = _config.DeepDungeonEspTraps;
            if (ImGui.Checkbox("Traps##ddesp", ref espTraps))
            {
                _config.DeepDungeonEspTraps = espTraps;
                _save();
            }
            CharonTheme.HelpMarker("Revealed traps, at any distance — one you can see down a\n"
                                   + "corridor is exactly the one worth routing around.\n\n"
                                   + "Unrevealed traps are SERVER-SIDE: until a Pomander of Sight\n"
                                   + "reveals them they do not exist in the world at all, so no\n"
                                   + "plugin can draw them (NecroLens can't either). Without Sight\n"
                                   + "the first you know of a landmine is the chat line.");
            ImGui.Unindent();
        }
    }

    /// <summary>
    /// Stay-logged-in: the toggle, the threshold, and — the part worth having on screen — the client's
    /// own idle timer, so "am I safe" is a number rather than a feeling.
    /// </summary>
    /// <summary>
    /// The daily Grand Company board: the game's three delivery tabs, what each one asks for today, and where
    /// that item actually is — bags or a retainer.
    ///
    /// Read-only by design. The board's rows are read from the agent that owns them, so this works while the
    /// window is open or shut; handing in happens at the officer, where the game wants a mission SELECTED
    /// first and Charon does not click rows in a list whose selection mechanism it has never verified. The
    /// turn-in fill (TWEAKS → auto-select turn-ins) fills the delivery window once the game opens it.
    /// </summary>
    private void DrawGcDailiesSection()
    {
        DrawPageHeader("Grand Company Dailies", "supply, provisioning and expert delivery — what the day asks for");

        var board = _gcDailies.Read();
        var plans = _gcDailies.Plans(board);

        DrawStatusLine($"{GcDailies.GrandCompanyName(board.Company)} · rank {board.Rank} · "
                       + $"{board.Seals:N0} / {board.MaxSeals:N0} seals");

        // The Timers window's mission-allowance line says when the request list ROLLS OVER. It is the number the
        // player reads off their own window, and it is not a statement about what has been handed in — a
        // countdown here does not mean today is done, which is exactly the claim this line used to make.
        if (board.AllowanceSeenUtc is { } seen)
        {
            var age = DateTime.UtcNow - seen;
            var verdict = board.DailiesOpen switch
            {
                true => "the request list is current",
                false => "the request list rolls over when it expires",
                _ => "state not recognised",
            };

            DrawStatusLine($"Next mission allowance: {board.AllowanceText} ({verdict})"
                           + $", read {(age.TotalMinutes < 1 ? "just now" : $"{age.TotalMinutes:0} min ago")}",
                CharonTheme.TextSecondary);
        }
        else
        {
            DrawStatusLine($"Next mission allowance: {board.AllowanceStatus}", CharonTheme.TextMuted);
        }

        // The ROWS decide, and nothing else. A sentence being present in the window's value array is not proof
        // that it is on screen — the client fills values for every tab when the window opens, so
        // "No more deliveries are being accepted today." sits in there while the Supply tab is showing eight
        // rows with nothing handed in. Trusting that string is what made this page claim the day was done.
        DrawStatusLine(GcDailies.RequestSummary(plans, board.Open), CharonTheme.TextSecondary);

        // The window's own sentence is shown only when the tab being LOOKED AT is empty, which is when the game
        // is actually showing it — "You possess no applicable items." is the Expert Delivery tab saying it.
        if (board.Notice.Length > 0 && board.SelectedTabEmpty)
            DrawStatusLine($"the window says: \"{board.Notice}\"", CharonTheme.TextMuted);

        DrawStatusLine(GcDailies.Summarise(plans), CharonTheme.TextSecondary);
        DrawStatusLine(board.Status, CharonTheme.TextDisabled);
        ImGui.Spacing();

        if (board.Missions.Count == 0)
        {
            CharonTheme.HelpMarker("The board's rows are read from the game while you are at a Grand Company\n"
                                   + "officer: ask to submit supplies, provisioning or gear, and they fill in here.\n"
                                   + "Nothing about this page is a click on your behalf.");
            return;
        }

        // The tab counts, by the client's own three-way split.
        var supply = plans.Where(p => p.Mission.Kind == GcMissionKind.Supply).ToList();
        var provisioning = plans.Where(p => p.Mission.Kind == GcMissionKind.Provisioning).ToList();
        var expert = plans.Where(p => p.Mission.Kind == GcMissionKind.ExpertDelivery).ToList();

        var scale = ImGuiHelpers.GlobalScale;
        var tileWidth = Math.Max(150f, (ImGui.GetContentRegionAvail().X / 3f) - (12f * scale));
        StatTile.Draw("Supply (DoH)", $"{supply.Count(p => p.Ready)}/{supply.Count}",
            supply.Count == 0 ? "nothing requested today" : $"{supply.Count} mission(s) requested",
            CharonTheme.AccentCyan, tileWidth);
        ImGui.SameLine();
        StatTile.Draw("Provisioning (DoL)", $"{provisioning.Count(p => p.Ready)}/{provisioning.Count}",
            provisioning.Count == 0 ? "nothing requested today" : $"{provisioning.Count} mission(s) requested",
            CharonTheme.AccentMint, tileWidth);
        ImGui.SameLine();
        StatTile.Draw("Expert Delivery", expert.Count.ToString(),
            "gear hand-ins — unlimited, SealBreaker's loop", CharonTheme.AccentAmber, tileWidth);

        ImGui.Spacing();

        if (!ImGui.BeginTable("gcRows", 8,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("For", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Req", ImGuiTableColumnFlags.WidthFixed, 36f);
        ImGui.TableSetupColumn("Exp", ImGuiTableColumnFlags.WidthFixed, 84f);
        ImGui.TableSetupColumn("Seals", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("Bags", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 250f);
        ImGui.TableHeadersRow();

        foreach (var plan in plans)
        {
            var mission = plan.Mission;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            Styling.Text(mission.Job.Length > 0
                ? mission.Job
                : mission.Kind == GcMissionKind.ExpertDelivery ? "gear" : "—", CharonTheme.TextDim);

            ImGui.TableNextColumn();
            Styling.Text(mission.ItemName + (mission.BonusReward ? "  ★" : string.Empty),
                plan.Ready ? CharonTheme.TextStrong : CharonTheme.TextSecondary);

            ImGui.TableNextColumn();
            Styling.Text(mission.Requested.ToString(), CharonTheme.TextDim);

            ImGui.TableNextColumn();
            Styling.Text(mission.ExpReward > 0 ? mission.ExpReward.ToString("N0") : "—", CharonTheme.TextDim);

            ImGui.TableNextColumn();
            Styling.Text(mission.SealReward > 0 ? mission.SealReward.ToString("N0") : "—", CharonTheme.TextDim);

            ImGui.TableNextColumn();
            Styling.Text(plan.InBags.ToString(), plan.InBags >= mission.Requested && mission.Requested > 0
                ? CharonTheme.AccentMint
                : CharonTheme.TextDim);

            // The column the game's own board cannot show: its possessed count only ever sees the bags.
            ImGui.TableNextColumn();
            Styling.Text(plan.InRetainers > 0 ? plan.InRetainers.ToString() : "—",
                plan.NeedsFetch ? CharonTheme.AccentAmber : CharonTheme.TextDim);

            ImGui.TableNextColumn();
            Styling.Text(plan.Status, plan.Ready
                ? CharonTheme.AccentMint
                : plan.NeedsFetch ? CharonTheme.AccentAmber : CharonTheme.TextMuted);
        }

        ImGui.EndTable();

        ImGui.Spacing();
        DrawStatusLine("Hand in at the officer: pick the mission there and the delivery window's fill "
                       + "(TWEAKS → auto-select turn-ins) puts the item in for you. Selecting the mission is "
                       + "yours — Charon does not click rows in a list it has not verified.",
            CharonTheme.TextMuted);
        DrawStatusLine(_gcDailies.Status, CharonTheme.TextDisabled);
    }

    private void DrawAfkGuardBlock()
    {
        var guard = _config.AfkGuardEnabled;
        if (ImGui.Checkbox("Stay logged in when idle##afk", ref guard))
        {
            _config.AfkGuardEnabled = guard;
            _save();
            _afkGuard.Reset();
        }

        CharonTheme.HelpMarker("Watch the client's own idle timer and send it a keystroke before it\n"
                               + "logs us out. Only ever acts when the CLIENT says it is idle, and only\n"
                               + "ever in the background — a bare left Ctrl, which has no action of its own.\n"
                               + "A toon kept moving by a plugin is still idle to the client: movement is\n"
                               + "not input, which is what makes this worth having on a box that follows.");

        if (guard)
        {
            ImGui.Indent();
            var threshold = _config.AfkGuardThresholdSeconds;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderInt("Nudge after (seconds idle)##afk", ref threshold, 60, 1800))
            {
                _config.AfkGuardThresholdSeconds = threshold;
                _save();
            }

            CharonTheme.HelpMarker("The client logs out around the 30-minute mark; 10 minutes is early\n"
                                   + "enough to be safe and late enough that the nudge is rare.");
            ImGui.Unindent();
        }

        DrawStatusLine(_afkGuard.Status
                       + (_afkGuard.Nudges > 0 ? $" · {_afkGuard.Nudges} nudges this session" : string.Empty),
            _afkGuard.StuckNudges > 0 ? CharonTheme.StatusYellow : CharonTheme.TextDim);
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
        DrawStatusLine($"Sprint: {_sprintStatus()}");
        DrawStatusLine($"Nav: {_navStatus()}");
        DrawStatusLine($"QoL: {_qolStatus()}");
        DrawStatusLine($"Loot: {_lootStatus()}");
        DrawStatusLine($"Leveling: {_levelingStatus()}");
        // Reading the line IS the refresh — the reader is lazy and nothing else polls it here.
        _weeklies.Read(DateTime.UtcNow);
        DrawStatusLine($"Weeklies: {_weeklies.Status}");

        // Read-only: this never opens a bell, so it is safe on a hand-played box.
        _retainers.Read(DateTime.UtcNow);
        DrawStatusLine($"Retainers: {_retainers.Status} · ventures: {_ventureRunner.Status}");
        DrawStatusLine($"Doman: {_doman.Status}"
                       + (_doman.StackReady ? " · stack ready to stage" : string.Empty));
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
