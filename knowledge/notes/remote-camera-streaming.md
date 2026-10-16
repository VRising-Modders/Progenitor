---
title: Remote camera previews need both server streaming and client render origins
game: V Rising
game_build: Steam 23521359
progenitor: a6b69f69
side: both
area: ui
tags: camera, preview, streaming, observation
route: PluginCamera.Observe and PluginCamera.Preview
status: verified-live
date: 2026-10-15
evidence: progenitor-shots/20261016-014429-346.png
---

## Goal

Preview a distant authorized storage room without moving the real player.

## Route

Use a bounded server observation lease and a temporary client render anchor, alongside the camera orbit. All native offsets come from the checked layout declarations.

## What the engine did

Moving only the camera rendered terrain but did not load remote chests. The server's secondary user streaming origin admitted remote entities. The client required both singleton and cached render-origin state to draw them.

## Verification

On the stated base with observer changes, a room about one kilometer away rendered its chests, walls and floor. Client entity readback found all ten test chests. The real user's position stayed unchanged; the secondary origin returned to zero after timeout. Upstairs visibility, manual rotation and interruption cases remain unqualified.

## Gotchas

### Terrain without containers

- **Symptom:** A distant camera preview shows terrain and no room contents.
- **Cause:** Camera position alone does not update server interest or the client's cached render origin.
- **Fix:** Pair the server observation lease with the client render anchor and release both on return. Keep target authorization in the server consumer.

### Interop write rejected

- **Symptom:** A typed User write reports a zero-sized component layout.
- **Cause:** The wrapper does not supply a usable raw write size on this path.
- **Fix:** Use the existing guarded accessor with the layout-generator-verified field offset and minimum size; do not write the whole wrapper or guess its size.
