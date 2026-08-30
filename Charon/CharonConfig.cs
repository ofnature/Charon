using System;
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
    /// 3 = gear IPC execution is switched on (it shipped off for one release, pending validation).
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>Newest schema version — a config below this runs <see cref="Migrate"/> on load.</summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Bring a loaded config up to <see cref="CurrentVersion"/>. Returns true when something
    /// changed (the caller then saves).
    ///
    /// Migrations exist because a saved config OVERRIDES a property's C# default: once a version has
    /// written a value, changing the default in code does nothing for anyone who already ran it.
    /// Each step runs exactly once, so a user who later reverses one of these keeps their decision.
    /// Fresh installs start at version 1 and run every step, which is what makes new and existing
    /// boxes end up in the same state.
    /// </summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        if (Version < 2)
        {
            foreach (var itemId in Features.Gear.ExpBonusItems.ItemIds)
            {
                if (!GearNeverEvictItemIds.Contains(itemId))
                    GearNeverEvictItemIds.Add(itemId);
            }
        }

        // Shipped OFF in 0.1.9 so the equip pass could be verified in-game before plugins could
        // trigger it. It has been, so switch the fleet on rather than making every box tick a box.
        if (Version < 3)
            GearIpcExecuteEnabled = true;

        Version = CurrentVersion;
        return true;
    }

    // Pillion
    public bool AutoPillionEnabled { get; set; } = false;

    /// <summary>Seconds to wait after mounting before sending invites (lets the mount animation finish).</summary>
    public float PillionDelay { get; set; } = 1.5f;

    /// <summary>Seconds before an unanswered seat invite is marked declined.</summary>
    public float SeatTimeout { get; set; } = 5.0f;

    /// <summary>
    /// Pop a notification on the mount owner's screen when every passenger seat is taken, so the
    /// driver knows the fleet is aboard without counting riders.
    /// </summary>
    public bool PillionFullNotify { get; set; } = true;

    /// <summary>Only invite Daedalus LAN party members (skip the manual whitelist for pillion).</summary>
    public bool LanMembersOnly { get; set; } = true;

    /// <summary>
    /// Pop a small riders window while driving a multi-seat mount, showing who is in which seat.
    /// Closes itself on dismount.
    /// </summary>
    public bool PillionRidersWindowEnabled { get; set; } = true;

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

    /// <summary>
    /// Slack radius (yalms) while in ordinary combat: the follower holds position until the leader
    /// gets this far away, so a melee toon can stay on its target instead of being heeled out of
    /// range. Set to the follow distance or below to disable the slack and follow tightly.
    /// </summary>
    public float FollowCombatLeash { get; set; } = 15f;

    /// <summary>Active follow leader ("" = not following). Persisted so a reload mid-follow resumes.</summary>
    public string FollowLeaderName { get; set; } = string.Empty;

    /// <summary>Verify the leader is actually walkable-to before pathing (catches portals/teleport stones).</summary>
    public bool FollowReachabilityCheck { get; set; } = true;

    /// <summary>When the leader ports out of reach, walk to the object they used and click it too.</summary>
    public bool FollowTakePortals { get; set; } = true;

    /// <summary>
    /// Sprint whenever out of combat and actually moving. Never in combat (the rotation owns the
    /// action queue there) and never mounted (a mount is already faster).
    /// </summary>
    public bool AutoSprintEnabled { get; set; } = true;

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

    /// <summary>
    /// Accept the revival prompt when a raise lands. Without this an unattended toon stays dead —
    /// the raise resolves and the prompt sits unanswered.
    /// </summary>
    public bool AutoAcceptRevival { get; set; } = true;

    /// <summary>
    /// Addon name of the revival prompt, learned at runtime the first time one appears while dead
    /// with a raise pending (not documented in ClientStructs).
    /// </summary>
    public string RevivalPromptAddonName { get; set; } = string.Empty;

    // Loot rolling
    /// <summary>
    /// Watch the loot window and work out what to roll. READ-ONLY for now — decisions are shown and
    /// logged, nothing is clicked, until item resolution has been checked against real drops.
    /// </summary>
    public bool LootRollEnabled { get; set; } = true;

    /// <summary>Pass on gear more than this many item levels below what this job wears.</summary>
    public int LootPassBelowIlvlGap { get; set; } = 30;

    /// <summary>Walk within reach of a treasure chest and open it (never in high-end duties).</summary>
    public bool AutoOpenChestsEnabled { get; set; } = true;

    /// <summary>
    /// How close (yalms) a chest must be before the auto-open fires. The game enforces its own
    /// interact limit — a value past it just means the open fires as soon as you're close enough.
    /// </summary>
    public float ChestOpenRange { get; set; } = 4.0f;

    /// <summary>Mash the Active Time Maneuver (QTE) automatically so unattended toons never fail one.</summary>
    public bool AutoQteEnabled { get; set; } = true;

    /// <summary>Commend a party member when the end-of-duty banner appears (never premades — the game refuses them).</summary>
    public bool AutoCommendEnabled { get; set; } = true;

    /// <summary>Commendation priority: 0 tank, 1 healer, 2 dps, 3 none.</summary>
    public int CommendPriority { get; set; } = 0;

    /// <summary>Skip the "[Charon] Commendation given to X" chat line.</summary>
    public bool CommendHideChat { get; set; } = false;

    /// <summary>Never commend a party member who died this duty.</summary>
    public bool CommendExcludeDeaths { get; set; } = false;

    /// <summary>Fill the item turn-in ("Request") window automatically.</summary>
    public bool AutoTurnInEnabled { get; set; } = true;

    /// <summary>Also press Hand Over once filled. Off by default — handing items over is a decision.</summary>
    public bool AutoTurnInConfirm { get; set; } = false;

    /// <summary>Show the deep-dungeon floor map window while inside one (full layout, incl. unrevealed rooms).</summary>
    public bool DeepDungeonMapEnabled { get; set; } = true;

    /// <summary>
    /// Auto-advance quest dialogue (click through Talk boxes). OFF by default — on a box a human
    /// is playing this eats dialogue; bots tick it, and Odysseus can force it over IPC with a
    /// self-expiring lease while it quests.
    /// </summary>
    public bool TextAdvanceEnabled { get; set; } = false;

    /// <summary>Draw the deep-dungeon ESP overlay (chests, passage, traps, mob aggro ranges).</summary>
    public bool DeepDungeonEspEnabled { get; set; } = true;

    /// <summary>ESP: mob aggro circles/cones and patrol arrows.</summary>
    public bool DeepDungeonEspMobs { get; set; } = true;

    /// <summary>ESP: mob name labels.</summary>
    public bool DeepDungeonEspMobNames { get; set; } = true;

    /// <summary>ESP: chest/passage/return/trap highlights.</summary>
    public bool DeepDungeonEspChests { get; set; } = true;

    /// <summary>
    /// Learn unlearned collectibles in the bags automatically (out of combat, one per 1.5s).
    /// NEVER the sellable kinds — fashion accessories and chocobo barding stay a manual click,
    /// because an unlearned one can be worth millions and collecting consumes it. Off by default:
    /// consuming items unprompted is opt-in.
    /// </summary>
    public bool AutoCollectEnabled { get; set; } = false;

    // Fleet Leader
    /// <summary>
    /// Character designated as fleet leader — the only toon whose fleet commands (currently Leave
    /// Duty) this box obeys. Set the same name on every box; a box whose own name matches it shows
    /// the leader controls.
    /// </summary>
    public string FleetLeaderName { get; set; } = string.Empty;

    /// <summary>Obey the fleet leader's Leave Duty broadcast.</summary>
    public bool FleetLeaveDutyEnabled { get; set; } = true;

    /// <summary>
    /// Hand party leadership back to the fleet leader when this toon is holding it. A disconnect
    /// moves leadership to another member (usually a bot), and it does not return on its own.
    /// </summary>
    public bool FleetAutoPromoteLeader { get; set; } = true;

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
    /// Equip Recommended. On by default since 0.1.10 (verified in-game); existing installs are moved
    /// over by the version-3 migration, since their saved <c>false</c> would otherwise stick.
    /// </summary>
    public bool GearIpcExecuteEnabled { get; set; } = true;

    /// <summary>Save the newly worn gear onto the active gearset after a pass.</summary>
    public bool GearUpdateGearsetAfterPass { get; set; } = true;

    /// <summary>Scan the armoury ONLY — skips the main bags, where dungeon/SealBreaker loot lands.</summary>
    public bool GearArmouryOnly { get; set; } = false;

    /// <summary>Item ids the armoury cleanup must never evict (per-item veto from its preview list).</summary>
    public List<uint> GearNeverEvictItemIds { get; set; } = new();

    // Leveling support (docs/leveling-mode-plan.md)
    /// <summary>Expose the Charon.Leveling.* gates (job levels + blockers) to other plugins.</summary>
    public bool LevelingIpcEnabled { get; set; } = true;

    // GIL section
    /// <summary>The item the gil tools sell/donate — Duck Bones (10119, PriceLow 360, verified).</summary>
    public uint GilItemId { get; set; } = 10119;

    /// <summary>
    /// When each character (by content id) last donated at the Doman Enclave — or was OBSERVED
    /// with an empty weekly budget, which counts the same. Checked against the Tuesday 08:00 UTC
    /// reset so a used-up toon skips the trip entirely.
    /// </summary>
    public Dictionary<ulong, DateTime> DomanLastDonationUtc { get; set; } = new();

    /// <summary>
    /// Last enclave state seen live, per character (content id). The client only populates
    /// <c>DomanEnclaveManager</c> after the character has been near the enclave that session, so
    /// away from it the live read says nothing — this cache answers instead (DailyDuty's shipped
    /// approach). The donated count only carries inside the week it was captured; the allowance
    /// carries until the next real read corrects it (a milestone can change it).
    /// </summary>
    public sealed class DomanEnclaveSnapshot
    {
        public int Allowance { get; set; }
        public int Donated { get; set; }
        public int RatePercent { get; set; }
        public DateTime CapturedUtc { get; set; }
    }

    /// <inheritdoc cref="DomanEnclaveSnapshot"/>
    public Dictionary<ulong, DomanEnclaveSnapshot> DomanEnclaveCache { get; set; } = new();

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
