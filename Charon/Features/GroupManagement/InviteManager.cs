using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Services;

namespace Charon.Features.GroupManagement;

/// <summary>One sent invite, kept for the section's log.</summary>
public sealed record InviteLogEntry(DateTime TimeUtc, string Name, string Detail, bool Success);

/// <summary>
/// Single + mass party invites from the LAN roster. Pure queue/eligibility logic — the actual
/// game call is the injected send callback (native InviteToParty via PartyInviteHelper).
///
/// Mass invites are STAGGERED 0.3–0.5s apart to avoid server-side rate limiting; eligibility
/// excludes offline toons, the local character, toons already grouped with us, and everything
/// past the 8-slot party cap (counting invites already in flight).
/// </summary>
public sealed class InviteManager
{
    public const int MaxPartySize = 8;
    internal static readonly TimeSpan MinStagger = TimeSpan.FromSeconds(0.3);
    internal static readonly TimeSpan MaxStagger = TimeSpan.FromSeconds(0.5);
    private const int MaxLogEntries = 12;

    private readonly Func<LanToonInfo, (bool Ok, string Detail)> _sendInvite;
    private readonly Random _random;
    private readonly Action<string>? _log;

    private readonly List<(LanToonInfo Toon, DateTime DueUtc)> _queue = new();
    private readonly List<InviteLogEntry> _inviteLog = new();

    public InviteManager(
        Func<LanToonInfo, (bool Ok, string Detail)> sendInvite,
        Random? random = null,
        Action<string>? log = null)
    {
        _sendInvite = sendInvite;
        _random = random ?? new Random();
        _log = log;
    }

    public IReadOnlyList<InviteLogEntry> InviteLog => _inviteLog;

    /// <summary>Invites queued but not yet sent (mass-invite stagger in flight).</summary>
    public int PendingCount => _queue.Count;

    /// <summary>Invite one specific toon immediately (next Update tick).</summary>
    public void InviteSingle(LanToonInfo toon, DateTime nowUtc)
    {
        if (toon.CharacterName.Length == 0 || IsQueued(toon.CharacterName))
            return;

        _queue.Add((toon, nowUtc));
    }

    /// <summary>
    /// Queue invites for every eligible roster toon: online, not us, not already grouped,
    /// staggered 0.3–0.5s apart, and never past the 8-slot cap (current members + in-flight
    /// invites). Returns how many were queued.
    /// </summary>
    public int InviteAll(
        IEnumerable<LanToonInfo> roster,
        string localName,
        int currentPartySize,
        Func<string, bool> isInParty,
        DateTime nowUtc)
    {
        // A solo player counts as a party of one for cap purposes.
        var slotsTaken = Math.Max(currentPartySize, 1) + _queue.Count;
        var due = nowUtc;
        var queued = 0;

        foreach (var toon in roster)
        {
            if (slotsTaken >= MaxPartySize)
                break;
            if (!toon.IsOnline
                || toon.CharacterName.Length == 0
                || toon.CharacterName.Equals(localName, StringComparison.OrdinalIgnoreCase)
                || isInParty(toon.CharacterName)
                || IsQueued(toon.CharacterName))
                continue;

            _queue.Add((toon, due));
            due += MinStagger + TimeSpan.FromSeconds(_random.NextDouble() * (MaxStagger - MinStagger).TotalSeconds);
            slotsTaken++;
            queued++;
        }

        _log?.Invoke($"Mass invite: {queued} queued");
        return queued;
    }

    /// <summary>Send due invites. Call every frame. (The queue is not due-ordered — an
    /// InviteSingle can land behind staggered mass entries — so scan the whole list.)</summary>
    public void Update(DateTime nowUtc)
    {
        for (var i = 0; i < _queue.Count;)
        {
            if (_queue[i].DueUtc > nowUtc)
            {
                i++;
                continue;
            }

            var (toon, _) = _queue[i];
            _queue.RemoveAt(i);

            var (ok, detail) = _sendInvite(toon);
            _inviteLog.Insert(0, new InviteLogEntry(nowUtc, toon.CharacterName, detail, ok));
            if (_inviteLog.Count > MaxLogEntries)
                _inviteLog.RemoveRange(MaxLogEntries, _inviteLog.Count - MaxLogEntries);
            _log?.Invoke($"Invite {toon.CharacterName}: {detail}");
        }
    }

    private bool IsQueued(string name) =>
        _queue.Any(q => q.Toon.CharacterName.Equals(name, StringComparison.OrdinalIgnoreCase));
}
