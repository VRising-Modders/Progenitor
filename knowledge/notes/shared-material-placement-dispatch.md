---
title: Build placement can bypass shared inventory counts
game: V Rising
game_build: Steam 23521359
progenitor: PR 15
side: both
area: inventory
tags: materials, building, inventory, placement
route: MaterialPlacementDispatch and scoped material count/payment seams
status: verified-live
date: 2026-10-13
evidence: internal research notes (shared resources live checks, 2026-10-13)
---

## Goal

Use remote shared containers for building while showing the same affordability in the build menu and placement preview.

## Route

Use the game's preserved synchronous placement job entry while a shared Build view is active. Retain a game-owned delegate and restore the original dispatch after the system update and on unload.

## What the engine did

The build-menu tooltip used the hooked inventory count, but the optimized placement job embedded its own count and reported missing resources. The generated non-optimized delegate still routed through the optimized wrapper. The preserved synchronous entry reached the inventory seam.

## Verification

Observed live on an isolated test server with a copy of a save: a workbench placed from the build menu on a resource-poor plot; the remote plank, stone and hide debits followed the configured per-item order, including one item split across two chests. A workbench craft completed from remote materials, and a prison cell fed one fish from a remote chest. These used a fixed test provider, not a production sharing policy. A client Core swap collected the previous generation.

## Gotchas

### Tooltip success does not prove placement affordability

- **Symptom:** Green resource counts in the menu, but missing resources on the placement preview.
- **Cause:** The placement job's embedded count bypasses the ordinary inventory seam.
- **Fix:** Scope the preserved game dispatch to the placement update while shared totals are active. Verify the actual placed entity and inventory debits.

### Invalidation must preserve recipients

- **Symptom:** Disabling a provider can leave stale client totals.
- **Cause:** Forgetting recipients prevents sending their clearing packets.
- **Fix:** Force refresh without forgetting recipients; retry transport failures. Live provider disable cleared both client views.
