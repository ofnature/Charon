using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Services;

namespace Charon.Features.AutoAccept;

/// <summary>What the manager decided to do with an incoming invite.</summary>
public enum InviteDecision
{
    /// <summary>Feature disabled — invite untouched.</summary>
    Disabled,

    /// <summary>Inviter not trusted — invite left visible for the player to handle manually.</summary>
    Ignored,

    /// <summary>Trusted inviter — accept scheduled after a small random delay.</summary>
    AcceptScheduled,
}

/// <summary>One auto-accepted invite, kept for the debug section.</summary>
public sealed record AcceptLogEntry(DateTime TimeUtc, string CharacterName, string World, string Source);

/// <summary>
/// Whitelisted auto-accept for group invites. Pure decision + timing logic — no Dalamud types.
///
/// Trust order: manual whitelist first, then the Daedalus LAN roster (when LanAutoWhitelist is on).
/// Unknown inviters are IGNORED, never declined — the invite dialog stays up for a manual decision.
/// Accepts fire after a random 0.3–0.8s delay via the callback passed to the constructor.
/// </summary>
public sealed class GroupInviteManager
{
    private const int MaxLogEntries = 20;

    private readonly WhitelistService _whitelist;
    private readonly IDaedalusRosterProvider _roster;
    private readonly Random _random;
    private readonly Action _acceptInvite;
    private readonly Action<string>? _log;
    private readonly List<AcceptLogEntry> _acceptLog = new();

    private AutoAcceptConfig _config;
    private DateTime? _pendingAcceptAtUtc;
    private (string Name, string World, string Source)? _pendingInviter;

    public GroupInviteManager(
        AutoAcceptConfig config,
        WhitelistService whitelist,
        IDaedalusRosterProvider roster,
        Action acceptInvite,
        Random? random = null,
        Action<string>? log = null)
    {
        _config = config;
        _whitelist = whitelist;
        _roster = roster;
        _acceptInvite = acceptInvite;
        _random = random ?? new Random();
        _log = log;
    }

    public IReadOnlyList<AcceptLogEntry> AcceptLog => _acceptLog;

    /// <summary>True while an accept is scheduled and waiting out its delay.</summary>
    public bool AcceptPending => _pendingAcceptAtUtc != null;

    public void UpdateConfig(AutoAcceptConfig config) => _config = config;

    /// <summary>
    /// A group invite arrived from <paramref name="characterName"/>@<paramref name="world"/>.
    /// Decides trust and, when trusted, schedules the accept 0.3–0.8s out.
    /// </summary>
    public InviteDecision OnInviteReceived(string characterName, string world, DateTime nowUtc)
    {
        if (!_config.Enabled)
            return InviteDecision.Disabled;

        var source = ResolveTrustSource(characterName, world);
        if (source == null)
        {
            _log?.Invoke($"Invite from {characterName}@{world} — not whitelisted, leaving for manual decision");
            return InviteDecision.Ignored;
        }

        var delay = TimeSpan.FromSeconds(
            AutoAcceptConfig.MinAcceptDelaySeconds
            + _random.NextDouble() * (AutoAcceptConfig.MaxAcceptDelaySeconds - AutoAcceptConfig.MinAcceptDelaySeconds));
        _pendingAcceptAtUtc = nowUtc + delay;
        _pendingInviter = (characterName, world, source);
        _log?.Invoke($"Invite from {characterName}@{world} trusted via {source} — accepting in {delay.TotalSeconds:F1}s");
        return InviteDecision.AcceptScheduled;
    }

    /// <summary>The invite dialog went away (accepted manually, declined, expired) — cancel any pending accept.</summary>
    public void OnInviteWithdrawn()
    {
        _pendingAcceptAtUtc = null;
        _pendingInviter = null;
    }

    /// <summary>Drive the accept delay. Call every frame with current UTC time.</summary>
    public void Update(DateTime nowUtc)
    {
        if (_pendingAcceptAtUtc == null || nowUtc < _pendingAcceptAtUtc)
            return;

        var (name, world, source) = _pendingInviter!.Value;
        _pendingAcceptAtUtc = null;
        _pendingInviter = null;

        _acceptLog.Insert(0, new AcceptLogEntry(nowUtc, name, world, source));
        if (_acceptLog.Count > MaxLogEntries)
            _acceptLog.RemoveRange(MaxLogEntries, _acceptLog.Count - MaxLogEntries);

        _log?.Invoke($"Auto-accepted group invite from {name}@{world} ({source})");
        _acceptInvite();
    }

    /// <summary>"Manual" / "LAN" when trusted, null when not.</summary>
    private string? ResolveTrustSource(string characterName, string world)
    {
        if (_whitelist.IsWhitelisted(characterName, world))
            return "Manual";

        if (_config.LanAutoWhitelist && IsLanMember(characterName, world))
            return "LAN";

        return null;
    }

    /// <summary>
    /// LAN roster match. World must match when the roster carries one; roster entries without
    /// a world (older Daedalus builds) match on name alone.
    /// </summary>
    private bool IsLanMember(string characterName, string world) =>
        _roster.GetLanPartyMembers().Any(t =>
            t.CharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase)
            && (t.World.Length == 0 || t.World.Equals(world, StringComparison.OrdinalIgnoreCase)));
}
