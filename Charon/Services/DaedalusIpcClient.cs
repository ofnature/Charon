using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Charon.Services;

/// <summary>One Daedalus-connected toon from the LAN party roster.</summary>
public sealed class LanToonInfo
{
    [JsonPropertyName("name")]
    public string CharacterName { get; set; } = string.Empty;

    [JsonPropertyName("world")]
    public string World { get; set; } = string.Empty;

    [JsonPropertyName("machine")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("online")]
    public bool IsOnline { get; set; }

    /// <summary>HP fraction 0–1 from the LAN heartbeat (~1–2s stale; detection only — re-check live before acting). 0 when the Daedalus build predates the field.</summary>
    [JsonPropertyName("hp")]
    public float Hp { get; set; }

    /// <summary>Entity id from the LAN heartbeat for object-table resolution. 0 when the Daedalus build predates the field.</summary>
    [JsonPropertyName("entityId")]
    public uint EntityId { get; set; }

    /// <summary>Seat assigned during the current pillion session (0 = none). Set by Charon, not Daedalus.</summary>
    [JsonIgnore]
    public int SeatAssignment { get; set; }
}

/// <summary>Read-only view of the Daedalus LAN roster — the seam the feature managers depend on.</summary>
public interface IDaedalusRosterProvider
{
    /// <summary>True when Daedalus is loaded and answering IPC.</summary>
    bool IsAvailable { get; }

    /// <summary>Current LAN party roster, in Daedalus LAN-window toon order. Empty when unavailable.</summary>
    IReadOnlyList<LanToonInfo> GetLanPartyMembers();

    /// <summary>Character names Daedalus trusts. Empty when unavailable.</summary>
    IReadOnlyList<string> GetTrustList();
}

/// <summary>
/// IPC bridge to the Daedalus LAN party window (CoordinationBus roster).
///
/// Polls <c>Daedalus.Party.GetRosterJson</c> / <c>Daedalus.Party.GetTrustListJson</c> on a throttle;
/// every refresh re-attempts the call gates, so a Daedalus reload reconnects automatically. When
/// Daedalus is not loaded the client reports unavailable and returns empty lists — callers fall
/// back to the manual whitelist only. Never throws out of <see cref="Refresh"/>.
/// </summary>
public sealed class DaedalusIpcClient : IDaedalusRosterProvider, IDisposable
{
    internal const string RosterEndpoint = "Daedalus.Party.GetRosterJson";
    internal const string TrustListEndpoint = "Daedalus.Party.GetTrustListJson";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly ICallGateSubscriber<string> _getRoster;
    private readonly ICallGateSubscriber<string> _getTrustList;
    private readonly ICallGateSubscriber<bool> _isEnabled;
    private readonly IPluginLog _log;

    private List<LanToonInfo> _roster = new();
    private List<string> _trustList = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private bool _wasAvailable;

    public DaedalusIpcClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;
        _getRoster = pluginInterface.GetIpcSubscriber<string>(RosterEndpoint);
        _getTrustList = pluginInterface.GetIpcSubscriber<string>(TrustListEndpoint);
        _isEnabled = pluginInterface.GetIpcSubscriber<bool>("Daedalus.IsEnabled");
    }

    /// <summary>
    /// True while the local Daedalus ROTATION is enabled — it owns the action queue then,
    /// and Heal Watch must stand down. False when Daedalus is absent (fail-open: Charon
    /// may act when nothing else is driving).
    /// </summary>
    public bool IsRotationEnabled { get; private set; }

    public bool IsAvailable { get; private set; }

    public IReadOnlyList<LanToonInfo> GetLanPartyMembers() => _roster;

    public IReadOnlyList<string> GetTrustList() => _trustList;

    /// <summary>
    /// Throttled poll — call from the framework update loop. Re-subscribing isn't needed:
    /// call gates resolve the provider at call time, so a reloaded Daedalus just starts answering.
    /// </summary>
    public void Refresh(DateTime nowUtc)
    {
        if (nowUtc - _lastRefreshUtc < RefreshInterval)
            return;
        _lastRefreshUtc = nowUtc;

        try
        {
            var rosterJson = _getRoster.InvokeFunc();
            _roster = ParseRoster(rosterJson);

            try
            {
                _trustList = ParseTrustList(_getTrustList.InvokeFunc());
            }
            catch
            {
                _trustList = new List<string>(); // roster endpoint newer than trust endpoint — tolerate
            }

            try
            {
                IsRotationEnabled = _isEnabled.InvokeFunc();
            }
            catch
            {
                IsRotationEnabled = false; // endpoint missing — treat as rotation off
            }

            IsAvailable = true;
            if (!_wasAvailable)
                _log.Info("Connected to Daedalus LAN party roster ({0} toons)", _roster.Count);
            _wasAvailable = true;
        }
        catch
        {
            // Daedalus not loaded (IpcNotReady) or mid-reload — fall back to manual whitelist only.
            if (_wasAvailable)
                _log.Info("Daedalus IPC unavailable — falling back to manual whitelist");
            IsAvailable = false;
            _wasAvailable = false;
            IsRotationEnabled = false;
            _roster = new List<LanToonInfo>();
            _trustList = new List<string>();
        }
    }

    /// <summary>Tolerant roster parse: bad/empty JSON yields an empty list, never an exception.</summary>
    internal static List<LanToonInfo> ParseRoster(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<LanToonInfo>();

        try
        {
            var parsed = JsonSerializer.Deserialize<List<LanToonInfo>>(json);
            if (parsed == null)
                return new List<LanToonInfo>();
            parsed.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.CharacterName));
            return parsed;
        }
        catch
        {
            return new List<LanToonInfo>();
        }
    }

    /// <summary>Tolerant trust-list parse: bad/empty JSON yields an empty list, never an exception.</summary>
    internal static List<string> ParseTrustList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            if (parsed == null)
                return new List<string>();
            parsed.RemoveAll(string.IsNullOrWhiteSpace);
            return parsed;
        }
        catch
        {
            return new List<string>();
        }
    }

    public void Dispose()
    {
        // ICallGateSubscriber holds no unmanaged state and needs no unsubscription for funcs.
    }
}
