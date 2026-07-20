using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Charon.Features.AutoAccept;
using Charon.Features.AutoPillion;
using Charon.Features.Follow;
using Charon.Features.GroupManagement;
using Charon.Features.HealWatch;
using Charon.Ipc;
using Charon.Services;
using Charon.Services.Game;
using Charon.Windows;

namespace Charon;

public sealed class CharonPlugin : IDalamudPlugin
{
    public const string PluginVersion = "0.1.6";
    private const string CommandName = "/charon";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IPartyList _partyList;
    private readonly IClientState _clientState;
    private readonly IAetheryteList _aetheryteList;
    private readonly ICondition _condition;
    private readonly IPluginLog _log;

    private readonly CharonConfig _config;
    private readonly WhitelistService _whitelist;
    private readonly DaedalusIpcClient _daedalusIpc;
    private readonly RelayClient _relay;
    private readonly PillionManager _pillionManager;
    private readonly GroupInviteManager _inviteManager;
    private readonly GroupInviteInterop _inviteInterop;
    private readonly TeleportOfferInterop _teleportOffer;
    private readonly DutyPopInterop _dutyPop;
    private readonly TradeInterop _trade;
    private readonly MountStateReader _mountReader;
    private readonly NavClient _nav;
    private readonly HealWatchManager _healWatch;
    private readonly HealExecutor _healExecutor;
    private readonly InviteManager _groupInvites;
    private readonly FcChestManager _fcChest;
    private readonly FollowManager _followManager;
    private readonly BossModClient _bossMod;
    private readonly InteractHelper _interact;

    // Fleet Follow: our own nav-path bookkeeping (separate from the pillion path state).
    private bool _followNavIssued;
    private DateTime _lastFollowNavUtc = DateTime.MinValue;
    private string _followFleetStatus = "idle";

    // Reachability cache — the navmesh query flood-fills, so it runs on a throttle rather than
    // every frame, and is forced immediately when the leader teleports (portal/lift).
    private bool _leaderReachable = true;
    private DateTime _lastReachabilityCheckUtc = DateTime.MinValue;
    private static readonly TimeSpan ReachabilityCheckInterval = TimeSpan.FromSeconds(1.5);

    // Portal taking: when the leader ports between raid arenas, walk to the object they used
    // and click it ourselves rather than stranding the follower.
    private DateTime _lastPortalAttemptUtc = DateTime.MinValue;
    private int _portalAttempts;
    private const int MaxPortalAttempts = 6;
    private const float PortalSearchRadius = 12f;   // how far from the leader's pre-jump spot to look
    private const float PortalInteractRange = 4.5f; // game interact range

    // Heal Watch runs at 1 Hz; status surfaced in the window.
    private DateTime _lastHealScanUtc = DateTime.MinValue;
    private string _healStatus = "idle";

    private readonly WindowSystem _windowSystem = new("Charon");
    private readonly MainWindow _mainWindow;
    private readonly FcChestWindow _fcChestWindow;
    private readonly IAddonLifecycle _addonLifecycle;
    private const string FcChestAddonName = "FreeCompanyChest";

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
        ICondition condition,
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
        _condition = condition;
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
        _groupInvites = new InviteManager(
            toon =>
            {
                var ok = PartyInviteHelper.TryInvite(toon.CharacterName, toon.ContentId, toon.WorldId, out var detail);
                return (ok, detail);
            },
            log: message => _log.Debug("[GroupMgmt] {0}", message));
        _fcChest = new FcChestManager(gameGui, dataManager, log);
        _bossMod = new BossModClient(pluginInterface);
        _interact = new InteractHelper(log);
        _followManager = new FollowManager(FollowConfig.From(_config));
        if (_config.FollowLeaderName.Length > 0)
            _followManager.StartFollowing(_config.FollowLeaderName); // resume a follow interrupted by reload
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
        _dutyPop = new DutyPopInterop(addonLifecycle, gameGui, ShouldAutoCommenceDuty, log);
        _trade = new TradeInterop(gameGui, () => _config.AutoTradeEnabled, IsTrustedToon, log);

        _mainWindow = new MainWindow(_config, SaveConfig, _whitelist, _daedalusIpc, _pillionManager, _inviteManager,
            _healWatch, _groupInvites, _fcChest, _followManager, ReadRawSeatOccupancy, () => _boardingStatus,
            () => $"{_followStatus} · offer: {_teleportOffer.Status}",
            () => _healStatus,
            () => _followFleetStatus,
            () => _partyList.Length,
            IsInMyParty,
            () => _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty,
            new MainWindow.FollowCommands(CommandFollow, CommandStopFollow, CommandFollowAll, CommandStopFollowAll));
        _mainWindow.IsOpen = _config.MainWindowVisible;
        _windowSystem.AddWindow(_mainWindow);

