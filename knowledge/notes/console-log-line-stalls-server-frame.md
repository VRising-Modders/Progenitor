---
title: Every BepInEx log line wrote the console on the game thread
game: V Rising
game_build: Steam 23521359
progenitor: add0d42f
side: server
area: logging
tags: logging, console, performance, hitch, loginfo, stall
route: BepInEx ConsoleLogListener replaced by Bootstrap's QueuedConsoleLogListener (bounded queue, background writer)
status: reported
date: 2026-10-19
evidence: isolated test server BepInEx/LogOutput.log of 2026-10-19 (Satisvampory slow island ticks of 991 and 1763 ms with 12 to 58 ms of measured work; ui hitch summaries naming WorldSaveGuard.Tick 127 ms and PrefabRegistry.OnRefreshSettleFrame 85 ms in frames that logged one console line)
---

## Goal

Explain dedicated-server stalls of 1 to 2.7 s that a mod's own timing could not account for, and keep logging cheap for every mod.

## Route

Find every listener in `Logger.Listeners` and what each does per line. Progenitor's own are cheap: the bridge ring (`LogRing`, one struct under a lock) and the preloader's session log (buffered, with a one-second timer flush). BepInEx's `DiskLogListener` buffers and flushes every 2 s. BepInEx's `ConsoleLogListener` is the expensive one. For each line it sets the console color, writes the line with `AutoFlush`, and resets the color, synchronously on the logging thread. The fix replaces it at Bootstrap start with a queued listener; see [bridge.md, Logging policy](../../docs/bridge.md#logging-policy). Rejected: asking every mod to log less (it does not scale), and turning the console off (players and admins read it).

## What the engine did

Reported from the log, not reproduced with a timer around the console write:

- In one mod's slow island tick, a full time of 991 ms and of 1,763 ms contained only 12 to 58 ms of measured work. The difference fell where the mod wrote its once-a-minute `LogInfo` summary of several thousand characters.
- Three short `LogInfo` lines cost about 45 ms each while a player was connected. With no player connected, the same lines cost about 1 ms.
- Progenitor's hitch summaries named `WorldSaveGuard.Tick` (127 ms) and `PrefabRegistry.OnRefreshSettleFrame` (85 ms). In each case the handler logged a single console line in that frame; its other work is small.
- Not established: why a connected player makes the console slower. On the test machine the game client runs on the same machine, which is consistent with a console host starved for CPU, but that was not measured. The gaps of 2,663 and 1,976 ms around those handlers fell during a player join with a mod hot load, and during a world save. The game's own work there is not attributed.

## Verification

Source-checked against BepInEx 6.0.0-dev (`ConsoleLogListener`, `WindowsConsoleDriver`, `DiskLogListener`). The queue, batching, truncation and buffered session log are tested game-free (`ConsoleLogQueueTests`, `SessionLogWriterTests`, `UiFrameCostTests`). Not verified live: the fix needs a Bootstrap restart of each process. After a restart, the `console=…,writeMs:` field of the `ui hitch summary` line shows how long the console writes took off the game thread.

## Gotchas

### A short LogInfo can stall the frame for tens of milliseconds

- **Symptom:** a server frame stall whose own work is small; the slow frames are the ones that logged.
- **Cause:** BepInEx's console listener writes every line synchronously on the logging thread, with color calls before and after; a slow console host blocks the write, and longer lines take longer.
- **Fix:** Bootstrap's `QueuedConsoleLogListener` (default on, `[Logging] QueuedConsole`). Check `console=` in the hitch summary; `LogDiag` still keeps per-frame diagnostics off the console altogether.

### A handler's hitch time includes the log lines it wrote

- **Symptom:** `top=[handler:WorldSaveGuard.Tick 127ms/1]` for a handler that only compares a file stamp.
- **Cause:** the handler logged a line, and the console write was charged to it.
- **Fix:** read the handler's own work before optimizing it; with the queued console the log line costs microseconds.
