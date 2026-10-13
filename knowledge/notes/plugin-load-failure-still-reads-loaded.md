---
title: A plugin whose OnLoad threw still reads loaded to the reloader, and the held-connection flag outlives the world
game: V Rising
game_build: Steam 23521359
progenitor: 4829e4cc
side: client
area: hot-reload
tags: auto-update, reload, onload, rollback, join, window
route: ClientUpdates hot update wait (Bootstrap admission snapshot) and the auto-update apply window (Handshake.ClientConnectionHeld)
status: verified-live
date: 2026-10-09
evidence: internal research notes (auto-update phase 2, 2026-10-09, with the client log lines)
---

## Goal

Decide, after an automatic hot update, whether the new plugin really loaded (else roll back), and decide when the client is idle enough to apply updates (the title screen, right after leaving a server).

## Route

The wait reads Bootstrap's admission snapshot for the plugin (`PluginLoaded` and the loaded image's sha256), then Core's `ModRegistry` outcome for the same id. The apply window reads the world, the join flow, the pre-join hold, the loader and the version store. Rejected: the snapshot alone (it cannot see an `OnLoad` failure) and `Handshake.ClientConnectionHeld` alone (stale after a leave).

## What the engine did

Observed live on the Steam client:

- A test build that throws in `OnLoad` logged `OnLoad threw; undoing registrations`, yet the snapshot reported `PluginLoaded` with the new image's sha256. `ProgenitorPlugin.Load` catches the exception and calls `ModRegistry.NoteFailed`; the reloader keeps the assembly.
- After a bridge `disconnect`, the client world went away but `Handshake.ClientConnectionHeld` stayed true, so a window built on it read "a join is in progress" indefinitely at the title screen.

Source-checked: the held flag is updated in a frame watch that returns early when `Worlds.Client` is null, so it keeps its last value once the world is destroyed without the client state passing through Idle in a frame the watch sees.

## Verification

Verified live 2026-10-09 on the Steam client (research note above): with the registry check the failing build was rolled back (`op=rollback by=auto … why="Its load threw an exception"`), and with the world-gated flag an update was applied two seconds after leaving the test server. Game-free: `ClientAutoUpdateTests`.

## Gotchas

### A failed OnLoad still looks loaded

- **Symptom:** a hot update whose plugin throws at load is reported loaded; nothing rolls back.
- **Cause:** the reloader's view (snapshot `PluginLoaded`, image sha256) is about the assembly, not about `OnLoad`; `ProgenitorPlugin.Load` swallows the exception.
- **Fix:** also read `ModRegistry.TryGet(id).Outcome`: `Failed` is a failed load, `Loading` means keep waiting.

### The held-connection flag outlives the world

- **Symptom:** after leaving a server the client reads "a join is in progress" forever.
- **Cause:** `Handshake.ClientConnectionHeld` is not cleared when the client world disappears.
- **Fix:** trust it only while a client world exists (`ClientConnectionHeld && Worlds.Client != null`), as the apply window now does.
