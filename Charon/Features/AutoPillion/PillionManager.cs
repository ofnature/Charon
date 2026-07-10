using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.AutoPillion;

/// <summary>Lifecycle of one passenger seat during a pillion session.</summary>
public enum SeatStatus
{
    /// <summary>Empty, no outstanding invite — assignable.</summary>
    Available,

    /// <summary>An invite was sent for this seat and is awaiting a response.</summary>
    InvitePending,

    /// <summary>Someone is sitting here.</summary>
    Filled,

    /// <summary>An invite for this seat timed out — never re-invite to this seat.</summary>
    Declined,
}

/// <summary>One invitable candidate. LAN members sort before manual whitelist members;
/// LAN members keep their toon order from the Daedalus LAN window.</summary>
public sealed record PillionCandidate(string CharacterName, string World, bool IsLanMember, int LanOrder);

/// <summary>An invite the manager wants sent: put <paramref name="CharacterName"/> on seat <paramref name="SeatIndex"/>.</summary>
public sealed record PillionInvite(string CharacterName, string World, int SeatIndex);

/// <summary>Per-seat state, exposed for the UI/debug section.</summary>
public sealed class PillionSeat
{
    public int Index { get; init; }
    public SeatStatus Status { get; internal set; } = SeatStatus.Available;
    public string AssignedName { get; internal set; } = string.Empty;
    public DateTime InviteSentUtc { get; internal set; }
}

/// <summary>
/// Seat-assignment state machine for multi-passenger mounts. Pure logic — no Dalamud types.
/// The plugin feeds it mount/occupancy events from the game and a candidate list from the
/// Daedalus LAN roster + manual whitelist; it decides who gets invited to which seat.
///
/// Seat numbering matches the game's /ridepillion indices: passenger seats are 1..N-1 for an
/// N-person mount (1–7 on an 8-seater, 1–3 on a 4-seater). The owner rides the implicit last
/// spot and is never assigned a passenger seat.
///
/// Guarantees:
/// - At most one outstanding invite per seat, and per candidate.
/// - A seat whose invite timed out is Declined and never re-invited.
/// - A candidate is dropped after <see cref="MaxAttemptsPerCandidate"/> timeouts.
/// - All state clears on dismount.
/// </summary>
public sealed class PillionManager
{
    /// <summary>A candidate who timed out this many times is dropped for the session.</summary>
    internal const int MaxAttemptsPerCandidate = 2;

    private readonly Action<PillionInvite> _sendInvite;
    private readonly Action<string>? _log;

    private PillionConfig _config;
    private readonly List<PillionSeat> _seats = new();
    private readonly List<PillionCandidate> _candidates = new();
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seated = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dropped = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _mountedUtc;
    private bool _delayElapsed;

    public PillionManager(PillionConfig config, Action<PillionInvite> sendInvite, Action<string>? log = null)
    {
        _config = config;
        _sendInvite = sendInvite;
        _log = log;
    }

    /// <summary>True while the local player is on a multi-passenger mount and a session is running.</summary>
    public bool SessionActive { get; private set; }

    /// <summary>Mount row id of the current session's mount (0 when inactive).</summary>
    public uint MountId { get; private set; }

    /// <summary>Invitable passenger seats, numbered 1..this (0 when inactive). Excludes the owner's spot.</summary>
    public int PassengerSeats { get; private set; }

    public IReadOnlyList<PillionSeat> Seats => _seats;

    public int SeatsFilled => _seats.Count(s => s.Status == SeatStatus.Filled);

    /// <summary>Passenger seats that are still open (not filled, pending, or declined).</summary>
    public int SeatsAvailable => _seats.Count(s => s.Status == SeatStatus.Available);

    public void UpdateConfig(PillionConfig config) => _config = config;

