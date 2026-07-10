using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Charon.Features.AutoAccept;
using Charon.Features.AutoPillion;
using Charon.Ipc;
using Charon.Services;
using Charon.Services.Game;
using Charon.Windows;

namespace Charon;

public sealed class CharonPlugin : IDalamudPlugin
{
    public const string PluginVersion = "0.1.0";
    private const string CommandName = "/charon";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IPartyList _partyList;
    private readonly IPluginLog _log;

    private readonly CharonConfig _config;
    private readonly WhitelistService _whitelist;
    private readonly DaedalusIpcClient _daedalusIpc;
    private readonly CharonPillionIpc _pillionIpc;
    private readonly PillionManager _pillionManager;
    private readonly GroupInviteManager _inviteManager;
    private readonly GroupInviteInterop _inviteInterop;
    private readonly MountStateReader _mountReader;
    private readonly NavClient _nav;

    private readonly WindowSystem _windowSystem = new("Charon");
    private readonly MainWindow _mainWindow;

    /// <summary>Previous per-seat occupant entity ids (index 0 = seat 1) — diffed each frame.</summary>
    private uint[] _previousOccupants = Array.Empty<uint>();
    private DateTime _lastCandidateRefreshUtc = DateTime.MinValue;

    // Passenger-side self-boarding state (one boarding session per detected owner mount-up).
    private uint _boardingOwnerEntityId;
    private DateTime _boardingDetectedUtc;
    private DateTime _lastBoardingAttemptUtc;
    private int _boardingAttempts;
    private const int MaxBoardingAttempts = 3;

    /// <summary>Human-readable passenger-boarding state, shown in the Debug section.</summary>
    private string _boardingStatus = "idle";

    /// <summary>Boarding can only succeed near the mount — nav in when further than this.</summary>
    private const float BoardingRangeYalms = 4.0f;

    /// <summary>True while a vnavmesh path WE issued is (or may be) running — never stop user paths.</summary>
    private bool _navIssuedByUs;
    private DateTime _lastNavIssueUtc = DateTime.MinValue;

    public CharonPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IObjectTable objectTable,
        IPartyList partyList,
        IDataManager dataManager,
        IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _framework = framework;
        _objectTable = objectTable;
        _partyList = partyList;
        _log = log;

        _config = pluginInterface.GetPluginConfig() as CharonConfig ?? new CharonConfig();

        _whitelist = new WhitelistService(_config.ManualWhitelist, SaveConfig);
        _daedalusIpc = new DaedalusIpcClient(pluginInterface, log);
        _pillionIpc = new CharonPillionIpc(pluginInterface, log);
        _pillionIpc.OnAssignmentReceived += OnPillionAssignmentReceived;

        _mountReader = new MountStateReader(objectTable, dataManager);
        _nav = new NavClient(pluginInterface, log);
        _pillionManager = new PillionManager(
            Features.AutoPillion.PillionConfig.From(_config),
            SendPillionInvite,
            message => _log.Debug("[Pillion] {0}", message));

        _inviteManager = new GroupInviteManager(
            AutoAcceptConfig.From(_config),
            _whitelist,
            _daedalusIpc,
            AcceptPendingInvite,
            log: message => _log.Debug("[AutoAccept] {0}", message));
        _inviteInterop = new GroupInviteInterop(dataManager, _inviteManager, log);

