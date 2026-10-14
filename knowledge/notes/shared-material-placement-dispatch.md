---
title: Build placement can bypass shared inventory counts
game: V Rising
game_build: Steam 23521359
progenitor: PR 7
side: both
area: inventory
tags: materials, building, inventory, placement
route: MaterialPlacementDispatch and scoped material count/payment seams
status: verified-live
date: 2026-10-13
evidence: ../../../../Progenitor-research/shared-resources-live-20261013.md
---

## Goal

Use remote shared containers for building while showing the same affordability in the build menu and placement preview.

## Route

Use the game's preserved synchronous placement job entry while a shared Build view is active. Retain a game-owned delegate and restore the original dispatch after the system update and on unload.

## What the engine did

The build-menu tooltip used the hooked inventory count, but the optimized placement job embedded its own count and reported missing resources. The generated non-optimized delegate still routed through the optimized wrapper. The preserved synchronous entry reached the inventory seam.

## Verification

Observed live on an isolated copied save: a Simple Workbench placed on a resource-poor plot; exact remote plank, stone and hide consumption followed reversed per-item priorities, including a split debit. A copper sword completed from remote materials. A prison feeding consumed one remote sage fish and reduced misery. These are fixed-provider tests, not qualification of a production sharing policy. Client Core replacement collected the previous generation.

## Gotchas

### Tooltip success does not prove placement affordability

- **Symptom:** Green resource counts in the menu, but missing resources on the placement preview.
- **Cause:** The placement job's embedded count bypasses the ordinary inventory seam.
- **Fix:** Scope the preserved game dispatch to the placement update while shared totals are active. Verify the actual placed entity and inventory debits.

### Invalidation must preserve recipients

- **Symptom:** Disabling a provider can leave stale client totals.
- **Cause:** Forgetting recipients prevents sending their clearing packets.
- **Fix:** Force refresh without forgetting recipients; retry transport failures. Live provider disable cleared both client views.
