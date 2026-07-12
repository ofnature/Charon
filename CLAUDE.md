# Charon — CLAUDE.md
> Load this file at the start of every session. Charon is a companion plugin to Daedalus — its conventions mirror `D:\Dev\Olympus\CLAUDE.md` and the ImGui design skill at `D:\Dev\Olympus\.cursor\rules\SKILL.md`.

## Project Overview
Charon is a standalone Dalamud plugin for FFXIV (named for the ferryman of Greek myth). Two features:
1. **Auto Pillion** — when the local player mounts a multi-passenger mount, scan seat occupancy and assign LAN party members (then manual whitelist) to open seats. No seat-2 spam: one invite per seat, timed-out seats are never re-invited.
2. **Auto Accept Group Invite** — auto-accept party invites from whitelisted characters only (manual list + optional Daedalus LAN roster trust). Strangers are IGNORED, never declined.

**Repo:** `D:\Dev\Charon` · **Solution:** `Charon.sln` · **Entry:** `Charon/CharonPlugin.cs` · **Command:** `/charon`

## Architecture
- Feature logic is PURE (no Dalamud types) and unit-tested: `Features/AutoPillion/PillionManager.cs`, `Features/AutoAccept/GroupInviteManager.cs`, `Services/WhitelistService.cs`. They consume immutable config snapshots (`PillionConfig`, `AutoAcceptConfig`) rebuilt from `CharonConfig` each frame.
- Game interop is isolated in thin unsafe adapters: `Services/Game/` (MountStateReader, GroupInviteInterop, ChatCommandSender). Fail-open, no logic worth testing.
- Invite detection/acceptance goes through ClientStructs `InfoProxyPartyInvite` (polled each tick: `InviterName`/`InviterWorldId`/`InviteTime`; accept via `RespondToInvitation(name, true)`). Do NOT go back to SelectYesno text parsing / FireCallback — it is language-dependent and broke in testing.
- Seat occupancy comes from rider characters: `Character.Mode == CharacterModes.RidingPillion` with `ModeParam` = 1-based seat number, tied to our mount by proximity (8y). Do NOT use `MountContainer.MountedEntityIds` — its ClientStructs offset is stale and reads float garbage (0x3F490FDB = π/4, verified in testing).
- Passengers further than 4y from the owner nav in first via vnavmesh IPC (`Services/Game/NavClient.cs`, `vnavmesh.SimpleMove.PathfindAndMoveCloseTo`) — fail-open no-op when vnavmesh isn't installed. Only paths WE issued are ever stopped.
- Boarding uses the native `BattleChara.RidePillion(uint seatIndex)` on the OWNER's character (`Services/Game/PillionRideHelper.cs`). The native index is 0-BASED — the helper converts from Charon's 1-based seat numbers (verified: passing 1 unconverted boarded Mount Seat #2). Do NOT use the /ridepillion chat command with a name — it only accepts placeholders (`<t>`, `<2>`…), never character names, and fails silently (verified in testing). Pillion requires being in the owner's party; both candidate ranking and boarding are party-gated.
- `Services/DaedalusIpcClient.cs` polls `Daedalus.Party.GetRosterJson` / `Daedalus.Party.GetTrustListJson` every 2s; falls back to manual-whitelist-only when Daedalus is not loaded. **Daedalus does not expose these endpoints yet** — provider side must be added to Daedalus (`PartyCoordinationIpc` / CoordinationBus roster).
- Pillion boarding is TRANSPORT-FREE: each passenger client self-boards via `Features/AutoPillion/PassengerSeatPicker.cs` — deterministic rank-by-name over the shared trusted set, k-th unmounted candidate takes k-th free seat, staggered by seat, then `/ridepillion`. The owner-side PillionManager session is monitoring/bookkeeping. `Ipc/CharonPillionIpc.cs` (Dalamud IPC broadcast) only reaches same-process instances; a Daedalus LAN relay would allow explicit owner-driven assignments (backlog, optional now).

## Build Baseline
- SDK: `Dalamud.NET.Sdk/15.0.0` (same as Daedalus — do not upgrade independently)
- **Warnings: 0. Tests: 66 passing.** Never commit above/below these baselines.
- Build BOTH configs before any change is done: `dotnet build Charon.sln -c Debug` AND `-c Release`; run `dotnet test Charon.Tests`.
- Release build emits `Charon/bin/Release/Charon/latest.zip` (DalamudPackager) for GitHub releases.
- Versions must match across: `Charon.csproj` `<Version>`, `CharonPlugin.PluginVersion`, `repo.json` `AssemblyVersion` + download-link tags, and the Charon entry in the combined `D:\Dev\Olympus\repo.json`.

## UI
Single window (`Windows/MainWindow.cs`), `AlwaysAutoResize`, Daedalus dark/gold theme via `Windows/CharonTheme.cs` (palette copied from the design skill — keep in sync). Sections: Auto Pillion, Auto Accept (trusted-characters table with [LAN]/[Manual] tags), collapsed Debug tree.

- Follow Teleport (two mechanisms, offer preferred): (1) the native party teleport-offer dialog ("Accept Teleport to X?" Yes/Wait/No) is auto-accepted via `Services/Game/TeleportOfferInterop.cs` — its addon name is NOT in ClientStructs, so it is LEARNED at runtime (first addon opening while `Telepo.ActiveTeleportRequest` is true) and persisted in `CharonConfig.TeleportOfferAddonName`; (2) fallback territory-follow (party member zones → `Telepo.Teleport` to an attuned aetheryte there via `IAetheryteList`), which stands down for 15s after an offer accept.

## Safety Rails (do not weaken)
- Never auto-DECLINE an invite — ignore means the dialog stays up for the player.
- Accept fires after a random 0.3–0.8s delay (`AutoAcceptConfig` constants).
- Passenger seats are 1..N-1 for an N-person mount (game's Ride Pillion menu: #1–#3 on a 4-seater, #1–#7 on an 8-seater); the owner rides the implicit last spot and is never assigned a seat.
- Character names fed to chat commands are sanitized (`ChatCommandSender.IsSafeName`).
