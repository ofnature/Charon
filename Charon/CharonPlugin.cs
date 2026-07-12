using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Charon.Features.AutoAccept;
using Charon.Features.AutoPillion;
using Charon.Features.HealWatch;
using Charon.Ipc;
using Charon.Services;
using Charon.Services.Game;
using Charon.Windows;

namespace Charon;

public sealed class CharonPlugin : IDalamudPlugin
{
    public const string PluginVersion = "0.1.2";
    private const string CommandName = "/charon";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IPartyList _partyList;
    private readonly IClientState _clientState;
    private readonly IAetheryteList _aetheryteList;
    private readonly IPluginLog _log;

    private readonly CharonConfig _config;
    private readonly WhitelistService _whitelist;
    private readonly DaedalusIpcClient _daedalusIpc;
    private readonly RelayClient _relay;
    private readonly PillionManager _pillionManager;
    private readonly GroupInviteManager _inviteManager;
    private readonly GroupInviteInterop _inviteInterop;
    private readonly TeleportOfferInterop _teleportOffer;
    private readonly MountStateReader _mountReader;
    private readonly NavClient _nav;
    private readonly HealWatchManager _healWatch;
    private readonly HealExecutor _healExecutor;

    // Heal Watch runs at 1 Hz; status surfaced in the window.
    private DateTime _lastHealScanUtc = DateTime.MinValue;
    private string _healStatus = "idle";

    private readonly WindowSystem _windowSystem = new("Charon");
    private readonly MainWindow _mainWindow;

    /// <summary>Previous per-seat occupant entity ids (index 0 = seat 1) — diffed each frame.</summary>
    private uint[] _previousOccupants = Array.Empty<uint>();
    private DateTime _lastCandidateRefreshUtc = DateTime.MinValue;

    // Passenger-side self-boarding state (one boarding session per detected owner mount-up).
    private uint _boardingOwnerEntityId;
    private DateTime _boardingDetectedUtc;
    private DateTime _lastBoardingAttemptUtc;
    // Attempts are cheap no-ops when they lose a race (rider mid-animation on the target seat),
    // so retry quickly and generously rather than predicting boarding animations.
    private int _boardingAttempts;
    private const int MaxBoardingAttempts = 8;
    private static readonly TimeSpan BoardingRetryInterval = TimeSpan.FromSeconds(1.25);

    /// <summary>Human-readable passenger-boarding state, shown in the Debug section.</summary>
    private string _boardingStatus = "idle";

    /// <summary>Pillion scan cadence — object-table sweeps are too heavy for every frame.</summary>
    private static readonly TimeSpan PillionTickInterval = TimeSpan.FromMilliseconds(250);
    private DateTime _lastPillionTickUtc = DateTime.MinValue;

    /// <summary>Local mount snapshot from the last pillion tick — shared by session, boarding, and UI.</summary>
    private MountSnapshot? _cachedLocalMount;

    /// <summary>Seat we last attempted — a shifting computed seat resets the attempt budget.</summary>
    private int _lastAttemptedSeat;

    /// <summary>Latest owner-issued seat assignment from the relay (null = observe-and-pick).</summary>
    private SeatCommand? _seatCommand;
    private static readonly TimeSpan SeatCommandLifetime = TimeSpan.FromSeconds(10);

    // Follow-teleport state: last seen territory per party member, and the pending follow.
    private readonly Dictionary<string, uint> _partyTerritories = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastFollowScanUtc = DateTime.MinValue;
    private DateTime? _pendingTeleportAtUtc;
    private uint _pendingAetheryteId;
    private byte _pendingAetheryteSubIndex;
    private string _followStatus = "idle";
    private readonly Random _followJitter = new();

    /// <summary>
    /// Boarding can only succeed near the mount — nav in when further than this.
    /// Compared on HORIZONTAL distance: the owner sits meters above ground on tall mounts,
    /// and 3D distance never converged there (toons walked in circles under the mount).
    /// </summary>
    private const float BoardingRangeYalms = 5.0f;

    /// <summary>True while a vnavmesh path WE issued is (or may be) running — never stop user paths.</summary>
    private bool _navIssuedByUs;
    private DateTime _lastNavIssueUtc = DateTime.MinValue;

