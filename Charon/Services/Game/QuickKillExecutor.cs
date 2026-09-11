using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Charon.Features.PowerLevel;

namespace Charon.Services.Game;

/// <summary>
/// Quick Kill's adapter: reads which mobs are fighting a friendly and acts on
/// <see cref="QuickKillPolicy"/>'s choice, in the role this box plays.
///
/// "Engaged" = a live combatant that is IN COMBAT and currently TARGETING someone friendly: our
/// party OR any fleet toon on the LAN roster (for player characters the object id is the entity
/// id, so the target id compares straight against both). Nothing else is ever offered, so Quick
/// Kill cannot pull. The fleet half matters most for KILL: the carry usually stands OUTSIDE the
/// party it is levelling, so a party-only test would see none of their mobs.
///
/// KILL aims — it sets this toon's target — and fires ONE ranged opener only while this toon is
/// out of combat, because rotations only fire once their own toon is in combat; after that the
/// rotation owns every action. TAG fires one ranged hit per mob. Every shot asks the game first
/// (<c>GetActionStatus</c> with the target decides level, range, line of sight and cooldown), with
/// the base id run through <c>GetAdjustedActionId</c> so an upgraded form (Stone to Glare) is what
/// actually fires. A mob counts as tagged once it is on OUR enmity list (<c>UIState.Hater</c>, the
/// list the game's own enmity display reads) — the game's record of the hit, not a guess.
/// </summary>
public sealed unsafe class QuickKillExecutor
{
    private const int ModeKill = 0;

    private static readonly TimeSpan ScanThrottle = TimeSpan.FromMilliseconds(250);

    /// <summary>Longer than any tag's cast (2s) plus travel: by then the enmity list has spoken.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(4);

    /// <summary>Movement per scan that counts as moving — a cast would fail.</summary>
    private const float MovingYalms = 0.1f;

    /// <summary>KILL reach for a job with no ranged tag — ranged weapons reach 25y.</summary>
    private const float DefaultKillReach = 25f;

    private readonly IObjectTable _objectTable;
    private readonly IPartyList _partyList;
    private readonly ITargetManager _targets;
    private readonly Func<bool> _enabled;
    private readonly Func<int> _mode;
    private readonly Func<bool> _rotationActive;
    private readonly Func<bool> _inCombat;
    private readonly Func<IReadOnlyCollection<uint>> _fleetEntityIds;
    private readonly IPluginLog _log;

    private readonly Dictionary<uint, DateTime> _tried = new();
    private readonly List<TagCandidate> _candidates = new();
    private readonly HashSet<ulong> _friendlyIds = new();
    private readonly HashSet<uint> _tagged = new();
    private DateTime _lastScanUtc = DateTime.MinValue;
    private Vector3 _lastPosition;

    public QuickKillExecutor(IObjectTable objectTable, IPartyList partyList, ITargetManager targets,
        Func<bool> enabled, Func<int> mode, Func<bool> rotationActive, Func<bool> inCombat,
        Func<IReadOnlyCollection<uint>> fleetEntityIds, IPluginLog log)
    {
        _objectTable = objectTable;
        _partyList = partyList;
        _targets = targets;
        _enabled = enabled;
        _mode = mode;
        _rotationActive = rotationActive;
        _inCombat = inCombat;
        _fleetEntityIds = fleetEntityIds;
        _log = log;
    }

    public string Status { get; private set; } = "off";

    /// <summary>What this job tags with right now — shown even while Quick Kill is off.</summary>
    public string TagDescription { get; private set; } = "—";

