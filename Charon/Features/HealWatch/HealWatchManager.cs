using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.HealWatch;

/// <summary>Immutable snapshot of the Heal Watch settings, rebuilt from <see cref="CharonConfig"/>.</summary>
public sealed record HealWatchConfig(
    bool Enabled,
    float HealThreshold,
    float EmergencyThreshold,
    bool OutOfPartyOnly)
{
    public static HealWatchConfig From(CharonConfig config) => new(
        config.HealWatchEnabled,
        config.HealThreshold,
        config.EmergencyThreshold,
        config.HealOutOfPartyOnly);
}

/// <summary>One fleet toon as seen by Heal Watch: LAN vitals + local party context.</summary>
public sealed record HealCandidate(string Name, uint EntityId, float RosterHp, bool InParty);

/// <summary>A heal the manager wants cast, most urgent first.</summary>
public sealed record HealIntent(string Name, uint EntityId, bool Emergency);

/// <summary>One executed heal, kept for the debug section.</summary>
public sealed record HealLogEntry(DateTime TimeUtc, string Name, bool Emergency);

/// <summary>
/// Watches fleet vitals from the Daedalus LAN roster and decides who needs a heal.
/// Pure logic — no Dalamud types. The roster HP is heartbeat-stale (~1–2s), so it is
/// DETECTION only: the executor re-checks live HP before every cast.
///
/// Rules:
/// - Inert while disabled OR while the local Daedalus rotation is enabled (it owns the queue).
/// - Candidates above the heal threshold, dead (hp 0 is indistinguishable from a pre-vitals
///   Daedalus build — skip both), or in-party when OutOfPartyOnly, are ignored.
/// - Per-target cooldown absorbs heartbeat staleness (no double-casting on old HP); a global
///   cooldown paces casts to roughly one per GCD.
/// - Emergencies (below the emergency threshold) sort before everyone else, then by HP.
/// </summary>
public sealed class HealWatchManager
{
    internal static readonly TimeSpan TargetCooldown = TimeSpan.FromSeconds(2.5);
    internal static readonly TimeSpan GlobalCooldown = TimeSpan.FromSeconds(2.5);
    private const int MaxLogEntries = 10;

    private readonly Dictionary<string, DateTime> _lastHealPerTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HealLogEntry> _healLog = new();
    private DateTime _lastHealUtc = DateTime.MinValue;

    private HealWatchConfig _config;

    public HealWatchManager(HealWatchConfig config)
    {
        _config = config;
    }

    public IReadOnlyList<HealLogEntry> HealLog => _healLog;

    public void UpdateConfig(HealWatchConfig config) => _config = config;

    /// <summary>Ranked heal intents for this pass (empty when inert or nobody qualifies).</summary>
    public IReadOnlyList<HealIntent> Evaluate(
        IEnumerable<HealCandidate> candidates,
        bool rotationEnabled,
        DateTime nowUtc)
    {
        if (!_config.Enabled || rotationEnabled)
            return Array.Empty<HealIntent>();

        if (nowUtc - _lastHealUtc < GlobalCooldown)
            return Array.Empty<HealIntent>();

        return candidates
            .Where(c => c.EntityId != 0
                        && c.RosterHp > 0f // dead, or a Daedalus build without vitals
                        && c.RosterHp <= _config.HealThreshold
                        && !(_config.OutOfPartyOnly && c.InParty)
                        && nowUtc - _lastHealPerTarget.GetValueOrDefault(c.Name) >= TargetCooldown)
            .OrderBy(c => c.RosterHp <= _config.EmergencyThreshold ? 0 : 1)
            .ThenBy(c => c.RosterHp)
            .Select(c => new HealIntent(c.Name, c.EntityId, c.RosterHp <= _config.EmergencyThreshold))
            .ToList();
    }

    /// <summary>Record an executed heal — starts both cooldowns and feeds the debug log.</summary>
    public void OnHealCast(HealIntent intent, DateTime nowUtc)
    {
        _lastHealUtc = nowUtc;
        _lastHealPerTarget[intent.Name] = nowUtc;

        _healLog.Insert(0, new HealLogEntry(nowUtc, intent.Name, intent.Emergency));
        if (_healLog.Count > MaxLogEntries)
            _healLog.RemoveRange(MaxLogEntries, _healLog.Count - MaxLogEntries);
    }
}
