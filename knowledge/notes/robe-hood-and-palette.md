---
title: Guide robe dye palette, and why the authored hood was dropped
game: V Rising
game_build: Steam 23521359
progenitor: PR 107 (codex/hood-palette-workflow); crash gate 6109d17f
side: both
area: wearables
tags: wearables, robe, hood, palette, transmog, dye, winding, normals, crash
route: authored chest, gloves and boots plus the native Razer hood mesh borrowed at runtime; palette read from the native registry, not the generated utility wrapper
status: verified-live
date: 2026-10-02
evidence: internal research notes (robe fitting 2026-10-01: hood and palette qualification with captures); samples/Progenitor.RobeSet/README.md
---

## Goal

Ship the Guide robes ([samples/Progenitor.RobeSet](../../samples/Progenitor.RobeSet/README.md)): a hood, chest, gloves and boots that fit the player rig and take the color a player picks in the equipment dye picker.

## Route

Chest, gloves and boots are original geometry authored in Blender and exported with explicit `surfacePalette` mappings ([wearables.md](../../docs/wearables.md)). The hood that ships borrows the equipped Razer hood mesh at runtime with owned, palette-aware materials and an authored black face insert; no native geometry is packaged. The dye color is read from the native palette registry directly.

Rejected: an authored hood (several drafts rejected on shape; one exposed the winding trap below), and calling the generated `TransmogInfoUtilities.GetEquippedTransmogColors` wrapper (crashed the client).

## What the engine did

- The equipment color picker's X cell hides the item; it is not a color reset.
- Picking a chest swatch recolored the authored chest; with `WithPaletteSource(WearableSlot.Chest)` the hood took the chest's color while the gloves kept their own.
- Surfaces without a palette mapping kept their authored color.

## Verification

**Verified live** 2026-10-02, Steam client and isolated test server: picking the top-right chest swatch turned the robe from pale cloth to red (palette-top-right.png); a capture showed hood and chest with identical cloth color. Later accepted on the female test character: native hood shape, fixed black face and chest palette inheritance (sample README). Not qualified: male binding and broad combat coverage. The game build was not recorded at the time; the stamp is the installed client build, unchanged since 2026-08-25.

## Gotchas

### Client exits with no managed exception when reading the dye color

- **Symptom:** enabling the palette-mapped garment closes the client; Windows logs an access violation (c0000005) in coreclr.dll and BepInEx logs nothing.
- **Cause:** not proved. The generated interop wrapper takes the registry and slot by reference, and the likely mechanism is that it passes an object pointer where native expects a by-ref struct.
- **Fix:** do not call that wrapper. Read the registry with `TryGet<TransmogPaletteData>` at index `TransmogIndex - StartIndex` (checked against the native function), bounds-check it, and fall back to the authored color. This read path is what the live swatch test above used.

### Authored hood crown renders black or cut off

- **Symptom:** in game the top of the authored hood was black and looked clipped, while the Blender preview looked fine.
- **Cause:** every hood shell, lip and recess face was wound inward in the authoring scene, and the exporter preserved those normals. Because the runtime material is double-sided, inward winding was not proved to be the only mechanism.
- **Fix:** after correcting the winding the crown rendered as cloth (hood-winding-live.png). The face-direction assertions live only in `samples/Progenitor.RobeSet/authoring/build_hood.py`; neither the exporter nor `dotnet build` checks winding, so check normals yourself for any new garment.
