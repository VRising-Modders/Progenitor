---
title: Build mode's Escape is Building_Exit, not Menu_Close
game: V Rising
game_build: Steam 23521359
progenitor: 07bad52d (failing route); fix in branch fix/build-search-escape
side: client
area: ui
tags: escape, build-mode, input, input-context, build-search
route: menu-hotkey guard input context (order 498) holding Back, Menu_Close and Building_Exit, plus a close-key input hold so the context exists
status: failed
date: 2026-10-18
evidence: live report on the guide client (Progenitor 2026.10.128, main 07bad52da), 2026-10-18; game input asset probe in the internal research notes (game-input-asset)
---

## Goal

Keep one Escape away from the game while the build search owns it: with text in the search box and the box not focused (Copy building filled it), Escape clears the search and build mode stays on.

## Route

The typed-key pump turns on the close hold from the frames before the press, and the menu-hotkey guard context at input order 498 applies it. Rejected: holding only `Back` (17) and `Menu_Close` (1004), the set that works for the game's full-screen menu and chest windows. That route failed live.

## What the engine did

- Observed live: search filled by Copy building, box unfocused, placement cancelled with Space, then one Escape. The build menu closed and the game left build mode.
- Source-checked (game input asset): on keyboard and mouse, Escape is bound to `Back` (Core map), `Building_Deselect` (304) and `Building_Exit` (309) (Building map). `Building_Deselect` is also Space. `Building_Exit` is not offered for rebinding; on a gamepad it is B.
- Source-checked (client native code): the build menu's own input context registers at order 502 and reads `Building_Deselect` from `InputsDown` to drop the current placement; it does not leave build mode. So the Escape that leaves build mode is `Building_Exit`, read by build mode's own input context. Its registration order was not read; the game's order list names `BuildMode` = 602, after 498.
- Source-checked: the 498 guard context lives only while some input hold or capturing panel wants Progenitor's input context. An unfocused build search had none, so the hold flag was set with no context to apply it.

## Verification

Not live-verified after the fix. Game-free unit tests cover the held action set, the keyboard-only rule and the close-key hold decision (`BuildMenuBlobTests`); `dotnet build` and `dotnet test` pass. The failing route was observed live once on the guide client.

## Gotchas

### Holding the menu close actions does not stop build mode's Escape

- **Symptom:** Progenitor takes an Escape in build mode (to clear a search, answer a dialog or end a key capture), and the game still leaves build mode and closes the build menu.
- **Cause:** In build mode Escape is also `Building_Exit` (309), a different action from `Back` and `Menu_Close`.
- **Fix:** Hold `Building_Exit` as well, on keyboard and mouse (`MenuHotkeyGuard.BlockedBuildModeCloseActions`). Leave `Building_Deselect` alone: it is also Space, and a whole-frame hold would take the player's Space deselect away.

### A hold flag needs a registered context

- **Symptom:** The close hold reads true, yet nothing is blocked.
- **Cause:** The 498 guard context is registered only while a panel, a close-key hold or an exclusive hold wants input capture.
- **Fix:** Take `UiInputCapture.AcquireCloseKey` while the hold's reason is live, as the build search now does, and release it on every exit path.
