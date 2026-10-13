---
title: A borrowed sprite matched by name alone can be another menu's art, and the HUD's Button_Normal drew every Mods button flat red
game: V Rising
game_build: Steam 23521359
progenitor: cb9d9a46
side: client
area: ui
tags: button, art, sprite, red, core, swap, hot-reload, resident, loaded-sprite, button_normal, iconcache, kept, settings-kit
route: SettingsArtDonors / KeybindArtDonors resident rung (Resources.FindObjectsOfTypeAll(Sprite), match by name) after a client Core swap in a world
status: verified-live
date: 2026-10-06
evidence: user screenshot 2026-10-06 (Core 2026.10.85.1 swapped in a world, Mods > Server); client LogOutput.log lines "Settings kit: art menuButton live=menu found, no SimpleMenuButton_Normal cached=none kept resident=ok -> resident" and "Keybinds tab: art keyCap ... resident=no donor prefab or named sprite loaded -> kit"; asset bundles a1620494 / fd6610a9 / 3d198ee3 read with _tools/scan UnityPy
---

## Goal

Draw the Mods workspace's buttons with the game's steel menu button (`SimpleMenuButton_Normal`) after a client Core hot swap made while the client is in a world, as they are after a swap at the title.

## Route

The settings kit and the Keybinds tab borrow game sprites through a ladder: live donor menu, owned GPU copy, held original, resident prefab or a loaded sprite found by name (`Resources.FindObjectsOfTypeAll(Sprite)`), then the kit. A Core swap destroys the owned copies. In a world the title's menus and their textures are gone, so the new generation falls to the loaded-sprite rung. The menu button part also accepted the alternate name `Button_Normal` (the Options page's Back button).

## What the engine did

- After the swap in a world, no `SimpleMenuButton_Normal` sprite was loaded (the Keybinds tab's key cap logged `no donor prefab or named sprite loaded`), yet the settings kit's `menuButton` resolved `resident`. The only other name it accepted was `Button_Normal`.
- The game ships three different sprites named `Button_Normal`: the Options Back button (152x50, border 48/0/88/0, steel, bundle `fd6610a9`), a main-menu frame (40x40, bundle `3d198ee3`) and a HUD square (35x35, border 12 on every side, flat dark red, bundle `a1620494`). Only the HUD one is loaded in a world. Its owned copy became the art of every game button.
- Separately, the parked sidebar (Overview / refresh, Close Mods) was built about 180 frames before the idle art preparation finished. `SetGameButton` painted only when art was ready and never retried, so those kept the kit's dark maroon fill (`#241a20`).
- The Keybinds ornament (`Divider_Big_04`) and category rule (`Divider02_LeftToRight`) fell to the kit for the same reason as the key cap: the copies made at the title died with the old Core, and the loaded-sprite rung did not look for those names, although both live in the HUD bundle and are loaded in a world.

## Verification

- Observed live (Steam client, isolated test server, 2026-10-06): Show, View mods, Apply, Preview, Change and Close Mods drawn as flat red rectangles after swapping Core 2026.10.85.1 in a world. The same buttons were steel after a swap at the title followed by a join.
- The sprite shapes and colors above were read from the installed asset bundles offline (UnityPy), not read back from the running client. The readback that would confirm which `Button_Normal` was taken is `mods_ui` `settingsArt.parts.menuButton.sprite`, which the live run did not capture.
- The fix (branch `fix/button-art-after-world-swap`) is game-free tested (`ButtonArtKeptTests`) and built. It is **not** live-verified yet.

## Gotchas

### A sprite name is not an identity
- **Symptom:** a borrowed part draws some other menu's art, here a flat red square on every game button, only in a world.
- **Cause:** the loaded-sprite rung matched a generic name (`Button_Normal`) with no donor menu around it, and the game reuses that name for unrelated sprites in different bundles.
- **Fix:** match bare loaded sprites by their unique primary names only (`SettingsArtLadder.PrimaryPartOf`), keep alternates for images a donor menu names, and require a button's shape (horizontal-only 9-slice, at least twice as wide as tall: `SettingsArtLadder.ShapeFits`).

### Owned copies die with the Core generation
- **Symptom:** art that drew at the title is missing or wrong after a Core swap in a world.
- **Cause:** `OwnedSprites.Release` destroys every copy on Core unload, and a world has no donor to copy from again.
- **Fix:** hand each copy's pixels to Bootstrap's process-lifetime `IconCache` (`BootstrapLink.TryGetOrCreateSprite`), with the part and the sprite's look encoded in its path (`GameArtKept`); a later generation adopts it from the table sprite's name during its resident scan (the `kept` rung). No Bootstrap change is needed.

### A game button painted before its art arrives stays the kit's
- **Symptom:** Close Mods and Overview / refresh keep a flat maroon fill after a swap, while buttons built later look right.
- **Cause:** the parked list is prebuilt before the idle art preparation finishes, and `SetGameButton` did not retry.
- **Fix:** paint a neutral steel face (`GameSettingsLook.NeutralButtonFill`), retry on idle frames and repaint pending game buttons at every panel show.
