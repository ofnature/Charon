using System;
using System.Collections.Generic;
using System.Numerics;
using Charon.Features.Tweaks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Charon.Services.Game;

/// <summary>
/// Hides other players beyond a chosen distance, for the character it is switched on for.
///
/// The game has no draw distance for players, only a limit on how many are shown. What it does
/// have is a pair of flags on every object (ClientStructs <c>GameObject.RenderFlags</c>: Model = 2,
/// Nameplate = 0x800) that stop it drawing that object — the flags the Visibility plugin sets
/// (GPL-3.0, so the fact only). The player stays in the object table, so follow and Heal Watch
/// are unaffected; only the picture changes. Whether a hidden player can still be targeted is unseen.
///
/// Never hidden: this character, its party and alliance, friends, the Daedalus fleet, and whoever
/// is targeted or focus-targeted. Off in PvP, and paused in cutscenes and group pose, where the
/// game decides who is visible. Every flag Charon set is taken off again when the player comes
/// back in range, when the setting is turned off, and on unload — and only the flags Charon set,
/// so a player the game itself hid stays hidden.
/// </summary>
public sealed unsafe class PlayerDrawLimiter : IDisposable
{
    private static readonly TimeSpan ScanEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>The object table's placeholder id for an object with none (cutscene copies).</summary>
    private const uint NoEntityId = 0xE0000000;

    private const StatusFlags AlwaysShown = StatusFlags.PartyMember | StatusFlags.AllianceMember | StatusFlags.Friend;

    private readonly IObjectTable _objectTable;
    private readonly ITargetManager _targets;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly Func<CharonConfig.PlayerRangeSetting> _setting;
    private readonly Func<IReadOnlyCollection<uint>> _fleetEntityIds;
    private readonly IPluginLog _log;

    /// <summary>Entity id -> the flags Charon set on that player (may be none, if the game had them set already).</summary>
    private readonly Dictionary<uint, VisibilityFlags> _hidden = new();
    private readonly HashSet<uint> _seen = new();
    private readonly HashSet<uint> _fleet = new();
    private readonly List<uint> _gone = new();

    private DateTime _nextScanUtc = DateTime.MinValue;
    private bool _failureLogged;

    public string Status { get; private set; } = "off";

    public PlayerDrawLimiter(
        IObjectTable objectTable,
        ITargetManager targets,
        IClientState clientState,
        ICondition condition,
        Func<CharonConfig.PlayerRangeSetting> setting,
        Func<IReadOnlyCollection<uint>> fleetEntityIds,
        IPluginLog log)
    {
        _objectTable = objectTable;
        _targets = targets;
        _clientState = clientState;
        _condition = condition;
        _setting = setting;
        _fleetEntityIds = fleetEntityIds;
        _log = log;
    }

    public void Update(DateTime utcNow)
    {
        if (utcNow < _nextScanUtc)
            return;
        _nextScanUtc = utcNow + ScanEvery;

        try
        {
            Scan();
        }
        catch (Exception ex)
        {
            Status = $"failed: {ex.Message}";
            if (_failureLogged)
                return;

            _failureLogged = true;
            _log.Warning(ex, "Player range: the scan failed");
        }
    }

    private void Scan()
    {
        var local = _objectTable.LocalPlayer;
        if (local == null)
        {
            _hidden.Clear(); // the objects are gone with the zone or the login
            Status = "not logged in";
            return;
        }

        var setting = _setting();
        if (!setting.Enabled)
        {
            RestoreAll();
            Status = "off";
            return;
        }

        if (_clientState.IsPvP)
        {
            RestoreAll();
            Status = "off in PvP";
            return;
        }

        if (_clientState.IsGPosing
            || _condition[ConditionFlag.OccupiedInCutSceneEvent]
            || _condition[ConditionFlag.WatchingCutscene]
            || _condition[ConditionFlag.WatchingCutscene78])
        {
            RestoreAll();
            Status = "paused - the game decides who is visible in a cutscene or group pose";
            return;
        }

        var range = PlayerDrawRange.Clamp(setting.Range);
        var wanted = VisibilityFlags.Model | (setting.HideNameplates ? VisibilityFlags.Nameplate : VisibilityFlags.None);

        _fleet.Clear();
        foreach (var id in _fleetEntityIds())
            _fleet.Add(id);

        var target = _targets.Target?.EntityId ?? 0;
        var focus = _targets.FocusTarget?.EntityId ?? 0;

        _seen.Clear();
        int others = 0, hidden = 0, exemptCount = 0;
        foreach (var obj in _objectTable)
        {
            if (obj is not IPlayerCharacter pc || pc.EntityId == local.EntityId || pc.EntityId == NoEntityId || pc.Address == 0)
                continue;

            var id = pc.EntityId;
            if (!_seen.Add(id))
                continue;

            others++;
            var exempt = (pc.StatusFlags & AlwaysShown) != 0 || _fleet.Contains(id) || id == target || id == focus;
            if (exempt)
                exemptCount++;

            var hiddenNow = _hidden.TryGetValue(id, out var ours);
            var gameObject = (GameObject*)pc.Address;
            if (PlayerDrawRange.ShouldHide(Vector3.Distance(local.Position, pc.Position), range, hiddenNow, exempt))
            {
                var release = ours & ~wanted; // the nameplate option was turned off
                if (release != VisibilityFlags.None)
                {
                    gameObject->RenderFlags &= ~release;
                    ours &= ~release;
                }

                var add = wanted & ~gameObject->RenderFlags;
                if (add != VisibilityFlags.None)
                {
                    gameObject->RenderFlags |= add;
                    ours |= add;
                }

                _hidden[id] = ours;
                hidden++;
            }
            else if (hiddenNow)
            {
                gameObject->RenderFlags &= ~ours;
                _hidden.Remove(id);
            }
        }

        // A player who left the table took the flags with them: forget the entry.
        _gone.Clear();
        foreach (var id in _hidden.Keys)
        {
            if (!_seen.Contains(id))
                _gone.Add(id);
        }

        foreach (var id in _gone)
            _hidden.Remove(id);

        Status = others == 0
            ? $"{range:0}y - no other players here"
            : $"{range:0}y - hiding {hidden} of {others} other players ({exemptCount} always shown)";
    }

    /// <summary>Takes Charon's flags off every player it hid.</summary>
    private void RestoreAll()
    {
        if (_hidden.Count == 0)
            return;

        foreach (var obj in _objectTable)
        {
            if (obj is not IPlayerCharacter pc || pc.Address == 0 || !_hidden.TryGetValue(pc.EntityId, out var ours))
                continue;

            ((GameObject*)pc.Address)->RenderFlags &= ~ours;
        }

        _hidden.Clear();
    }

    public void Dispose()
    {
        try
        {
            RestoreAll();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Player range: could not put players back on unload");
        }
    }
}