    /// <summary>
    /// Replace the ordered candidate list (LAN roster + manual whitelist). Order is normalized here:
    /// LAN members first by their LAN toon order, then manual entries in list order. Candidates who
    /// are already seated, invited, or dropped keep that state.
    /// </summary>
    public void SetCandidates(IEnumerable<PillionCandidate> candidates)
    {
        _candidates.Clear();
        var normalized = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.CharacterName))
            .Where(c => !_config.LanMembersOnly || c.IsLanMember)
            .OrderBy(c => c.IsLanMember ? 0 : 1)
            .ThenBy(c => c.IsLanMember ? c.LanOrder : 0);
        foreach (var candidate in normalized)
        {
            if (!_candidates.Any(c => c.CharacterName.Equals(candidate.CharacterName, StringComparison.OrdinalIgnoreCase)))
                _candidates.Add(candidate);
        }
    }

    /// <summary>
    /// Local player mounted a multi-passenger mount. Starts a session; passenger seats
    /// 1..<paramref name="passengerSeats"/> open (the owner's spot is not among them).
    /// </summary>
    public void OnMounted(uint mountId, int passengerSeats, DateTime nowUtc)
    {
        Reset();

        if (!_config.Enabled || passengerSeats < 1)
            return;

        SessionActive = true;
        MountId = mountId;
        PassengerSeats = passengerSeats;
        _mountedUtc = nowUtc;
        _delayElapsed = false;

        for (var i = 1; i <= passengerSeats; i++)
            _seats.Add(new PillionSeat { Index = i });

        _log?.Invoke($"Pillion session started: mount {mountId}, {passengerSeats} passenger seats");
    }

    /// <summary>Local player dismounted — clear all session state.</summary>
    public void OnDismounted()
    {
        if (SessionActive)
            _log?.Invoke("Pillion session ended (dismounted)");
        Reset();
    }

    /// <summary>A passenger appeared on a seat (accept, or someone hopped on uninvited).</summary>
    public void OnSeatOccupied(int seatIndex, string occupantName)
    {
        var seat = FindSeat(seatIndex);
        if (seat == null)
            return;

        seat.Status = SeatStatus.Filled;
        seat.AssignedName = occupantName;
        if (occupantName.Length > 0)
            _seated.Add(occupantName);

        // If this occupant had a pending invite on a DIFFERENT seat (they picked their own),
        // that seat is actually still open — release it.
        foreach (var other in _seats)
        {
            if (other.Index != seatIndex
                && other.Status == SeatStatus.InvitePending
                && other.AssignedName.Equals(occupantName, StringComparison.OrdinalIgnoreCase))
            {
                other.Status = SeatStatus.Available;
                other.AssignedName = string.Empty;
            }
        }
    }

    /// <summary>
    /// A previously filled seat opened up (passenger hopped off) — it becomes assignable again.
    /// The leaver chose to get off: they are not re-invited this session, so the reopened seat
    /// goes to a still-pending candidate.
    /// </summary>
    public void OnSeatVacated(int seatIndex)
    {
        var seat = FindSeat(seatIndex);
        if (seat == null || seat.Status != SeatStatus.Filled)
            return;

        if (seat.AssignedName.Length > 0)
        {
            _seated.Remove(seat.AssignedName);
            _dropped.Add(seat.AssignedName);
        }

        seat.Status = SeatStatus.Available;
        seat.AssignedName = string.Empty;
    }

    /// <summary>
    /// Drive timers and assignment. Call every frame (or tick) with the current UTC time.
    /// Sends invites via the callback passed to the constructor.
    /// </summary>
    public void Update(DateTime nowUtc)
    {
        if (!SessionActive)
            return;

        if (!_config.Enabled)
        {
            Reset();
            return;
        }

        // Post-mount grace period: let the mount animation finish before the first invite.
        if (!_delayElapsed)
        {
            if (nowUtc - _mountedUtc < _config.PillionDelay)
                return;
            _delayElapsed = true;
        }

        ExpireTimeouts(nowUtc);
        AssignPending(nowUtc);
    }

    private void ExpireTimeouts(DateTime nowUtc)
    {
        foreach (var seat in _seats)
        {
            if (seat.Status != SeatStatus.InvitePending)
                continue;
            if (nowUtc - seat.InviteSentUtc < _config.SeatTimeout)
                continue;

            // Timeout: the seat is dead for this session; the candidate may get one more
            // try on a different seat before being dropped.
            var name = seat.AssignedName;
            seat.Status = SeatStatus.Declined;
            seat.AssignedName = name; // keep for the debug display
            _log?.Invoke($"Seat {seat.Index} invite to {name} timed out — seat marked declined");

            if (_attempts.GetValueOrDefault(name) >= MaxAttemptsPerCandidate)
            {
                _dropped.Add(name);
                _log?.Invoke($"{name} timed out {MaxAttemptsPerCandidate} times — dropped for this session");
            }
        }
    }

    private void AssignPending(DateTime nowUtc)
    {
        foreach (var candidate in _candidates)
        {
            if (_seated.Contains(candidate.CharacterName) || _dropped.Contains(candidate.CharacterName))
                continue;
            if (HasOutstandingInvite(candidate.CharacterName))
                continue;

            var seat = _seats.FirstOrDefault(s => s.Status == SeatStatus.Available);
            if (seat == null)
                return; // no open seats left — wait for one to open

            seat.Status = SeatStatus.InvitePending;
            seat.AssignedName = candidate.CharacterName;
            seat.InviteSentUtc = nowUtc;
            _attempts[candidate.CharacterName] = _attempts.GetValueOrDefault(candidate.CharacterName) + 1;
            _sendInvite(new PillionInvite(candidate.CharacterName, candidate.World, seat.Index));
            _log?.Invoke($"Invited {candidate.CharacterName} to seat {seat.Index}");
        }
    }

    private bool HasOutstandingInvite(string name) =>
        _seats.Any(s => s.Status == SeatStatus.InvitePending
                        && s.AssignedName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private PillionSeat? FindSeat(int index) => _seats.FirstOrDefault(s => s.Index == index);

    private void Reset()
    {
        SessionActive = false;
        MountId = 0;
        PassengerSeats = 0;
        _delayElapsed = false;
        _seats.Clear();
        _attempts.Clear();
        _seated.Clear();
        _dropped.Clear();
    }
}
