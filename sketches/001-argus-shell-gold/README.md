# Variant 001 — Argus shell, Charon gold

## Design stance
Take Argus' window chrome and component set verbatim, keep Charon's gold identity and its
sidebar's category grouping. Nothing about *where* features live changes; only what they look like.

## Key choices
- **Layout** — identity strip across the top (mark, wordmark, status pill, right-aligned detail, cog),
  160px icon sidebar at 38px rows, page title / muted subtitle / hairline, then content.
- **Typography** — ImGui font scales from Argus: page title 1.45x, header wordmark 1.62x,
  section labels 0.80x uppercase-dim, stat values 1.25x, pills 0.78x.
- **Colour** — Argus' card/border/text ramp (`CardBg`, `CardBgSoft`, `BorderDim`, four text steps)
  with the gold accent left in place: gold selection bar and wash, gold action buttons, gold tile stripe.
  Amber still means "waiting for you", mint good, rose problem.
- **Components** — stat tiles with a 3px left accent, cards with a 3px state stripe, status pills with a dot,
  label-left / control-right setting rows with hairline separators, 38x20 toggle switches, 28px action buttons.
- **Interaction** — sidebar switches pages, toggles flip, everything has a hover state.

## Trade-offs
- Strong at: familiar to anyone who has used Argus; the whole existing 19-entry nav survives untouched;
  the Debug status lines gain a natural home as tiles.
- Weak at: the sidebar is the same information architecture as today, so the win is polish plus one new page.

## Best for
- Shipping the look change without an argument about feature placement. Lowest-risk adoption path.

## New in this variant
- A landing page, **Overview**: eight stat tiles (follow / heal / quick kill / nav / weeklies / ventures /
  spawns / relay), the fleet table with one row per box and a state pill, and a "needs you" card.
  This is the Debug section's content, promoted to the front page and made glanceable.
