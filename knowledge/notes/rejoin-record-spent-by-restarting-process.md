---
title: A restarting game can spend its own rejoin record before it quits
game: V Rising
game_build: Steam 23521359
progenitor: 830e5a46
side: client
area: lifecycle
tags: rejoin, restart, record, pickup, join, core swap, already-used
route: Mods menu pending-restart bar, Restart and rejoin, progenitor-rejoin.json pickup at the title screen
status: failed
date: 2026-10-19
evidence: old session BepInEx progenitor-logs session-1.log 2026-10-19 (bridge connect at startup, then rejoin record at start rejoin in the same process before the quit), new session LogOutput.log (rejoin record at start already-used), BepInEx progenitor-restart.log
---

## Goal

After **Restart and rejoin** in the Mods menu's Install bar, the relaunched game reconnects to the server the player was on.

## Route

The press writes `progenitor-rejoin.json` (`used false`), leaves the world the game's own way, starts the relauncher and quits. The next process's start pickup, once per Core generation when the title screen is ready, marks the record `used` and reconnects.

## What the engine did

Observed live (Steam client, Core 2026.10.147, Bootstrap 2026.10.13): the restart helper logged a clean wait, Steam idle and a new game start, but the new session logged `JoinFlow: rejoin record at start: already-used`, deleted the record and did not rejoin.

Observed in the old session's log: the Dev bridge `connect` joined the server right at startup, before the start pickup ever saw the title screen ready; only one Core generation ran. At the restart, `leaving the world first` was followed in the same process by `after the restart at start` (the install-restart record consumed), `rejoin record at start: rejoin` and the **Rejoining** dialog, just before the quit.

Source-checked cause: the start pickup is gated per Core generation, not per process. Any join before its first title-screen tick leaves it pending: a bridge or command-line connect at startup, a quick player, or a Core swap while in the world. Leaving the world for the restart landed on the title screen in the old process, and its pickup took the records that process had just written.

## Verification

Bug: observed live once on 2026-10-19 in both sessions' logs (lines above). Fix (REJOINSELF1): built and game-free tested in `InstallRestartTests` (a real restart marks the pickup done; a Core swap during the leave keeps the record; another process rejoins once) and `JoinFlowStage2Tests` (same-process record untouched, another process takes it once, records without `writer` still work). Verified live in part on 2026-10-19 (Steam client, Core 2026.10.150 (main 22bf979f6)): a mod uninstalled from the Mods page and the pending-restart bar's restart relaunched the game, which rejoined the server by itself. Not observed: the join screens' Restart and rejoin, and the start pickup answering `own-process`.

## Gotchas

### The writer's own title screen consumes the record

- **Symptom:** the game restarts, but the new session logs `rejoin record at start: already-used` and stays at the title screen.
- **Cause:** a once-per-generation flag cannot tell "this process just wrote the record" from "this process just started". The Leave before the quit is a title screen in the writing process.
- **Fix:** a real restart marks its own generation's pickup done, and the record carries `writer` (process id and start time). The start pickup answers `own-process` and leaves the file untouched for a record its own process wrote. The bridge's `rejoin_now` skips the check, so same-process dry runs still work.

### Core generation state is not process state

- **Symptom:** a fix that only sets the pickup flag still fails after a Core hot swap during the leave.
- **Cause:** the swap resets the generation's flags (`_rejoinChecked`, `_relaunching`); the process is the same.
- **Fix:** identify the process from the process itself (`Process.StartTime` with the id), which every Core generation reads the same way, rather than from a Core static.