        _fcChestWindow = new FcChestWindow(_config, SaveConfig, _fcChest);
        _windowSystem.AddWindow(_fcChestWindow);

        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Charon window. /charon follow <name> to follow a toon, /charon follow stop to stop.",
        });

        // Auto-open the FC chest window when the game's Free Company chest opens (and close it
        // when the chest closes). The chest is the same FreeCompanyChest addon FcChestManager
        // gates on — reuse the injected addon lifecycle.
        _addonLifecycle = addonLifecycle;
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, FcChestAddonName, OnFcChestOpen);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, FcChestAddonName, OnFcChestClose);

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

        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, FcChestAddonName, OnFcChestOpen);
        _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, FcChestAddonName, OnFcChestClose);

        _commandManager.RemoveHandler(CommandName);
        _windowSystem.RemoveAllWindows();

        _relay.OnMessage -= OnRelayMessage;
        _dutyPop.Dispose();
        _teleportOffer.Dispose();
        _inviteInterop.Dispose();
        _relay.Dispose();
        _daedalusIpc.Dispose();

        _config.MainWindowVisible = _mainWindow.IsOpen;
        SaveConfig();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();

        // "/charon follow <name>" drives the LOCAL box directly (no relay) — handy for testing.
        // "/charon follow stop" (or unfollow) clears it. Bare "/charon" toggles the window.
        if (trimmed.StartsWith("follow", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[6..].Trim();
            if (rest.Length == 0 || rest.Equals("stop", StringComparison.OrdinalIgnoreCase)
                                 || rest.Equals("off", StringComparison.OrdinalIgnoreCase))
                StopLocalFollow();
            else
                StartLocalFollow(rest);
            return;
        }

        if (trimmed.Equals("unfollow", StringComparison.OrdinalIgnoreCase))
        {
            StopLocalFollow();
            return;
        }

        _mainWindow.IsOpen = !_mainWindow.IsOpen;
        _config.MainWindowVisible = _mainWindow.IsOpen;
        SaveConfig();
    }

    private void OpenMainWindow() => _mainWindow.IsOpen = true;

    /// <summary>FC chest opened — pop the standalone window (unless auto-open is disabled).</summary>
    private void OnFcChestOpen(AddonEvent type, AddonArgs args)
    {
        if (_config.FcChestWindowAutoOpen)
            _fcChestWindow.IsOpen = true;
    }

    /// <summary>FC chest closed — the transfer session is gone, so close the window with it.</summary>
    private void OnFcChestClose(AddonEvent type, AddonArgs args)
    {
        _fcChestWindow.IsOpen = false;
    }

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
        _dutyPop.Update(now);
        _trade.Update(now);
        UpdateFollowTeleport(now);
        UpdateFleetFollow(now);
        UpdateHealWatch(now);
        _groupInvites.Update(now);
        _fcChest.Update(now);
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

    /// <summary>
    /// Fleet Follow (receiver side): trail the commanded leader via vnavmesh. Every gate is
    /// re-checked each tick, so a paused follower resumes when the gate clears — no reissue.
    /// On ANY non-Move outcome we actively stop our path, so movement is released cleanly the
    /// instant a boss fight engages (BMR then owns movement).
    /// </summary>
    private void UpdateFleetFollow(DateTime now)
    {
        _followManager.UpdateConfig(FollowConfig.From(_config));

        if (!_followManager.Following)
        {
            _followFleetStatus = "idle";
            StopFollowNavIfOurs();
            return;
        }

        var local = _objectTable.LocalPlayer;
        if (local == null)
        {
            _followFleetStatus = "no player";
            StopFollowNavIfOurs();
            return;
        }

        var leader = FindPlayerByName(_followManager.LeaderName);
        System.Numerics.Vector3? leaderPos = leader?.Position;

        // A big one-tick position jump = portal / teleport stone / lift, not walking. Re-verify
        // reachability at once rather than pathing at a spot we may not be able to walk to.
        var teleported = _followManager.NoteLeaderPosition(leaderPos);
        if (teleported)
            _log.Debug("Follow: {0} jumped position (portal?) — re-checking reachability", _followManager.LeaderName);

        UpdateLeaderReachability(leaderPos, teleported, now);

        // Yield movement to the game/other features: dead, cutscene/zoning, being carried, or
        // an active pillion boarding session (which drives the shared vnav path itself).
        var localBusy = local.IsDead
                        || _condition[ConditionFlag.BetweenAreas]
                        || _condition[ConditionFlag.OccupiedInCutSceneEvent]
                        || _condition[ConditionFlag.WatchingCutscene]
                        || _mountReader.IsLocalRidingPillion()
                        || _boardingOwnerEntityId != 0;

        var decision = _followManager.Evaluate(
            leaderPos, local.Position,
            _condition[ConditionFlag.InCombat], _bossMod.HasActiveModule, localBusy, _leaderReachable);
        _followFleetStatus = decision.Status;

        // Leader ported somewhere we can't walk (raid arena transition): take the same portal
        // instead of stranding here. Only in exactly that state, and only near where they stood.
        if (decision.Action == FollowAction.Hold && !_leaderReachable
            && _config.FollowTakePortals && _followManager.PortalHint != null)
        {
            if (TryTakeLeaderPortal(local.Position, _followManager.PortalHint.Value, now))
                return; // driving toward / clicking the portal this tick
        }

        if (decision.Action == FollowAction.Move)
            FollowNavTo(decision.Target, now);
        else
            StopFollowNavIfOurs(); // Idle or Hold — release the path (the boss-fight handoff)
    }

    /// <summary>
    /// Take the portal/lift the leader just used. The hint is where they STOOD before jumping —
    /// they walked to the thing and clicked it — so we look for an interactable right there
    /// rather than guessing at whatever object happens to be near us. Walks over if needed,
    /// then interacts. Returns true while it is driving this (caller skips normal follow).
    /// </summary>
    private bool TryTakeLeaderPortal(System.Numerics.Vector3 selfPos, System.Numerics.Vector3 portalHint, DateTime now)
    {
        if (_portalAttempts >= MaxPortalAttempts)
        {
            _followFleetStatus = $"waiting — {_followManager.LeaderName} ported; couldn't use the portal";
            return false;
        }

        var portal = FindInteractableNear(portalHint, PortalSearchRadius);
        if (portal == null)
        {
            _followFleetStatus = $"waiting — {_followManager.LeaderName} ported (no portal found here)";
            return false;
        }

        var distance = System.Numerics.Vector3.Distance(selfPos, portal.Position);
        if (distance > PortalInteractRange)
        {
            // Walk to it first — the portal itself is reachable even though the leader isn't.
            FollowNavTo(portal.Position, now);
            _followFleetStatus = $"taking {_followManager.LeaderName}'s portal ({distance:F1}y)";
            return true;
        }

        StopFollowNavIfOurs();

        if (now - _lastPortalAttemptUtc < TimeSpan.FromSeconds(2.5))
            return true; // let the previous click resolve

        _lastPortalAttemptUtc = now;
        _portalAttempts++;
        var clicked = _interact.TryInteract(portal);
        _followFleetStatus = clicked
            ? $"clicked {portal.Name.TextValue} (attempt {_portalAttempts})"
            : $"portal click FAILED (attempt {_portalAttempts})";
        _log.Info("Follow portal: {0}", _followFleetStatus);
        return true;
    }

    /// <summary>Nearest targetable EventObj to a point — raid portals/lifts are EventObjs.</summary>
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindInteractableNear(
        System.Numerics.Vector3 point, float radius)
    {
        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDistance = radius;

        foreach (var obj in _objectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj || !obj.IsTargetable)
                continue;

            var distance = System.Numerics.Vector3.Distance(point, obj.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = obj;
            }
        }

        return best;
    }

    /// <summary>
    /// Refresh the cached "can we actually walk to the leader" answer — throttled because the
    /// navmesh query flood-fills, but forced the moment the leader teleports. Disabled by
    /// config or with vnavmesh absent, we assume reachable (fail-open).
    /// </summary>
    private void UpdateLeaderReachability(System.Numerics.Vector3? leaderPos, bool forceNow, DateTime now)
    {
        if (!_config.FollowReachabilityCheck || leaderPos == null || !_nav.IsAvailable)
        {
            _leaderReachable = true;
            return;
        }

        if (!forceNow && now - _lastReachabilityCheckUtc < ReachabilityCheckInterval)
            return;

        _lastReachabilityCheckUtc = now;
        var reachable = _nav.IsReachable(leaderPos.Value);
        if (reachable != _leaderReachable)
            _log.Debug("Follow: {0} is now {1}", _followManager.LeaderName, reachable ? "reachable" : "UNREACHABLE");

        // Back together (we took the portal, or they came back) — retire the portal episode.
        if (reachable)
        {
            _followManager.ClearPortalHint();
            _portalAttempts = 0;
        }

        _leaderReachable = reachable;
    }

    private void FollowNavTo(System.Numerics.Vector3 target, DateTime now)
    {
        if (!_nav.IsAvailable)
        {
            _followFleetStatus += " — vnavmesh unavailable";
            return;
        }

        // Re-issue as the leader moves: when our path finished/failed, throttled to 0.5s.
        if ((!_followNavIssued || !_nav.IsPathRunning) && now - _lastFollowNavUtc > TimeSpan.FromSeconds(0.5))
        {
            if (_nav.MoveCloseTo(target, _config.FollowDistance))
            {
                _followNavIssued = true;
                _lastFollowNavUtc = now;
            }
        }
    }

    private void StopFollowNavIfOurs()
    {
        if (!_followNavIssued)
            return;

        _followNavIssued = false;
        if (_nav.IsPathRunning)
            _nav.Stop();
    }

    private Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter? FindPlayerByName(string characterName)
    {
        if (string.IsNullOrEmpty(characterName))
            return null;

        foreach (var obj in _objectTable)
        {
            if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc
                && pc.Name.TextValue.Equals(characterName, StringComparison.OrdinalIgnoreCase))
                return pc;
        }

        return null;
    }

    // --- Fleet Follow: sender side (publish commands over the LAN relay) ---

    /// <summary>Tell a toon to follow the local character.</summary>
    private void CommandFollow(string targetName)
    {
        var me = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        if (me.Length == 0 || targetName.Length == 0 || targetName.Equals(me, StringComparison.OrdinalIgnoreCase))
            return;
        _relay.Publish(RelayClient.FollowChannel, FollowRelay.Serialize(me, targetName, FollowRelay.ActStart));
        _log.Debug("Follow command → {0} follow me", targetName);
    }

    /// <summary>Tell every online LAN toon (except us) to follow the local character.</summary>
    private void CommandFollowAll()
    {
        var me = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        if (me.Length == 0)
            return;
        foreach (var toon in _daedalusIpc.GetLanPartyMembers())
        {
            if (toon.IsOnline && !toon.CharacterName.Equals(me, StringComparison.OrdinalIgnoreCase))
                _relay.Publish(RelayClient.FollowChannel, FollowRelay.Serialize(me, toon.CharacterName, FollowRelay.ActStart));
        }
    }

    private void CommandStopFollow(string targetName)
    {
        if (targetName.Length == 0)
            return;
        _relay.Publish(RelayClient.FollowChannel, FollowRelay.Serialize(string.Empty, targetName, FollowRelay.ActStop));
    }

    /// <summary>Stop every online LAN toon, and stop the local follower too.</summary>
    private void CommandStopFollowAll()
    {
        foreach (var toon in _daedalusIpc.GetLanPartyMembers())
        {
            if (toon.IsOnline)
                _relay.Publish(RelayClient.FollowChannel, FollowRelay.Serialize(string.Empty, toon.CharacterName, FollowRelay.ActStop));
        }
        StopLocalFollow();
    }

    /// <summary>Start/stop the LOCAL follower directly (relay receive + /charon follow command).</summary>
    private void StartLocalFollow(string leaderName)
    {
        _followManager.StartFollowing(leaderName);
        _config.FollowLeaderName = _followManager.LeaderName;
        SaveConfig();
    }

    private void StopLocalFollow()
    {
        _followManager.Stop();
        _config.FollowLeaderName = string.Empty;
        SaveConfig();
        StopFollowNavIfOurs();
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

    /// <summary>
    /// Duty-pop gate: commence only when we're in a real party and every other member is a
    /// trusted LAN toon (our fleet queueing together). A matched/roulette pop arrives while
    /// solo, and any stranger in the party vetoes it — both are left for the player.
    /// </summary>
    private bool ShouldAutoCommenceDuty()
    {
        try
        {
            var localName = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
            var trusted = BuildTrustedNames(localName);

            var members = new List<string>();
            foreach (var member in _partyList)
                members.Add(member.Name.TextValue);

            return DutyAcceptPolicy.ShouldAutoCommence(
                _config.AutoCommenceDutyEnabled, members, localName, trusted.Contains);
        }
        catch
        {
            return false; // unreadable party — never auto-commence on a guess
        }
    }

    /// <summary>Trust gate shared by the trade mirror: LAN roster (+ manual whitelist per config).</summary>
    private bool IsTrustedToon(string characterName)
    {
        if (characterName.Length == 0)
            return false;

        var localName = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        return BuildTrustedNames(localName).Contains(characterName)
               && !characterName.Equals(localName, StringComparison.OrdinalIgnoreCase);
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
        else if (channel == RelayClient.FollowChannel)
            OnFollowCommandReceived(json);
    }

    /// <summary>Receiver side: a follow command addressed to our character starts/stops following.</summary>
    private void OnFollowCommandReceived(string json)
    {
        var message = FollowRelay.Parse(json);
        var me = _objectTable.LocalPlayer?.Name.TextValue ?? string.Empty;
        if (message == null || me.Length == 0 || !message.Target.Equals(me, StringComparison.OrdinalIgnoreCase))
            return;

        if (message.Act == FollowRelay.ActStop)
        {
            StopLocalFollow();
        }
        else if (!message.Leader.Equals(me, StringComparison.OrdinalIgnoreCase))
        {
            StartLocalFollow(message.Leader);
            _log.Debug("Now following {0} (relay command)", message.Leader);
        }
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
