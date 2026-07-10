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
