---
title: Refit native sections after character sheet row changes
game: V Rising
game_build: Steam 23521359
progenitor: cba183e02
side: client
area: ui
tags: character-sheet, layout, rows
route: Native grid refresh followed by scoped layout rebuild
status: verified-live
date: 2026-10-12
evidence: internal research notes (character sheet layout, 2026-10-12)
---

## Goal

Keep added Attributes rows below their headings, using native art and scrolling.

## Route

After the next native mapper pass creates the added entries, rebuild section grids,
then the Attributes scroll content. Rebuild only for structural changes, not stat values.

## What the engine did

Observed: grids grew to fit extra rows, but retained positions calculated for the old
heights. Defense's 268-unit grid remained at y=-94.21 instead of -169.21, covering its
heading. A scoped native layout rebuild corrected all expanded sections.

## Verification

Build and nine focused tests passed. On game 1.1.13.0+r99712+b17/net25, client Core
reload completed with its previous generation released. Readbacks covered all nine
sections after reload and close/reopen. Captures show Physical, Defense, Health &
Healing, Utility and Cooldown with clear spacing and native selected-row tooltips.
Calling the removal path while open restored original row counts and positions.
Performance and exhaustive tooltip/localization coverage were not tested.

## Gotchas

### Growing grids keep stale positions

- **Symptom:** Rows cover headings despite correct text and values.
- **Cause:** Native row refresh and content sizing do not finish section positioning together.
- **Fix:** Rebuild grids then their scoped content root after entries exist. On removal,
  retire owned cached/pooled rows before measuring; changing the configuration alone
  leaves stale visual entries until another mapper pass.
