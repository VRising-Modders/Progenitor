---
title: Container tabs need native layout and bounded hit targets
game: V Rising
game_build: Steam 23521359
progenitor: a6b69f69
side: client
area: ui
tags: tabs, container, layout
route: Ui.AttachPanelTabs and UnityVanillaPanelTabs
status: verified-live
date: 2026-10-15
evidence: progenitor-shots/20261015-233505-331.png, progenitor-shots/20261015-233250-350.png
---

## Goal

Mount custom inventory content inside the game's container and production windows.

## Route

Clone the native character tab donor with its font and height, retain native content behind CanvasGroups, and mount a toolkit panel. Keep native inventory mappers active.

## What the engine did

Observed: the chest background uses vertical layout; adding an absolute strip overlaps its bonus section. Station root pivots differ from chest pivots and initial tab coordinates can move after the first frame. Donated label hit targets can extend into adjacent tabs.

## Verification

Built against the stated base with the tab-host changes. Chest and furnace navigation and native content restoration were checked at 3840x1080; furnace also at 1920x1080. The cited screenshots show Input selected and mounted. Core reload released the prior generation. Physical controller input was not checked.

## Gotchas

### Native layout timing

- **Symptom:** Tabs overlap the bonus row or appear near the window bottom on fresh open.
- **Cause:** Chest layout owns sibling positions; station geometry settles after creation and has a different pivot.
- **Fix:** Insert chest tabs in the layout group. Position station tabs from the bonus row's current bottom, using the root's local center each frame.

### Oversized donated hit targets

- **Symptom:** Clicking Input selects Output.
- **Cause:** The donated tab label retains a wider raycast rectangle than its assigned cell.
- **Fix:** Disable descendant Graphic raycasts, size the caption inside the cell, and use the root Image as the bounded hit target.

### Native font and baseline

- **Symptom:** Mod captions look larger and sit higher than character tabs; the silver baseline is absent.
- **Cause:** Generic label styling overwrites the donor typography, while the divider and gradient are siblings of the native buttons.
- **Fix:** Retain the donor font, size and tab height, disable caption autosizing, and clone the sibling TabGradient and TabDivider as non-interactive trim. The result was observed at 1920x1080 (progenitor-shots/20261015-234751-043.png).
