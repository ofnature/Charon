using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Charon.Features.Spawns;

namespace Charon.Services.Game;

/// <summary>
/// Feeds the object table to <see cref="SpawnWatcher"/>: every scan it collects nearby combat
/// NPCs and asks the watcher which ones are new. Read-only — this never targets, moves or
/// attacks anything.
///
/// The known limit, stated here because the UI repeats it: a client cannot distinguish a mob
/// that just SPAWNED from one that merely came into render range. Both arrive in the object
/// table the same way. Announcing each entity id once per zone is what keeps that honest.
/// </summary>
public sealed class SpawnScanner
{
    private static readonly TimeSpan ScanThrottle = TimeSpan.FromMilliseconds(500);

    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly Func<bool> _enabled;
    private readonly Func<IReadOnlyList<string>> _watchlist;
    private readonly IPluginLog _log;

    private readonly List<NearbyMob> _scratch = new();
    private DateTime _lastScanUtc = DateTime.MinValue;
    private ushort _lastTerritory;

    public SpawnScanner(IObjectTable objectTable, IClientState clientState, Func<bool> enabled,
        Func<IReadOnlyList<string>> watchlist, IPluginLog log)
    {
        _objectTable = objectTable;
        _clientState = clientState;
        _enabled = enabled;
        _watchlist = watchlist;
        _log = log;
    }

    public SpawnWatcher Watcher { get; } = new();

    public string Status { get; private set; } = "off";

    /// <summary>True on the tick a watched mob was first seen — the window opens on this.</summary>
    public bool SightedThisTick { get; private set; }

    public void Update(DateTime nowUtc)
    {
        SightedThisTick = false;

        try
        {
            if (!_enabled())
            {
                Status = "off";
                return;
            }

            var watchlist = _watchlist();
            if (watchlist.Count == 0)
            {
                Status = "watchlist empty — add a mob name";
                return;
            }

            // Entity ids are only meaningful inside a zone, so a change forgets what we logged.
            var territory = (ushort)_clientState.TerritoryType;
            if (territory != _lastTerritory)
            {
                _lastTerritory = territory;
                Watcher.Reset();
            }

            if (nowUtc - _lastScanUtc < ScanThrottle)
                return;
            _lastScanUtc = nowUtc;

            var local = _objectTable.LocalPlayer;
            if (local == null)
            {
                Status = "waiting for the player";
                return;
            }

            _scratch.Clear();
            foreach (var obj in _objectTable)
            {
                if (obj.ObjectKind != ObjectKind.BattleNpc)
                    continue;

                var name = obj.Name.TextValue;
                if (name.Length == 0)
                    continue;

                _scratch.Add(new NearbyMob(obj.EntityId, name, Vector3.Distance(local.Position, obj.Position)));
            }

            var fresh = Watcher.Observe(_scratch, watchlist, nowUtc, territory);
            if (fresh.Count > 0)
            {
                SightedThisTick = true;
                foreach (var sighting in fresh)
                    _log.Info("Spawn watch: {0} appeared {1:F0}y away", sighting.Name, sighting.Distance);
            }

            Status = $"watching {watchlist.Count} name(s) · {_scratch.Count} mobs nearby · "
                     + $"{Watcher.History.Count} logged";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Spawn scanner threw");
            Status = "threw (see log)";
        }
    }

    /// <summary>Distinct names of combat NPCs around us, for the watchlist's add-from-nearby list.</summary>
    public IReadOnlyList<string> NearbyNames()
    {
        try
        {
            return _objectTable
                .Where(o => o.ObjectKind == ObjectKind.BattleNpc && o.Name.TextValue.Length > 0)
                .Select(o => o.Name.TextValue)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
