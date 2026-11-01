using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Charon.Features.DeepDungeon;
using Charon.Features.Follow;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>What the follow loop should do after asking <see cref="TrapAwareFollow"/>.</summary>
public enum TrapFollowStep
{
    /// <summary>No revealed trap is near (or not in a deep dungeon): issue the ordinary move.</summary>
    NotNeeded,

    /// <summary>A route is being planned — issue nothing this tick.</summary>
    Planning,

    /// <summary>A route clear of every revealed trap is being walked.</summary>
    Moving,

    /// <summary>No safe way round, or planning failed: issue the ordinary move (the note says why).</summary>
    Fallback,
}

/// <summary>
/// Keeps a deep-dungeon follower off REVEALED traps (Deep Ducking's idea — see <see cref="TrapRoute"/>).
/// Only revealed traps count: they are real objects in the table. Likely-trap spots are history, not
/// fact, and detouring round every one would leave no route at all in a corridor.
///
/// The route is asked of the provider first (<c>Nav.Pathfind</c>); a route that passes a trap is
/// replaced by one that does not — Ariadne plans one itself (<c>Nav.PathfindAvoid</c>), on vnavmesh a
/// step is added beside the trap where the mesh is walkable — and walked with <c>Path.MoveTo</c>.
/// With no safe way round, the follower carries on with the ordinary move and SAYS so: holding would
/// strand it, since the leader has already gone past.
/// </summary>
public sealed class TrapAwareFollow
{
    private const float TrapRange = 60f;
    private const int MaxDetourSteps = 3;
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromSeconds(2);

    private readonly NavClient _nav;
    private readonly IObjectTable _objectTable;
    private readonly Func<bool> _inDeepDungeon;
    private readonly IPluginLog _log;

    private Task<List<Vector3>>? _plan;
    private bool _planAvoids;
    private Vector3 _plannedFor;
    private DateTime _plannedAt;

    public TrapAwareFollow(NavClient nav, IObjectTable objectTable, Func<bool> inDeepDungeon, IPluginLog log)
    {
        _nav = nav;
        _objectTable = objectTable;
        _inDeepDungeon = inDeepDungeon;
        _log = log;
    }

    /// <summary>The last thing this did about traps, for the Follow Debug line; empty when traps played no part.</summary>
    public string Note { get; private set; } = string.Empty;

    public TrapFollowStep Issue(Vector3 me, Vector3 target, float range, DateTime now)
    {
        var traps = _inDeepDungeon() ? RevealedTraps(me) : [];
        if (traps.Count == 0)
        {
            _plan = null;
            Note = string.Empty;
            return TrapFollowStep.NotNeeded;
        }

        // A plan for a target the leader has since left is worthless — start again.
        if (_plan != null && Vector3.Distance(_plannedFor, target) > 5f)
            _plan = null;

        if (_plan == null)
        {
            _plan = _nav.Pathfind(me, target);
            _planAvoids = false;
            _plannedFor = target;
            _plannedAt = now;
            if (_plan == null)
                return Fallback("could not ask for a route — following normally");
            Note = $"planning round {traps.Count} revealed trap(s)";
            return TrapFollowStep.Planning;
        }

        if (!_plan.IsCompleted)
        {
            if (now - _plannedAt <= PlanTimeout)
                return TrapFollowStep.Planning;
            _plan = null;
            return Fallback("route planning timed out — following normally");
        }

        var task = _plan;
        var avoided = _planAvoids;
        _plan = null;
        if (task.IsFaulted || task.IsCanceled || task.Result.Count == 0)
            return Fallback("no route came back — following normally");

        var path = task.Result;
        if (TrapRoute.FirstCrossing(path, traps) is not { } crossing)
            return Walk(path, range, avoided ? "detouring round a trap" : $"route clear of {traps.Count} revealed trap(s)");

        // Ariadne plans the detour itself — once; a second crossing after that is a blocked route.
        if (_nav.CanAvoid && !avoided)
        {
            _plan = _nav.PathfindAvoid(me, target, crossing.Trap, TrapRoute.Clearance + 0.5f);
            _planAvoids = true;
            _plannedAt = now;
            if (_plan == null)
                return Fallback("could not ask for a detour — following normally");
            Note = "planning a detour round a trap";
            return TrapFollowStep.Planning;
        }

        if (!_nav.CanAvoid && Sidestep(path, traps) is { } detour)
            return Walk(detour, range, "detouring round a trap");

        _log.Info("[Follow] no safe way round a revealed trap at {0} — following anyway", crossing.Trap);
        return Fallback($"no safe way round a trap {Vector3.Distance(me, crossing.Trap):0}y away — following anyway");
    }

    /// <summary>vnavmesh: add a walkable step beside each trap the route passes, up to a few; null when blocked.</summary>
    private List<Vector3>? Sidestep(List<Vector3> path, IReadOnlyList<Vector3> traps)
    {
        var route = path;
        for (var i = 0; i < MaxDetourSteps; i++)
        {
            if (TrapRoute.FirstCrossing(route, traps) is not { } crossing)
                return route;

            var (first, second) = TrapRoute.Sidesteps(route[crossing.Leg], route[crossing.Leg + 1], crossing.Trap,
                TrapRoute.Clearance + 1f);
            Vector3? via = _nav.IsReachable(first, 1.5f) ? first : _nav.IsReachable(second, 1.5f) ? second : null;
            if (via == null)
                return null;
            route = TrapRoute.Splice(route, crossing.Leg, via.Value);
        }

        return TrapRoute.FirstCrossing(route, traps) == null ? route : null;
    }

    private TrapFollowStep Walk(List<Vector3> path, float range, string note)
    {
        if (!_nav.MoveAlong(TrapRoute.StopShort(path, range)))
            return Fallback("the provider would not take the route — following normally");
        Note = note;
        return TrapFollowStep.Moving;
    }

    private TrapFollowStep Fallback(string note)
    {
        Note = note;
        return TrapFollowStep.Fallback;
    }

    private List<Vector3> RevealedTraps(Vector3 me) =>
        _objectTable
            .Where(o => DeepDungeonIds.Traps.ContainsKey(o.BaseId) && Vector3.Distance(o.Position, me) <= TrapRange)
            .Select(o => o.Position)
            .ToList();
}
