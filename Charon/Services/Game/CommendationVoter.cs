using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Gives a commendation to a party member when the end-of-duty banner appears. Ported from
/// PandorasBox's AutoVoteMvp (BSD-3-Clause, PunishXIV/PandorasBox) — the banner mechanics are
/// theirs, production-verified: the "BannerMIP" addon lists candidate names in AtkValues 22-29,
/// each name's vote index sits 14 values earlier, and the vote is callback (12, index).
///
/// Selection: priority role first (tank/healer/dps/none), random within it — with Pandora's
/// off-by-one fixed (their Random.Next(count-1) could never pick the last candidate). Members
/// you QUEUED WITH are excluded (the game refuses premade commendations anyway; the premade
/// list is cached when the duty-queue condition flips on). Optionally excludes anyone who died.
/// Never in PvP.
/// </summary>
public sealed unsafe class CommendationVoter : IDisposable
{
    private const string BannerAddon = "BannerMIP";

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IPartyList _partyList;
    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;
    private readonly IChatGui _chat;
    private readonly Func<bool> _enabled;
    private readonly Func<int> _priority;       // 0 tank, 1 healer, 2 dps, 3 none
    private readonly Func<bool> _hideChat;
    private readonly Func<bool> _excludeDeaths;
    private readonly IPluginLog _log;
    private readonly Random _random = new();

    /// <summary>Names queued WITH — the game refuses commendations for them, so they are never
    /// candidates. Cached at queue time; cleared when the duty binding drops.</summary>
    private readonly HashSet<string> _premadeNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Party members seen dead this duty (entity ids), for the exclude-deaths option.</summary>
    private readonly HashSet<uint> _diedThisDuty = new();
    private readonly HashSet<uint> _currentlyDead = new();

    public CommendationVoter(IAddonLifecycle addonLifecycle, IPartyList partyList,
        IObjectTable objectTable, IClientState clientState, ICondition condition, IChatGui chat,
        Func<bool> enabled, Func<int> priority, Func<bool> hideChat, Func<bool> excludeDeaths,
        IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _partyList = partyList;
        _objectTable = objectTable;
        _clientState = clientState;
        _condition = condition;
        _chat = chat;
        _enabled = enabled;
        _priority = priority;
        _hideChat = hideChat;
        _excludeDeaths = excludeDeaths;
        _log = log;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, BannerAddon, OnBanner);
        _condition.ConditionChange += OnConditionChange;
    }

    public string Status { get; private set; } = "idle";

    /// <summary>Track deaths while in a party. Call every framework tick — cheap.</summary>
    public void Update()
    {
        if (!_enabled() || !_excludeDeaths() || _partyList.Length == 0)
            return;

        try
        {
            foreach (var member in _partyList)
            {
                var obj = member.GameObject;
                if (obj == null)
                    continue;

                if (obj.IsDead)
                {
                    if (_currentlyDead.Add(member.EntityId))
                        _diedThisDuty.Add(member.EntityId);
                }
                else
                {
                    _currentlyDead.Remove(member.EntityId);
                }
            }
        }
        catch
        {
            // fail-open — death tracking is an option, never a crash
        }
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        try
        {
            // Queue time is when the premade is knowable: whoever is in the party NOW came with us.
            if (flag == ConditionFlag.WaitingForDuty && value)
            {
                _premadeNames.Clear();
                foreach (var member in _partyList)
                    _premadeNames.Add(member.Name.TextValue);
                _log.Debug("Commend: cached {0} premade name(s) at queue time", _premadeNames.Count);
            }

            if (flag == ConditionFlag.BoundByDuty && !value)
            {
                _premadeNames.Clear();
                _diedThisDuty.Clear();
                _currentlyDead.Clear();
            }
        }
        catch
        {
            // fail-open
        }
    }

    private void OnBanner(AddonEvent type, AddonArgs args)
    {
        if (!_enabled() || _clientState.IsPvP)
            return;

        try
        {
            var target = ChooseTarget();
            if (target == null)
            {
                Status = "banner shown — nobody commendable (premade/dead/empty)";
                return;
            }

            var banner = (AtkUnitBase*)args.Addon.Address;
            for (var i = 22; i <= 29 && i < banner->AtkValuesCount; i++)
            {
                var value = banner->AtkValues[i];
                if (value.Type != AtkValueType.String && value.Type != AtkValueType.ManagedString)
                    continue;

                var name = MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue;
                if (!string.Equals(name, target.Name.TextValue, StringComparison.Ordinal))
                    continue;

                var voteIndex = (int)banner->AtkValues[i - 14].UInt;
                var values = stackalloc AtkValue[2];
                values[0].SetInt(12);
                values[1].SetInt(voteIndex);
                banner->FireCallback(2, values);

                Status = $"commended {name}";
                _log.Info("Commend: {0} (vote index {1})", name, voteIndex);
                if (!_hideChat())
                    _chat.Print($"[Charon] Commendation given to {name}.");
                return;
            }

            Status = $"banner shown — {target.Name.TextValue} not on it";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Commendation vote threw");
            Status = "vote threw (see log)";
        }
    }

    private Dalamud.Game.ClientState.Party.IPartyMember? ChooseTarget()
    {
        var selfId = _objectTable.LocalPlayer?.GameObjectId ?? 0;
        var candidates = _partyList
            .Where(m => m.EntityId != selfId
                        && m.GameObject != null
                        && !_premadeNames.Contains(m.Name.TextValue)
                        && (!_excludeDeaths() || !_diedThisDuty.Contains(m.EntityId)))
            .ToList();

        if (candidates.Count == 0)
            return null;

        var tanks = candidates.Where(m => m.ClassJob.Value.Role == 1).ToList();
        var healers = candidates.Where(m => m.ClassJob.Value.Role == 4).ToList();
        var dps = candidates.Where(m => m.ClassJob.Value.Role is 2 or 3).ToList();

        var ordered = _priority() switch
        {
            0 => new[] { tanks, healers, dps },
            1 => new[] { healers, tanks, dps },
            2 => new[] { dps, tanks, healers },
            _ => new[] { candidates },
        };

        foreach (var pool in ordered)
        {
            if (pool.Count > 0)
                return pool[_random.Next(pool.Count)]; // Next(count), not Pandora's Next(count-1)
        }

        return null;
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, BannerAddon, OnBanner);
        _condition.ConditionChange -= OnConditionChange;
    }
}
