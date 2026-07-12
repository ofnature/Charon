using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.HealWatch;

/// <summary>Immutable snapshot of the Heal Watch settings, rebuilt from <see cref="CharonConfig"/>.</summary>
public sealed record HealWatchConfig(
    bool Enabled,
    float HealThreshold,
    float EmergencyThreshold,
    bool OutOfPartyOnly,
    bool MaintainHot,
    bool RaiseDead)
{
    public static HealWatchConfig From(CharonConfig config) => new(
        config.HealWatchEnabled,
        config.HealThreshold,
        config.EmergencyThreshold,
        config.HealOutOfPartyOnly,
        config.HealMaintainHot,
        config.HealRaiseDead);
}

/// <summary>One fleet toon as seen by Heal Watch: LAN vitals + local party context.</summary>
public sealed record HealCandidate(string Name, uint EntityId, float RosterHp, bool InParty);

public enum HealKind
{
    /// <summary>Single-target GCD heal.</summary>
    Heal,

    /// <summary>Maintain the HoT/shield (executor checks the live status before casting).</summary>
    Hot,

    /// <summary>Hardcast raise on a dead toon.</summary>
    Raise,
}

/// <summary>A cast the manager wants, most urgent first.</summary>
public sealed record HealIntent(string Name, uint EntityId, HealKind Kind, bool Emergency);

/// <summary>One executed cast, kept for the debug section.</summary>
public sealed record HealLogEntry(DateTime TimeUtc, string Name, HealKind Kind, bool Emergency);

/// <summary>
/// Watches fleet vitals from the Daedalus LAN roster and decides who needs attention.
/// Pure logic — no Dalamud types. The roster HP is heartbeat-stale (~1–2s), so it is
/// DETECTION only: the executor re-checks live state (HP, HoT status, raise-pending)
/// before every cast.
///
/// Priority per pass: emergency heals (the toon may die) → raises → normal heals →
/// HoT/shield upkeep. Built for babysitting low-HP leveling bots (BLU caps ~9k HP):
/// the HoT stays on anyone who has taken ANY damage, raises are hardcast.
///
/// Cooldowns: per-target-per-kind absorbs heartbeat staleness (heal 2.5s, HoT 6s — the
/// executor also refuses while the status runs, raise 15s — covers the ~8s hardcast); a
/// global cooldown paces casts to roughly one per GCD.
/// </summary>
public sealed class HealWatchManager
{
    internal static readonly TimeSpan HealCooldown = TimeSpan.FromSeconds(2.5);
    internal static readonly TimeSpan HotCooldown = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan RaiseCooldown = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan GlobalCooldown = TimeSpan.FromSeconds(2.5);
    private const int MaxLogEntries = 10;

    private readonly Dictionary<string, DateTime> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HealLogEntry> _healLog = new();
    private DateTime _lastCastUtc = DateTime.MinValue;

    private HealWatchConfig _config;

    public HealWatchManager(HealWatchConfig config)
    {
        _config = config;
    }

    public IReadOnlyList<HealLogEntry> HealLog => _healLog;

    public void UpdateConfig(HealWatchConfig config) => _config = config;

    /// <summary>
    /// Ranked intents for this pass (empty when inert or nobody qualifies).
    /// <paramref name="canHot"/>/<paramref name="canRaise"/> reflect the local job's kit —
    /// a SGE gets no HoT intents, a sub-12 healer no raise intents.
    /// </summary>
    public IReadOnlyList<HealIntent> Evaluate(
        IEnumerable<HealCandidate> candidates,
        bool rotationEnabled,
        bool canHot,
        bool canRaise,
        DateTime nowUtc)
    {
        if (!_config.Enabled || rotationEnabled)
            return Array.Empty<HealIntent>();

        if (nowUtc - _lastCastUtc < GlobalCooldown)
            return Array.Empty<HealIntent>();

        var intents = new List<(HealIntent Intent, int Rank, float Hp)>();

        foreach (var candidate in candidates)
        {
            if (candidate.EntityId == 0 || (_config.OutOfPartyOnly && candidate.InParty))
                continue;

            // Dead (hp 0 with a valid entity id) → raise. Rank below emergency heals: a
            // dying toon can still be saved; a dead one can wait one GCD.
            if (candidate.RosterHp <= 0f)
            {
                if (canRaise && _config.RaiseDead && !OnCooldown(HealKind.Raise, candidate.Name, nowUtc))
                    intents.Add((new HealIntent(candidate.Name, candidate.EntityId, HealKind.Raise, Emergency: true), 1, 0f));
                continue;
            }

            if (candidate.RosterHp <= _config.HealThreshold
                && !OnCooldown(HealKind.Heal, candidate.Name, nowUtc))
            {
                var emergency = candidate.RosterHp <= _config.EmergencyThreshold;
                intents.Add((new HealIntent(candidate.Name, candidate.EntityId, HealKind.Heal, emergency),
                    emergency ? 0 : 2, candidate.RosterHp));
            }

            // HoT upkeep on anyone who has taken ANY damage — the executor skips while the
            // status is still running, so this converges to "keep it applied".
            if (canHot && _config.MaintainHot
                && candidate.RosterHp < 1f
                && !OnCooldown(HealKind.Hot, candidate.Name, nowUtc))
            {
                intents.Add((new HealIntent(candidate.Name, candidate.EntityId, HealKind.Hot, Emergency: false), 3, candidate.RosterHp));
            }
        }

        return intents
            .OrderBy(i => i.Rank)
            .ThenBy(i => i.Hp)
            .Select(i => i.Intent)
            .ToList();
    }

    /// <summary>Record an executed cast — starts the global + per-target-per-kind cooldowns.</summary>
    public void OnHealCast(HealIntent intent, DateTime nowUtc)
    {
        _lastCastUtc = nowUtc;
        _cooldowns[CooldownKey(intent.Kind, intent.Name)] = nowUtc;

        _healLog.Insert(0, new HealLogEntry(nowUtc, intent.Name, intent.Kind, intent.Emergency));
        if (_healLog.Count > MaxLogEntries)
            _healLog.RemoveRange(MaxLogEntries, _healLog.Count - MaxLogEntries);
    }

    private bool OnCooldown(HealKind kind, string name, DateTime nowUtc) =>
        nowUtc - _cooldowns.GetValueOrDefault(CooldownKey(kind, name)) < CooldownFor(kind);

    private static string CooldownKey(HealKind kind, string name) => $"{kind}:{name}";

    private static TimeSpan CooldownFor(HealKind kind) => kind switch
    {
        HealKind.Hot => HotCooldown,
        HealKind.Raise => RaiseCooldown,
        _ => HealCooldown,
    };
}
