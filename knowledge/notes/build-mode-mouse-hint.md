---
title: Build controls need contextual conflicts and native mouse art
game: V Rising
game_build: Steam 23521359
progenitor: 15499039
side: client
area: ui
tags: build, keybind, mouse, inventory
route: Build-scoped Keybinds and loaded ControlsVisualMapping
status: verified-live
date: 2026-10-15
evidence: external research build-menu-recent-20261015.md; build-menu-native-mmb-1080-20261015-2.png
---

## Goal

Make Copy building rebindable in build mode, using the game's middle-mouse graphic.

## Route

Register through Keybinds with Build scope. Borrow the matching sprite from a loaded ControlsVisualMapping asset; keep the hint clone owned and disposable.

## What the engine did

Observed: the default middle button was blocked first by RepairItem and then SplitItem because General controls had been treated as always-active gameplay. These inventory actions do not apply to the build menu. InputActionSystem's mapping field was unavailable in the tested world, but the loaded mapping asset contained `<Mouse>/middleButton` with sprite `Icon_MMB1`.

## Verification

Working changes on the stated base, Core generation 20: a registered press selected wooden stables floor blueprint 2025363113 from the hovered building. The same press with the menu closed was suppressed. Readback identified Icon_MMB1; screenshots at 1920x1080 and 3840x1080 showed the graphic alongside native hints. Both prior Core generations collected after reload. Scope/conflict tests passed. This does not qualify Recent history ingestion or every building variant. Recent history ingestion was verified live separately on 2026-10-18 (release package 2026.10.128, cold install): real placements reached the list and a rebuild moved to the top.

## Gotchas

### Settings category is not an input context

- **Symptom:** an inventory shortcut blocks middle click while building.
- **Cause:** General groups actions by settings presentation, not runtime context.
- **Fix:** classify the seven inventory-only General actions as inventory scope. Keep actual gameplay/build conflicts and warn about Push To Talk sharing.

### Mapping field unavailable

- **Symptom:** the hint falls back to text despite an existing game icon.
- **Cause:** the current InputActionSystem has no mapping reference.
- **Fix:** find the loaded mapping asset with the requested path and live sprite. Cache a borrowed reference and clear it when releasing the owned hint; never destroy the game asset.