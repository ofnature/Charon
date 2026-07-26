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
    /// <summary>
    /// Config schema version, bumped when a load-time migration is needed.
    /// 2 = the never-evict list is seeded with EXP-bonus gear (<see cref="Features.Gear.ExpBonusItems"/>).
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>Newest schema version — a config below this runs <see cref="Migrate"/> on load.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Bring a loaded config up to <see cref="CurrentVersion"/>. Returns true when something
    /// changed (the caller then saves). Runs for fresh installs too, since a new config starts at
    /// version 1 — that is what seeds the EXP-gear protection for everyone exactly once. A user who
    /// later unticks one of those items keeps that decision: the migration never runs again.
    /// </summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        foreach (var itemId in Features.Gear.ExpBonusItems.ItemIds)
        {
            if (!GearNeverEvictItemIds.Contains(itemId))
                GearNeverEvictItemIds.Add(itemId);
        }

        Version = CurrentVersion;
        return true;
    }

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

    // Fleet Follow
    /// <summary>How close (yalms) a follower trails its leader before it stops moving.</summary>
    public float FollowDistance { get; set; } = 2.5f;

    /// <summary>Stop following while IN COMBAT during a boss module (both true) — hands movement to BMR.</summary>
    public bool FollowStopInBossFight { get; set; } = true;

    /// <summary>Active follow leader ("" = not following). Persisted so a reload mid-follow resumes.</summary>
    public string FollowLeaderName { get; set; } = string.Empty;

    /// <summary>Verify the leader is actually walkable-to before pathing (catches portals/teleport stones).</summary>
    public bool FollowReachabilityCheck { get; set; } = true;

    /// <summary>When the leader ports out of reach, walk to the object they used and click it too.</summary>
    public bool FollowTakePortals { get; set; } = true;

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

    /// <summary>Auto-commence the Duty Finder pop when the whole party is trusted LAN toons.</summary>
    public bool AutoCommenceDutyEnabled { get; set; } = true;

    /// <summary>Mirror a trusted LAN toon's trade: click Trade after they do, then confirm.</summary>
    public bool AutoTradeEnabled { get; set; } = true;

    /// <summary>Auto-trust every toon in the Daedalus LAN party roster.</summary>
    public bool LanAutoWhitelist { get; set; } = true;

    public List<WhitelistEntry> ManualWhitelist { get; set; } = new();

    // FC Chest
    /// <summary>Chest page (1–5) the entrust/withdraw operations target; remembered across sessions.</summary>
    public int LastSelectedChestPage { get; set; } = 1;

    /// <summary>Keep the FC chest operation log expanded.</summary>
    public bool ShowFCChestLog { get; set; } = false;

    /// <summary>Pop the standalone FC Chest window automatically when the game chest opens.</summary>
    public bool FcChestWindowAutoOpen { get; set; } = true;

    /// <summary>Text scale for the FC chest UI (1.0 = normal). Accessibility — the item list gets hard to read.</summary>
    public float FcChestFontScale { get; set; } = 1.0f;

    // Gear Equipper
    /// <summary>Expose the Charon.EquipUpgrades / …Busy / PendingUpgradeCount gates to other plugins.</summary>
    public bool GearIpcEnabled { get; set; } = true;

    /// <summary>
    /// Let the IPC actually EQUIP. Off = preview only: the count gate and the in-window preview stay
    /// live, EquipUpgrades just logs the plan and returns false so callers fall back to the game's
    /// Equip Recommended. Flip this on once the previews have been verified in-game.
    /// </summary>
    public bool GearIpcExecuteEnabled { get; set; } = false;

    /// <summary>Save the newly worn gear onto the active gearset after a pass.</summary>
    public bool GearUpdateGearsetAfterPass { get; set; } = true;

    /// <summary>Scan the armoury ONLY — skips the main bags, where dungeon/SealBreaker loot lands.</summary>
    public bool GearArmouryOnly { get; set; } = false;

    /// <summary>Item ids the armoury cleanup must never evict (per-item veto from its preview list).</summary>
    public List<uint> GearNeverEvictItemIds { get; set; } = new();

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