        _mainWindow = new MainWindow(_config, SaveConfig, _whitelist, _daedalusIpc, _pillionManager, _inviteManager,
            ReadRawSeatOccupancy, () => _boardingStatus);
        _mainWindow.IsOpen = _config.MainWindowVisible;
        _windowSystem.AddWindow(_mainWindow);

        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Charon window.",
        });

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi += OpenMainWindow;
        _pluginInterface.UiBuilder.OpenConfigUi += OpenMainWindow;
        _framework.Update += OnFrameworkUpdate;

        _log.Info("Charon v{0} loaded", PluginVersion);
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi -= OpenMainWindow;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenMainWindow;

        _commandManager.RemoveHandler(CommandName);
        _windowSystem.RemoveAllWindows();

        _pillionIpc.OnAssignmentReceived -= OnPillionAssignmentReceived;
        _inviteInterop.Dispose();
        _pillionIpc.Dispose();
        _daedalusIpc.Dispose();

        _config.MainWindowVisible = _mainWindow.IsOpen;
        SaveConfig();
    }

    private void OnCommand(string command, string args)
    {
        _mainWindow.IsOpen = !_mainWindow.IsOpen;
        _config.MainWindowVisible = _mainWindow.IsOpen;
        SaveConfig();
    }

    private void OpenMainWindow() => _mainWindow.IsOpen = true;

    private void SaveConfig() => _pluginInterface.SavePluginConfig(_config);

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        // Config snapshots — cheap records, safe to rebuild every frame.
        _pillionManager.UpdateConfig(Features.AutoPillion.PillionConfig.From(_config));
        _inviteManager.UpdateConfig(AutoAcceptConfig.From(_config));

        _daedalusIpc.Refresh(now);
        UpdatePillionSession(now);
        UpdatePassengerBoarding(now);
        _inviteInterop.Poll(now);

        _pillionManager.Update(now);
        _inviteManager.Update(now);
    }

    private void UpdatePillionSession(DateTime now)
    {
        var snapshot = _mountReader.ReadLocalMount();

        if (snapshot == null || snapshot.PassengerSeats < 1)
        {
            if (_pillionManager.SessionActive)
            {
                _pillionManager.OnDismounted();
                _previousOccupants = Array.Empty<uint>();
            }
            return;
        }

        if (!_pillionManager.SessionActive)
        {
            _pillionManager.SetCandidates(BuildCandidates());
            _pillionManager.OnMounted(snapshot.MountId, snapshot.PassengerSeats, now);
            _previousOccupants = new uint[snapshot.SeatOccupantEntityIds.Length];
            _lastCandidateRefreshUtc = now;
        }
        else if (now - _lastCandidateRefreshUtc > TimeSpan.FromSeconds(2))
        {
            // Roster/whitelist can change mid-session (toon logs in, entry added).
            _pillionManager.SetCandidates(BuildCandidates());
            _lastCandidateRefreshUtc = now;
        }

        DiffSeatOccupancy(snapshot.SeatOccupantEntityIds);
    }

    /// <summary>Turn occupancy changes into seat events for the manager (index 0 = passenger seat 1).</summary>
    private void DiffSeatOccupancy(uint[] current)
    {
        if (_previousOccupants.Length != current.Length)
            _previousOccupants = new uint[current.Length];

        for (var i = 0; i < current.Length; i++)
        {
            var seatIndex = i + 1;
            var previous = _previousOccupants[i];
            var occupant = current[i];
            if (occupant == previous)
                continue;

            if (occupant != 0)
                _pillionManager.OnSeatOccupied(seatIndex, ResolveCharacterName(occupant));
            else
                _pillionManager.OnSeatVacated(seatIndex);
        }

        _previousOccupants = current;

        // Name-resolution retry: the object table sometimes can't resolve the rider on the exact
        // frame they board. A filled seat with no name means the manager doesn't know WHO is
        // seated (so it can't release their pending invite elsewhere) — keep retrying until the
        // lookup succeeds. OnSeatOccupied is idempotent for the same seat.
        foreach (var seat in _pillionManager.Seats)
        {
            if (seat.Status != SeatStatus.Filled || seat.AssignedName.Length > 0)
                continue;

            var index = seat.Index - 1;
            if (index < 0 || index >= current.Length || current[index] == 0)
                continue;

            var name = ResolveCharacterName(current[index]);
            if (name.Length > 0)
                _pillionManager.OnSeatOccupied(seat.Index, name);
        }
    }

    /// <summary>Live raw seat view for the debug section: (seat, entity id, resolved name).</summary>
    private IReadOnlyList<(int Seat, uint EntityId, string Name)> ReadRawSeatOccupancy()
    {
        var snapshot = _mountReader.ReadLocalMount();
        if (snapshot == null)
            return Array.Empty<(int, uint, string)>();

        var rows = new (int, uint, string)[snapshot.SeatOccupantEntityIds.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var id = snapshot.SeatOccupantEntityIds[i];
            rows[i] = (i + 1, id, id == 0 ? string.Empty : ResolveCharacterName(id));
        }

        return rows;
    }

    /// <summary>
    /// Passenger side: when a trusted owner nearby is on a multi-seat mount and we are unmounted,
    /// deterministically pick our seat (same computation on every client — see
    /// <see cref="PassengerSeatPicker"/>) and board via /ridepillion. No cross-client messaging:
    /// every passenger box observes the same owner, roster, and occupancy, so assignments don't
    /// collide. Boarding is staggered by seat number to keep occupancy views consistent.
    /// </summary>
    private void UpdatePassengerBoarding(DateTime now)
    {
        if (!_config.AutoPillionEnabled || _pillionManager.SessionActive)
        {
            ResetBoarding(_config.AutoPillionEnabled ? "owner session active" : "disabled");
            return;
        }

        var local = _objectTable.LocalPlayer;
        if (local == null)
        {
            ResetBoarding("no local player");
            return;
        }

        // Already mounted (as owner) or already riding — nothing to do.
        if (_mountReader.IsLocalRidingPillion())
        {
            ResetBoarding("riding pillion");
            return;
        }

        if (_mountReader.ReadLocalMount() != null)
        {
            ResetBoarding("mounted (own mount)");
            return;
        }

        var localName = local.Name.TextValue;
        var trusted = BuildTrustedNames(localName);
        if (trusted.Count == 0)
        {
            ResetBoarding("no trusted characters (LAN roster empty?)");
            return;
        }

        // Find the nearest trusted owner on a multi-seat mount.
        Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter? owner = null;
        MountSnapshot? mount = null;
        foreach (var obj in _objectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc
                || pc.EntityId == local.EntityId
                || !trusted.Contains(pc.Name.TextValue)
                || System.Numerics.Vector3.Distance(local.Position, pc.Position) > 30f)
                continue;

            var snapshot = _mountReader.ReadMountOf(pc);
            if (snapshot == null || snapshot.PassengerSeats < 1)
                continue;

            owner = pc;
            mount = snapshot;
            break;
        }

        if (owner == null || mount == null)
        {
            ResetBoarding("no trusted multi-seat owner nearby");
            return;
        }

        // Pillion riding only works within a party — hold until the owner is in ours.
        // The session stays alive so boarding starts as soon as the party invite lands.
        if (!IsInMyParty(owner.Name.TextValue))
        {
            _boardingStatus = $"owner {owner.Name.TextValue} not in our party — waiting for group";
            return;
        }

        // New boarding session per owner mount-up.
        if (_boardingOwnerEntityId != owner.EntityId)
        {
            _boardingOwnerEntityId = owner.EntityId;
            _boardingDetectedUtc = now;
            _boardingAttempts = 0;
            _lastBoardingAttemptUtc = DateTime.MinValue;
            _log.Debug("Trusted owner {0} mounted a {1}-seat mount — planning to board",
                owner.Name.TextValue, mount.PassengerSeats + 1);
        }

        // Candidates = trusted toons ACTUALLY PRESENT at the mount and able to board right now.
        // Roster members who are absent, mounted elsewhere, or already riding must not consume
        // seat ranks — every passenger client sees the same nearby set, so this stays deterministic.
        var ownerName = owner.Name.TextValue;
        var presentCandidates = CollectBoardableCandidates(trusted, ownerName, owner.Position);

        var occupied = new bool[mount.SeatOccupantEntityIds.Length];
        for (var i = 0; i < occupied.Length; i++)
            occupied[i] = mount.SeatOccupantEntityIds[i] != 0;

        var seat = PassengerSeatPicker.PickSeat(localName, presentCandidates, occupied);
        if (seat == null)
        {
            // Not a candidate / no free seat left — stay put but keep the session.
            _boardingStatus = $"owner {ownerName} — no seat for us";
            return;
        }

        if (_boardingAttempts >= MaxBoardingAttempts)
        {
            _boardingStatus = $"owner {ownerName} — gave up after {MaxBoardingAttempts} attempts (seat {seat.Value})";
            return;
        }

        // Stagger by seat number so earlier seats board first and later clients see fresh occupancy.
        var boardAt = _boardingDetectedUtc
                      + TimeSpan.FromSeconds(_config.PillionDelay)
                      + TimeSpan.FromSeconds((seat.Value - 1) * 0.5);
        if (now < boardAt)
        {
            _boardingStatus = $"owner {ownerName} — boarding seat {seat.Value} in {(boardAt - now).TotalSeconds:F1}s";
            return;
        }

        // Too far to board — walk to the mount first (vnavmesh; skipped when unavailable).
        var distance = System.Numerics.Vector3.Distance(local.Position, owner.Position);
        if (distance > BoardingRangeYalms)
        {
            NavToOwner(owner.Position, distance, ownerName, now);
            return;
        }

        StopNavIfOurs();

        if (now - _lastBoardingAttemptUtc < TimeSpan.FromSeconds(2.5))
            return;

        _boardingAttempts++;
        _lastBoardingAttemptUtc = now;
        var sent = PillionRideHelper.TryRidePillion(owner, seat.Value);
        _boardingStatus = sent
            ? $"called RidePillion({ownerName}, seat {seat.Value}) (attempt {_boardingAttempts})"
            : $"RidePillion call FAILED (attempt {_boardingAttempts})";
        _log.Info("Passenger boarding: {0}", _boardingStatus);
    }

    /// <summary>
    /// Trusted toons standing near the mount who can board right now (not the owner, not riding,
    /// not on their own mount). This is the seat-ranking population for <see cref="PassengerSeatPicker"/>.
    /// </summary>
    private List<string> CollectBoardableCandidates(HashSet<string> trusted, string ownerName, System.Numerics.Vector3 ownerPosition)
    {
        var present = new List<string>();
        foreach (var obj in _objectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc)
                continue;

            var name = pc.Name.TextValue;
            if (name.Length == 0
                || name.Equals(ownerName, StringComparison.OrdinalIgnoreCase)
                || !trusted.Contains(name)
                || !IsInMyParty(name) // only party members can ride pillion
                || System.Numerics.Vector3.Distance(ownerPosition, pc.Position) > 30f
                || !_mountReader.CanBoard(pc))
                continue;

            present.Add(name);
        }

        return present;
    }

    /// <summary>True when the named character is in our current party (pillion requires a group).</summary>
    private bool IsInMyParty(string characterName)
    {
        try
        {
            foreach (var member in _partyList)
            {
                if (member.Name.TextValue.Equals(characterName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // party list unreadable mid-transition — treat as not in party
        }

        return false;
    }

    /// <summary>Trusted toon names for pillion: LAN roster, plus manual whitelist unless LanMembersOnly.</summary>
    private HashSet<string> BuildTrustedNames(string localName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var toon in _daedalusIpc.GetLanPartyMembers())
        {
            if (toon.CharacterName.Length > 0)
                names.Add(toon.CharacterName);
        }

        if (!_config.LanMembersOnly)
        {
            foreach (var entry in _whitelist.Entries)
            {
                if (entry.Enabled && entry.CharacterName.Length > 0)
                    names.Add(entry.CharacterName);
            }
        }

        // The local toon must rank itself among the candidates.
        if (localName.Length > 0 && names.Count > 0)
            names.Add(localName);

        return names;
    }

    /// <summary>Walk toward the owner via vnavmesh, re-issuing if the path stopped or the mount moved.</summary>
    private void NavToOwner(System.Numerics.Vector3 ownerPosition, float distance, string ownerName, DateTime now)
    {
        if (!_nav.IsAvailable)
        {
            _boardingStatus = $"owner {ownerName} — too far to board ({distance:F1}y) and vnavmesh unavailable";
            return;
        }

        // Re-issue only when nothing is running (path finished/failed or mount moved) — throttled.
        if ((!_navIssuedByUs || !_nav.IsPathRunning) && now - _lastNavIssueUtc > TimeSpan.FromSeconds(1.5))
        {
            if (_nav.MoveCloseTo(ownerPosition, BoardingRangeYalms - 1.5f))
            {
                _navIssuedByUs = true;
                _lastNavIssueUtc = now;
            }
        }

        _boardingStatus = $"owner {ownerName} — walking to mount ({distance:F1}y)";
    }

    private void StopNavIfOurs()
    {
        if (!_navIssuedByUs)
            return;

        _navIssuedByUs = false;
        if (_nav.IsPathRunning)
            _nav.Stop();
    }

    private void ResetBoarding(string status)
    {
        _boardingOwnerEntityId = 0;
        _boardingAttempts = 0;
        _boardingStatus = status;
        StopNavIfOurs();
    }

    private string ResolveCharacterName(uint entityId)
    {
        try
        {
            return _objectTable.SearchByEntityId(entityId)?.Name.TextValue ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// LAN roster toons (in LAN-window order) first, then enabled manual whitelist entries —
    /// restricted to toons actually standing near us and able to board right now. Inviting
    /// absent or mounted toons just burns seats into Declined.
    /// </summary>
    private IEnumerable<PillionCandidate> BuildCandidates()
    {
        var local = _objectTable.LocalPlayer;
        if (local == null)
            return Array.Empty<PillionCandidate>();

        var localName = local.Name.TextValue;
        var trusted = BuildTrustedNames(localName);
        var present = new HashSet<string>(
            CollectBoardableCandidates(trusted, localName, local.Position),
            StringComparer.OrdinalIgnoreCase);

        var lan = _daedalusIpc.GetLanPartyMembers()
            .Where(t => t.IsOnline && present.Contains(t.CharacterName))
            .Select((t, i) => new PillionCandidate(t.CharacterName, t.World, IsLanMember: true, LanOrder: i));

        var manual = _whitelist.Entries
            .Where(e => e.Enabled && present.Contains(e.CharacterName))
            .Select(e => new PillionCandidate(e.CharacterName, e.World, IsLanMember: false, LanOrder: 0));

        return lan.Concat(manual);
    }

    /// <summary>
    /// Invite transport: broadcast the assignment so the passenger's Charon instance rides the
    /// assigned seat, and mirror it onto the roster view for the UI.
    /// </summary>
    private void SendPillionInvite(PillionInvite invite)
    {
        var ownerName = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        if (ownerName.Length == 0)
            return;

        _pillionIpc.BroadcastAssignment(ownerName, invite.CharacterName, invite.SeatIndex);

        var toon = _daedalusIpc.GetLanPartyMembers()
            .FirstOrDefault(t => t.CharacterName.Equals(invite.CharacterName, StringComparison.OrdinalIgnoreCase));
        if (toon != null)
            toon.SeatAssignment = invite.SeatIndex;
    }

    /// <summary>Passenger side: an owner assigned OUR character a seat — hop on.</summary>
    private void OnPillionAssignmentReceived(PillionAssignmentMessage message)
    {
        var localName = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        if (localName.Length == 0
            || !message.MemberName.Equals(localName, StringComparison.OrdinalIgnoreCase)
            || message.OwnerName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            return;

        var owner = FindPlayerByName(message.OwnerName);
        if (owner != null && PillionRideHelper.TryRidePillion(owner, message.SeatIndex))
            _log.Debug("Riding {0}'s mount, seat {1}", message.OwnerName, message.SeatIndex);
    }

    private Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter? FindPlayerByName(string characterName)
    {
        foreach (var obj in _objectTable)
        {
            if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc
                && pc.Name.TextValue.Equals(characterName, StringComparison.OrdinalIgnoreCase))
                return pc;
        }

        return null;
    }

    private void AcceptPendingInvite() => _inviteInterop.AcceptCurrent();
}
