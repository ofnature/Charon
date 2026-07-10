<p align="center">
  <img src="images/icon.png" width="96" alt="Charon icon">
</p>

# Charon

**Auto pillion with smart seat scanning and whitelisted auto group invite.** A Dalamud plugin for FFXIV, companion to [Daedalus](https://github.com/ofnature/Daedalus) — named for the ferryman who carries passengers across the Styx.

Built for multibox setups: get all your toons grouped and onto one multi-seat mount without touching seven other keyboards.

<p align="center">
  <img src="images/ui-main.png" width="420" alt="Charon main window (mockup)">
</p>

## Auto Pillion

Existing auto-pillion tools default everyone to seat 2 and spam it when taken. Charon scans real seat occupancy and assigns intelligently:

- **Passengers board themselves** — each client detects a trusted party member mounting a multi-seat mount nearby, deterministically computes its own seat (rank-by-name over the toons actually present, k-th toon takes the k-th free seat), and boards through the game's native Ride Pillion call. No cross-client messaging needed, no seat collisions.
- **Walks to the mount first** via [vnavmesh](https://github.com/awgil/ffxiv_navmesh) when out of range (optional — works without it if the toons already stand nearby).
- **Owner-side session tracking** — per-seat status with invite delay, per-seat timeout, and a hard rule: a declined seat is never re-invited.
- Party-gated (pillion is a game rule: group members only), configurable invite delay and seat timeout, LAN-members-only toggle.

## Auto Accept Group Invites

Accepting invites on 7 toons manually gets old. Charon auto-accepts **from trusted characters only**:

- Manual whitelist (name + world, case-insensitive) with per-entry enable/disable, plus optional auto-trust for everyone in the Daedalus LAN party roster (one-click import too).
- Strangers are **ignored, never declined** — the dialog stays up for you to decide.
- Small randomized accept delay; invite detection is language-independent (no dialog text parsing).

<p align="center">
  <img src="images/ui-debug.png" width="420" alt="Debug section with scramble aliases (mockup)">
</p>

## Daedalus Integration

When [Daedalus](https://github.com/ofnature/Daedalus) is loaded, Charon consumes its LAN party roster over IPC — trusted toons appear automatically with `[LAN]` tags, reconnects survive plugin reloads, and everything degrades gracefully to the manual whitelist when Daedalus is absent.

Bonus for screenshots: a **Scramble** toggle swaps every character name for a session-stable underworld alias (Styx, Acheron, Lethe…) — cosmetic and draw-time only.

## Installation

Add the repo URL to Dalamud (Settings → Experimental → Custom Plugin Repositories):

```
https://raw.githubusercontent.com/ofnature/Daedalus/main/repo.json
```

Then install **Charon** from the plugin installer. `/charon` toggles the window.

One URL, whole family: the same repository also serves [Daedalus](https://github.com/ofnature/Daedalus) and [SealBreaker](https://github.com/ofnature/SealBreaker).

## Building

```
dotnet build Charon.sln -c Release
dotnet test Charon.Tests
```

Targets `Dalamud.NET.Sdk/15.0.0`; the test suite covers the seat state machine, deterministic seat picking, whitelist matching, and IPC fallback.

*Window images above are stylized mockups of the in-game UI.*
