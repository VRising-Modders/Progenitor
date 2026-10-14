---
title: Serialized UI fields can be empty: the inventory has no SelfSortButton
game: V Rising
game_build: Steam 23521359
progenitor: da9b3b7f
side: client
area: ui
tags: ui, vanilla, prefab, inventory, container, attach
route: Ui.AttachToVanilla panel sites cloning InventorySubMenu.SelfSortButton
status: failed
date: 2026-10-14
evidence: internal research notes (P6 vanilla panels on 1.1.13.0, 2026-10-14); client log of the run
---

## Goal

Draw mod buttons on the container, station, inventory and castle heart windows as
clones of the game's own inventory bar button (`Ui.AttachToVanilla`, P6).

## Route

The first design read the donor and the anchors from the windows' public fields:
`InventorySubMenu.SelfSortButton` (button donor and the `BesideSort` anchor) and
`ContainerSubMenu.OpenTagEditButtonParent` (the `BesideRename` group).

## What the engine did

Observed on a chest: the window opened and closed and reported its target, but no
button appeared and the site logged
`Ui: site ContainerPanel not found (InventorySubMenu.SelfSortButton)`.
The interop has the field; the shipped prefab leaves it empty. Source-checked in the
shipped UI prefab: the bar is `ButtonsLayoutNew`, a grid of 116x36 cells holding
`DropAllButton`, `SortButtonNew` (the `AllSortButton` field), `SmartTakeButton` and
`SmartMergeButtonNew`. `OpenTagEditButtonParent` is empty too; the rename button sits in
`ButtonLayoutParent`, a 24x24 right-aligned group that keeps its children's widths.

## Verification

Failed route observed live on game 1.1.13.0, client, 2026-10-14 (chest
`TM_Castle_Container_Specialized_Minerals_T02`). A second live run of the first fix showed
the containers attached and fitted (`donor: AllSortButton`, `withheld: ""`) with no
button built, which exposed the target-graphic trap below. The current anchors and donor are
built and game-free tested (`VanillaPanelAnchorsTests`); they are not live-verified.

## Gotchas

### A field in the interop is not a reference in the prefab

- **Symptom:** A window resolves and reports open and close, but nothing is drawn; one
  `not found (<field>)` line.
- **Cause:** Serialized references can be left unset in a game build. The field exists,
  its value is null.
- **Fix:** Resolve with a fallback chain from the hierarchy (`AllSortButton`, the bar's
  buttons; the rename button's own parent) and log what was tried. See
  `docs/ui.md` "Anchors on game build 1.1.13.0".

### A bar button can drive a sibling's image

- **Symptom:** With `AllSortButton` as the donor, the containers attached and fitted
  (live 2026-10-14) but every button read `built:false` and nothing was drawn; no warning.
- **Cause:** On 1.1.13.0 `SortButtonNew`, `SmartTakeButton` and `SmartMergeButtonNew` have
  `DropAllButton`'s image as their target graphic. The clone gate refuses a button whose
  state targets lie outside its own subtree, and the refusal was silent.
- **Fix:** Take only a clone-safe donor (`DropAllButton` on this build) and log every
  clone refusal once with its reason.

### Joining a fixed-size group moves the game's controls

- **Symptom:** (expected, not yet seen) a mod button pushes the rename button over the
  window title.
- **Cause:** A right-aligned horizontal group that does not size its children shifts all
  of them when a wider child joins.
- **Fix:** Put the mod container beside the group (a child of the group's parent) and place
  it after the group's last member. The same holds for the inventory bar: it is a centered
  grid, and one more cell would move all four game buttons.
