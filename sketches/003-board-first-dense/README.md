# Variant 003 — board first, denser

## Design stance
Argus' components at a tighter scale and a different emphasis: the landing page is a *status board*, not
a settings page. Charon's whole product state is one screen; settings are secondary.

## Key choices
- **Density** — 30px sidebar rows (icons 15px), 52px stat tiles, 23px list rows, six tiles across,
  27px setting rows, 25px buttons, 25px window padding.
- **Layout** — compact 60px identity strip; content splits into a wide "Live" column (every feature as one
  line: state dot + name + why it is idle) and a narrow right column (this character's switches as a
  segmented role picker + toggles, then the action buttons, then "needs you").
- **Content architecture** — the Debug status lines are promoted to the front page. Each line keeps its
  reason for being idle, which is the standard this plugin already holds itself to; the page is only
  honest if those lines are.
- **New component** — a segmented control (Kill / Tag / Off) for Quick Kill's per-character role, plus a
  provider switch (vnavmesh / Ariadne). Argus has no equivalent; these are Charon additions in its idiom.
- **Interaction** — sidebar switches pages, toggles flip, segmented controls switch.

## Trade-offs
- Strong at: information per screen; answering "what is it doing and what needs me" with no clicks.
- Weak at: less calm; the settings pages are 6-8 rows rather than exhaustive, so some knobs move behind
  sub-pages; a board is only as good as the status strings feeding it.

## Best for
- Someone running 4-8 boxes who wants the fleet state at a glance and rarely opens settings.