    public void Update(DateTime nowUtc)
    {
        if (nowUtc - _lastScanUtc < ScanThrottle)
            return;
        _lastScanUtc = nowUtc;

        try
        {
            var local = _objectTable.LocalPlayer;
            if (local == null)
            {
                Status = "waiting for the player";
                return;
            }

            var moving = Vector3.Distance(local.Position, _lastPosition) > MovingYalms;
            _lastPosition = local.Position;

            var jobId = local.ClassJob.RowId;
            var tag = TagActionTable.Get(jobId, local.Level);
            TagDescription = DescribeTag(jobId, tag);

            if (!_enabled())
            {
                Status = "off";
                return;
            }

            _friendlyIds.Clear();
            foreach (var member in _partyList)
                _friendlyIds.Add(member.EntityId);
            var fleetOutsideParty = 0;
            foreach (var id in _fleetEntityIds())
            {
                if (id != 0 && id != local.EntityId && _friendlyIds.Add(id))
                    fleetOutsideParty++;
            }

            _friendlyIds.Add(local.GameObjectId);
            var hasCarry = _partyList.Length > 1 || fleetOutsideParty > 0;

            ReadTagged();
            CollectCandidates(local.Position, nowUtc);

            if (_mode() == ModeKill)
            {
                Aim(TagActionTable.ForJob(jobId)?.Range ?? DefaultKillReach, hasCarry, _friendlyIds.Count - 1,
                    tag, _inCombat(), nowUtc);
            }
            else
            {
                TagOnce(tag, hasCarry, local.IsCasting, moving, nowUtc);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Quick Kill threw");
            Status = "threw (see log)";
        }
    }

    /// <summary>
    /// KILL: put the chosen mob under the crosshair, and if this toon is not in combat yet, open on
    /// it once so the rotation can start. The status names how many friendly toons it is watching —
    /// 0 would mean the LAN roster handed over no entity ids.
    /// </summary>
    private void Aim(float reach, bool hasCarry, int watching, TagAction? opener, bool inCombat, DateTime nowUtc)
    {
        var current = _targets.Target?.EntityId ?? 0;
        var decision = QuickKillPolicy.DecideKill(hasCarry, reach, current, _candidates);
        var watchingText = $"(watching {watching} toon{(watching == 1 ? "" : "s")})";
        Status = $"{decision.Reason} {watchingText}";
        if (!decision.HasTarget)
            return;

        var target = _objectTable.SearchByEntityId(decision.TargetEntityId);
        if (target == null)
        {
            Status = "target vanished";
            return;
        }

        if (decision.TargetEntityId != current)
        {
            _targets.Target = target;
            _log.Debug("Quick Kill: targeting {0}", decision.TargetName);
        }

        // Rotations only fire once THEIR toon is in combat — every Daedalus damage module returns
        // early otherwise, and its BaseRotation reads ConditionFlag.InCombat, the very flag passed
        // in here (both verified in its source), so the two can never disagree about the fight. A
        // carry standing outside the party is never hit, so it would never enter combat and would
        // aim at the mob forever (seen live: "killing Grenade" and not a single shot). One opener on
        // the engaged mob starts the fight; from then on the rotation owns the action queue.
        if (inCombat)
            return;

        if (opener == null)
        {
            Status = $"{decision.Reason} — out of combat, and this job has no ranged opener {watchingText}";
            return;
        }

        if (TryFire(opener, target, decision.TargetEntityId, nowUtc, out var refusal))
            Status = $"opened on {decision.TargetName} with {opener.Name} — the rotation takes it from here";
        else if (refusal != null)
            Status = refusal;
    }

    /// <summary>TAG: one ranged hit on the chosen mob.</summary>
    private void TagOnce(TagAction? tag, bool hasCarry, bool casting, bool moving, DateTime nowUtc)
    {
        var decision = QuickKillPolicy.DecideTag(_rotationActive(), hasCarry, tag, casting, moving, _candidates);
        if (!decision.HasTarget)
        {
            Status = decision.Reason;
            return;
        }

        var target = _objectTable.SearchByEntityId(decision.TargetEntityId);
        if (target == null)
        {
            Status = "target vanished";
            return;
        }

        if (TryFire(tag!, target, decision.TargetEntityId, nowUtc, out var refusal))
        {
            Status = $"tagged {decision.TargetName} with {tag!.Name}";
            _log.Debug("Quick Kill: {0} on {1}", tag.Name, decision.TargetName);
        }
        else
        {
            Status = refusal ?? decision.Reason;
        }
    }

    /// <summary>
    /// One hit on the target, the game asked first. A mob fired at is left alone for a few seconds
    /// either way, so a refusal can never pin the loop; <paramref name="refusal"/> is null when the
    /// shot was merely held back because one went out moments ago.
    /// </summary>
    private bool TryFire(TagAction action, IGameObject target, uint entityId, DateTime nowUtc, out string? refusal)
    {
        refusal = null;
        if (_tried.TryGetValue(entityId, out var at) && nowUtc - at < RetryAfter)
            return false;

        var manager = ActionManager.Instance();
        if (manager == null)
        {
            refusal = "action manager unavailable";
            return false;
        }

        var actionId = manager->GetAdjustedActionId(action.ActionId);
        _tried[entityId] = nowUtc;

        var status = manager->GetActionStatus(ActionType.Action, actionId, target.GameObjectId);
        if (status != 0)
        {
            refusal = $"game refused {action.Name} on {target.Name.TextValue} (status {status})";
            return false;
        }

        manager->UseAction(ActionType.Action, actionId, target.GameObjectId);
        return true;
    }

    private void ReadTagged()
    {
        _tagged.Clear();
        var ui = UIState.Instance();
        if (ui == null)
            return;

        var haters = ui->Hater.Haters;
        var count = Math.Min(ui->Hater.HaterCount, haters.Length);
        for (var i = 0; i < count; i++)
        {
            if (haters[i].EntityId != 0)
                _tagged.Add(haters[i].EntityId);
        }
    }

    private void CollectCandidates(Vector3 from, DateTime nowUtc)
    {
        _candidates.Clear();
        foreach (var obj in _objectTable)
        {
            if (obj is not IBattleNpc npc
                || npc.SubKind != (byte)BattleNpcSubKind.Combatant
                || npc.CurrentHp == 0
                || !npc.IsTargetable
                || !npc.StatusFlags.HasFlag(StatusFlags.InCombat)
                || !_friendlyIds.Contains(npc.TargetObjectId))
                continue;

            var distance = MathF.Max(0f, Vector3.Distance(from, npc.Position) - npc.HitboxRadius);
            var recentlyTried = _tried.TryGetValue(npc.EntityId, out var at) && nowUtc - at < RetryAfter;
            _candidates.Add(new TagCandidate(npc.EntityId, npc.Name.TextValue, distance,
                _tagged.Contains(npc.EntityId), recentlyTried));
        }

        if (_tried.Count > 64)
            _tried.Clear(); // entries only matter for seconds; a bounded map beats bookkeeping
    }

    private static string DescribeTag(uint jobId, TagAction? tag)
    {
        if (tag != null)
            return $"{tag.Name} ({tag.Range:F0}y, {(tag.IsCast ? "cast — only fires while standing still" : "instant")})";

        var any = TagActionTable.ForJob(jobId);
        return any == null
            ? "none — this job has no ranged attack to tag with"
            : $"{any.Name} unlocks at level {any.Level}";
    }
}