    public CharonPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IObjectTable objectTable,
        IPartyList partyList,
        IClientState clientState,
        IAetheryteList aetheryteList,
        IDataManager dataManager,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _framework = framework;
        _objectTable = objectTable;
        _partyList = partyList;
        _clientState = clientState;
        _aetheryteList = aetheryteList;
        _log = log;

        _config = pluginInterface.GetPluginConfig() as CharonConfig ?? new CharonConfig();

        _whitelist = new WhitelistService(_config.ManualWhitelist, SaveConfig);
        _daedalusIpc = new DaedalusIpcClient(pluginInterface, log);
        _relay = new RelayClient(pluginInterface, log);
        _relay.OnMessage += OnRelayMessage;

        _mountReader = new MountStateReader(objectTable, dataManager, partyList);
        _nav = new NavClient(pluginInterface, log);
        _healWatch = new HealWatchManager(HealWatchConfig.From(_config));
        _healExecutor = new HealExecutor(objectTable, log);
        _teleportOffer = new TeleportOfferInterop(
            addonLifecycle,
            gameGui,
            () => _config.FollowTeleportEnabled,
            () => _config.TeleportOfferAddonName,
            name => { _config.TeleportOfferAddonName = name; SaveConfig(); },
            log);
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
            _healWatch, ReadRawSeatOccupancy, () => _boardingStatus,
            () => $"{_followStatus} · offer: {_teleportOffer.Status}",
            () => _healStatus);
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

        _relay.OnMessage -= OnRelayMessage;
        _teleportOffer.Dispose();
        _inviteInterop.Dispose();
        _relay.Dispose();
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

        // The pillion paths do full object-table scans with name resolution — far too heavy for
        // every frame with 8 clients running. 4 Hz is plenty for mount/seat state.
        if (now - _lastPillionTickUtc >= PillionTickInterval)
        {
            _lastPillionTickUtc = now;
            _cachedLocalMount = _mountReader.ReadLocalMount();
            UpdatePillionSession(now);
            UpdatePassengerBoarding(now);
        }

        _inviteInterop.Poll(now);
        _teleportOffer.Update(now);
        UpdateFollowTeleport(now);
        UpdateHealWatch(now);
        _pillionManager.Update(now);
        _inviteManager.Update(now);
    }

    /// <summary>
    /// Heal Watch: fleet vitals from the LAN roster → at most one heal per pass on the most
    /// urgent toon. Stands down while the local Daedalus rotation is enabled. 1 Hz.
    /// </summary>
    private void UpdateHealWatch(DateTime now)
    {
        _healWatch.UpdateConfig(HealWatchConfig.From(_config));

        if (!_config.HealWatchEnabled)
        {
            _healStatus = "disabled";
            return;
        }

        if (now - _lastHealScanUtc < TimeSpan.FromSeconds(1))
            return;
        _lastHealScanUtc = now;

        if (_daedalusIpc.IsRotationEnabled)
        {
            _healStatus = "standing down (Daedalus rotation enabled)";
            return;
        }

        var kit = _healExecutor.GetLocalKit();
        if (kit == null || kit.HealAction == 0)
        {
            _healStatus = "inert (not a healer job)";
            return;
        }

        var localName = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        var candidates = _daedalusIpc.GetLanPartyMembers()
            .Where(t => t.IsOnline && !t.CharacterName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            .Select(t => new HealCandidate(t.CharacterName, t.EntityId, t.Hp, IsInMyParty(t.CharacterName)));

        var intents = _healWatch.Evaluate(candidates, _daedalusIpc.IsRotationEnabled,
            canHot: kit.HotAction != 0, canRaise: kit.RaiseAction != 0, now);
        if (intents.Count == 0)
        {
            _healStatus = "watching";
            return;
        }

        // One CAST per pass (global cooldown ≈ one GCD), but walk the ranked intents until one
        // lands — the executor's live re-checks legitimately refuse stale ones (already topped
        // up, HoT still running, raise already pending).
        foreach (var intent in intents.Take(4))
        {
            var cast = intent.Kind switch
            {
                HealKind.Heal => _healExecutor.TryHeal(kit.HealAction, intent.EntityId, _config.HealThreshold),
                HealKind.Hot => _healExecutor.TryApplyHot(kit.HotAction, kit.HotStatusId, intent.EntityId),
                HealKind.Raise => _healExecutor.TryRaise(kit.RaiseAction, intent.EntityId),
                _ => false,
            };

            if (!cast)
                continue;

            _healWatch.OnHealCast(intent, now);
            _healStatus = intent.Kind switch
            {
                HealKind.Hot => $"HoT → {intent.Name}",
                HealKind.Raise => $"raising {intent.Name} (hardcast)",
                _ => $"healed {intent.Name}{(intent.Emergency ? " (EMERGENCY)" : "")}",
            };
            _log.Info("Heal Watch: {0}", _healStatus);
            return;
        }

        _healStatus = "watching (live checks refused this pass)";
    }

    /// <summary>
    /// Follow-teleport: when a TRUSTED party member who was in our zone changes territory,
    /// teleport to an unlocked aetheryte in their new zone after a short randomized delay.
    /// Party-only by construction (we watch the party list), scans at 1 Hz.
    /// </summary>
    private void UpdateFollowTeleport(DateTime now)
    {
        if (!_config.FollowTeleportEnabled)
        {
            _partyTerritories.Clear();
            _pendingTeleportAtUtc = null;
            _followStatus = "disabled";
            return;
        }

        // Execute a scheduled follow (checked every frame — cheap).
        if (_pendingTeleportAtUtc != null && now >= _pendingTeleportAtUtc)
        {
            _pendingTeleportAtUtc = null;
            var ok = TeleportHelper.TryTeleport(_pendingAetheryteId, _pendingAetheryteSubIndex);
            _followStatus = ok ? "teleport cast" : "teleport FAILED (combat/unavailable?)";
            _log.Info("Follow teleport: {0} (aetheryte {1})", _followStatus, _pendingAetheryteId);
        }

        if (now - _lastFollowScanUtc < TimeSpan.FromSeconds(1))
            return;
        _lastFollowScanUtc = now;

        var local = _objectTable.LocalPlayer;
        var localName = local?.Name.TextValue ?? string.Empty;
        if (local == null || _partyList.Length == 0)
        {
            _partyTerritories.Clear();
            _followStatus = "no party";
            return;
        }

        var localTerritory = _clientState.TerritoryType;
        var trusted = BuildTrustedNames(localName);

        foreach (var member in _partyList)
        {
            var name = member.Name.TextValue;
            if (name.Length == 0 || name.Equals(localName, StringComparison.OrdinalIgnoreCase))
                continue;

            var territory = member.Territory.RowId;
            var previous = _partyTerritories.GetValueOrDefault(name);
            _partyTerritories[name] = territory;

            // Follow only a trusted member who just LEFT OUR zone for another one.
            // Stand down when we just accepted a native teleport offer — we're already going.
            if (previous == 0
                || territory == previous
                || previous != localTerritory
                || territory == localTerritory
                || !trusted.Contains(name)
                || _pendingTeleportAtUtc != null
                || now - _teleportOffer.LastAcceptUtc < TimeSpan.FromSeconds(15))
                continue;

            var aetheryte = FindUnlockedAetheryteIn(territory);
            if (aetheryte == null)
            {
                _followStatus = $"{name} zoned — no unlocked aetheryte there";
                continue;
            }

            // Small random stagger so 7 toons don't all start casting on the same server tick.
            var delay = TimeSpan.FromSeconds(0.8 + _followJitter.NextDouble() * 1.7);
            _pendingAetheryteId = aetheryte.Value.Id;
            _pendingAetheryteSubIndex = aetheryte.Value.SubIndex;
            _pendingTeleportAtUtc = now + delay;
            _followStatus = $"following {name} in {delay.TotalSeconds:F1}s";
            _log.Info("Follow teleport: {0} zoned to territory {1} — teleporting in {2:F1}s",
                name, territory, delay.TotalSeconds);
        }
    }

    private (uint Id, byte SubIndex)? FindUnlockedAetheryteIn(uint territoryId)
    {
        foreach (var entry in _aetheryteList)
        {
            if (entry.TerritoryId == territoryId)
                return (entry.AetheryteId, entry.SubIndex);
        }

        return null;
    }

    private void UpdatePillionSession(DateTime now)
    {
        var snapshot = _cachedLocalMount;

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

    /// <summary>Live raw seat view for the debug section — reads the tick-cached snapshot, never rescans.</summary>
    private IReadOnlyList<(int Seat, uint EntityId, string Name)> ReadRawSeatOccupancy()
    {
        var snapshot = _cachedLocalMount;
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

        if (_cachedLocalMount != null)
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
                || System.Numerics.Vector3.Distance(local.Position, pc.Position) > 30f
                || !trusted.Contains(pc.Name.TextValue))
                continue;

            // Riders carry the mount id too — only the actual owner (mounted, NOT riding
            // pillion) can be boarded. Without this, once several riders are aboard the scan
            // could return a rider and RidePillion on a rider is a silent no-op.
            if (_mountReader.IsRidingPillion(pc))
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

        // Observation-based pick, then an owner command (LAN relay) overrides it while fresh.
        var pickerSeat = PassengerSeatPicker.PickSeat(localName, presentCandidates, occupied);
        var seat = SeatCommandResolver.Resolve(_seatCommand, ownerName, now, occupied, pickerSeat);
        if (seat == null)
        {
            // Not a candidate / no free seat left — stay put but keep the session.
            _boardingStatus = $"owner {ownerName} — no seat for us";
            return;
        }

        // The computed seat legitimately shifts as other riders board — a fresh target
        // deserves a fresh attempt budget, otherwise late boarders give up on stale failures.
        if (seat.Value != _lastAttemptedSeat)
        {
            _lastAttemptedSeat = seat.Value;
            _boardingAttempts = 0;
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
        var distance = HorizontalDistance(local.Position, owner.Position);
        if (distance > BoardingRangeYalms)
        {
            NavToOwner(owner.Position, distance, ownerName, now);
            return;
        }

        StopNavIfOurs();

        if (now - _lastBoardingAttemptUtc < BoardingRetryInterval)
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

            // Cheap geometric/state filters first — name resolution (SeString parse) last.
            if (System.Numerics.Vector3.Distance(ownerPosition, pc.Position) > 30f
                || !_mountReader.CanBoard(pc))
                continue;

            var name = pc.Name.TextValue;
            if (name.Length == 0
                || name.Equals(ownerName, StringComparison.OrdinalIgnoreCase)
                || !trusted.Contains(name)
                || !IsInMyParty(name)) // only party members can ride pillion
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

    /// <summary>XZ-plane distance — the owner sits meters above ground on tall mounts.</summary>
    private static float HorizontalDistance(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
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
        _lastAttemptedSeat = 0;
        _seatCommand = null;
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
    /// Invite transport: publish the assignment on the LAN relay so the passenger's Charon
    /// enters commanded mode, and mirror it onto the roster view for the UI. No-op transport
    /// when Daedalus/LAN is absent — passengers then self-board by observation as before.
    /// </summary>
    private void SendPillionInvite(PillionInvite invite)
    {
        var ownerName = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        if (ownerName.Length == 0)
            return;

        _relay.Publish(RelayClient.PillionChannel,
            PillionRelay.Serialize(ownerName, invite.CharacterName, invite.SeatIndex));

        var toon = _daedalusIpc.GetLanPartyMembers()
            .FirstOrDefault(t => t.CharacterName.Equals(invite.CharacterName, StringComparison.OrdinalIgnoreCase));
        if (toon != null)
            toon.SeatAssignment = invite.SeatIndex;
    }

    /// <summary>Relay dispatch — framework thread (PluginRelayIpc guarantees it).</summary>
    private void OnRelayMessage(string channel, string json)
    {
        if (channel == RelayClient.PillionChannel)
            OnPillionAssignmentReceived(json);
    }

    /// <summary>
    /// Passenger side: an owner assigned OUR character a seat. Stored as a fresh command —
    /// the boarding pipeline (delay, walk-in, live occupancy re-check) still runs; the
    /// command only overrides WHICH seat is taken (see SeatCommandResolver).
    /// </summary>
    private void OnPillionAssignmentReceived(string json)
    {
        var message = PillionRelay.Parse(json);
        var localName = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        if (message == null
            || localName.Length == 0
            || !message.MemberName.Equals(localName, StringComparison.OrdinalIgnoreCase)
            || message.OwnerName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            return;

        _seatCommand = new SeatCommand(message.OwnerName, message.SeatIndex,
            DateTime.UtcNow + SeatCommandLifetime);
        _log.Debug("Seat command received: {0}'s mount, seat {1}", message.OwnerName, message.SeatIndex);
    }

    private void AcceptPendingInvite() => _inviteInterop.AcceptCurrent();
}
