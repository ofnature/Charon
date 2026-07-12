# Charon Roadmap

Planned features, ordered by intended implementation. Each entry lists the design, the
existing plumbing it reuses, and the acceptance criteria. Conventions per CLAUDE.md:
pure testable feature logic, thin unsafe game adapters, fail-open, 0 warnings, tests green.

---

## 1. Assemble Party (one-click fleet invite)

**Goal:** One button on the main box invites every LAN roster toon to the party; their
Charon instances auto-accept (already shipped). Closes the loop: assemble → follow-teleport
→ auto-pillion with zero clicks on the alts.

**Design**
- `Services/Game/PartyInviteHelper.cs` — native `InfoProxyPartyInvite.InviteToParty(contentId, name, worldId)`
  (verified present in ClientStructs), fallback to the `/invite <name>` chat pipeline
  (Daedalus's helper proves it; same-world only — fine, the fleet shares a world).
- "Assemble Party" button in the main window next to Import from LAN: iterate LAN roster,
  skip self + toons already in party (IPartyList), stagger invites ~0.75s apart so
  7 accept dialogs don't race their randomized accept delays.
- Skip when party is full (8) or when we're not the leader and a party exists.

**Acceptance:** From 8 solo toons, one click on the main → full party in <15s with no
manual input. Strangers never invited (LAN roster only — not the manual whitelist).

## 2. Mount Matching (single-seat convoy)

**Goal:** When a trusted party member mounts a SINGLE-seat mount, alts mount their own
mount so the group can travel together.

**Design**
- Reuse the passenger-boarding owner scan (trusted + party + not riding pillion), but
  trigger on `PassengerSeats == 0` mounts instead.
- Execution: `ActionManager.UseAction(ActionType.Mount, mountId)` on the local client;
  check ownership via `PlayerState.IsMountUnlocked(mountId)` — if the leader's mount isn't
  owned, fall back to a configured favorite (config: `FallbackMountId`, 0 = same-as-leader only).
- Config: `MountMatchingEnabled` (default off), reuse PillionDelay for the stagger.
- Auto-dismount when the leader dismounts (same scan, inverse edge), config-gated
  (`DismountWithLeader`, default off).

**Acceptance:** Leader mounts a 1-seater → all alts mounted within ~3s; leader on a
multi-seater still triggers pillion boarding, never matching.

## 3. Auto-Follow on Foot

**Goal:** Alts vnav-follow the trusted leader when he walks, so the convoy moves without
per-box input between mounts/teleports.

**Design**
- `Features/Follow/FollowManager.cs` (pure: target selection + hysteresis) +
  reuse `NavClient` (the re-issue loop from boarding is exactly this).
- Follow when: enabled + trusted leader in party visible + distance > FollowDistance
  (slider, default 3y, deadband ±1y to avoid twitching — mirror Daedalus's vNav Flex
  pattern). Stop path when within deadband or leader mounts/teleports/vanishes.
- Suspend while: local player in combat, casting, mounted, or riding pillion; boarding
  logic takes priority over follow (boarding's nav wins).
- Config: `FollowEnabled`, `FollowDistance`, `FollowLeaderName` (empty = nearest trusted
  party member; a named leader avoids two alts electing different leaders mid-fight).

**Acceptance:** Leader walks 100y through a city; alts trail within FollowDistance ± deadband,
never ping-pong, and cleanly hand over to pillion boarding when the leader mounts an 8-seater.

## 4. Ready Check + Duty Confirm Auto-Accept

**Goal:** Alts answer ready checks and click Commence on the Duty Finder confirm.

**Design**
- Ready check: `AgentReadyCheck` / addon "_ReadyCheck"? — detect via the InfoProxy/agent
  first (same doctrine as the invite rework: no text parsing); fall back to addon
  PostSetup + `FireCallbackInt`. Respond only when a trusted party member is in the party.
- Duty confirm: addon "ContentsFinderConfirm" → Commence callback (well-trodden path in
  other plugins; keep it whitelist-gated and config-separate: `ReadyCheckEnabled`,
  `DutyConfirmEnabled`, both default off).
- Both use the 0.3–0.8s randomized delay convention.

**Acceptance:** Queue pops on 8 boxes → all commence without input; ready check initiated
by the main → 7 instant yeses. Nothing fires in parties without a trusted member.

## 5. Trade Auto-Accept (trusted only)

**Goal:** Trade REQUESTS from trusted characters are accepted automatically; the trade
contents/final confirm stay strictly manual.

**Design**
- Detect via the trade request addon/InfoProxy (research needed — likely a SelectYesno-class
  dialog + `InfoProxyTrade`?); identify the requester name and gate on the same
  manual-whitelist + LAN-roster trust as group invites. Ignore strangers (dialog stays up).
- Explicit non-goal: never auto-confirm trade CONTENTS — only the opening handshake.
- Config: `TradeAcceptEnabled` (default off).

**Acceptance:** Trusted toon opens trade → window opens on the alt without input; the
final checkmark always requires a human.

## 6. Flag Rally

**Goal:** Leader sets a map flag; alts walk to it (same zone).

**Design**
- Read the leader's flag: `AgentMap.Instance()->FlagMapMarker` — but the flag is per-client;
  the LEADER's flag isn't visible to alts without transport. Two options:
  a) Same-zone heuristic: rally to the flag set on the LOCAL map when a `/charon rally`
     command is typed on the leader and mirrored by hand — weak without transport.
  b) Do it properly over the Daedalus LAN relay (feature 7) — flag position broadcast.
- Decision: implement AFTER feature 7; expose `RallyToFlag()` on the local box first
  (`/charon rally` = vnav to own flag) as a useful standalone step.

**Acceptance (phase 1):** `/charon rally` on any box navs that toon to its own map flag.

## 7. Daedalus LAN Relay (cross-machine Charon channel)

**Goal:** Real owner-driven commands across machines: explicit seat assignments, rally
broadcasts, assemble-party triggers from one box for toons on another PC.

**Design**
- Daedalus side (D:\Dev\Olympus): CoordinationBus gains a generic relay message type
  (`PartyMessageType.PluginRelay`, payload = {channel, json}) + IPC provider endpoints
  `Daedalus.Relay.Publish(string channel, string json)` and subscriber event
  `Daedalus.Relay.Message` — Charon publishes/subscribes via Dalamud IPC locally, Daedalus
  ferries it over UDP to every machine (it already dedups/threads correctly).
- Charon side: `CharonPillionIpc` grows a transport interface; the existing message format
  is already transport-neutral (documented in CLAUDE.md). Fallback: today's behavior when
  the relay endpoints are absent.
- Then: seat assignments become owner-authoritative (observation-based self-boarding stays
  as the no-Daedalus fallback), and features 1/6 gain cross-machine triggers.

**Acceptance:** Two-PC test — owner on machine A mounts, toon on machine B receives its
seat assignment over the relay and boards it exactly (no rank inference), with the
observation fallback still passing the existing 66 tests.

---

### Explicitly skipped
- Auto-dismount riders on owner dismount (the game already does it).
- FC/friend-request auto-accepts (rare; wider abuse surface than value).
