using System.Collections.Generic;
using Dalamud.Configuration;

namespace Charon;

/// <summary>
/// Root persistent configuration for Charon. Feature code never reads this directly —
/// it consumes immutable snapshots (<see cref="Features.AutoPillion.PillionConfig"/>,
/// <see cref="Features.AutoAccept.AutoAcceptConfig"/>) taken from it each update.
/// </summary>
public sealed class CharonConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Pillion
    public bool AutoPillionEnabled { get; set; } = false;

    /// <summary>Seconds to wait after mounting before sending invites (lets the mount animation finish).</summary>
    public float PillionDelay { get; set; } = 1.5f;

    /// <summary>Seconds before an unanswered seat invite is marked declined.</summary>
    public float SeatTimeout { get; set; } = 5.0f;

    /// <summary>Only invite Daedalus LAN party members (skip the manual whitelist for pillion).</summary>
    public bool LanMembersOnly { get; set; } = true;

    // Follow Teleport
    /// <summary>When a trusted party member teleports to another zone, follow them there.</summary>
    public bool FollowTeleportEnabled { get; set; } = false;

    /// <summary>
    /// Addon name of the party teleport-offer dialog ("Accept Teleport to X?"), learned at
    /// runtime the first time an offer appears (not documented in ClientStructs).
    /// </summary>
    public string TeleportOfferAddonName { get; set; } = string.Empty;

    // Heal Watch
    /// <summary>Healer toon tops up fleet toons from LAN vitals (out-of-party healing).</summary>
    public bool HealWatchEnabled { get; set; } = false;

    /// <summary>Heal anyone at or below this HP fraction.</summary>
    public float HealThreshold { get; set; } = 0.8f;

    /// <summary>At or below this HP fraction a toon jumps the queue.</summary>
    public float EmergencyThreshold { get; set; } = 0.4f;

    /// <summary>Only heal toons OUTSIDE our party (in-party healing is the rotation's job).</summary>
    public bool HealOutOfPartyOnly { get; set; } = true;

    /// <summary>Keep the job's HoT/shield (Regen / Galvanize / Aspected Benefic) on damaged toons.</summary>
    public bool HealMaintainHot { get; set; } = true;

    /// <summary>Hardcast raise dead toons (no swiftcast — fine for parked leveling bots).</summary>
    public bool HealRaiseDead { get; set; } = true;

    // Auto Accept
    public bool AutoAcceptEnabled { get; set; } = false;

    /// <summary>Auto-trust every toon in the Daedalus LAN party roster.</summary>
    public bool LanAutoWhitelist { get; set; } = true;

    public List<WhitelistEntry> ManualWhitelist { get; set; } = new();

    // Window state
    public bool MainWindowVisible { get; set; } = true;
    public bool DebugSectionOpen { get; set; } = false;

    /// <summary>Cosmetic: replace character names with session-stable aliases in the window (for screenshots).</summary>
    public bool ScrambleNames { get; set; } = false;
}

/// <summary>One trusted character (name + world). Disabled entries stay listed but never match.</summary>
public sealed class WhitelistEntry
{
    public string CharacterName { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
