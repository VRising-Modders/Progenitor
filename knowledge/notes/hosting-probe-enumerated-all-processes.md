---
title: Hosting probe enumerated all processes every second: 80 ms hitches
game: V Rising
game_build: Steam 23521359
progenitor: daf85ad0
side: client
area: performance
tags: stutter, hitch, frame-time, hosting, private-game, process-scan, mainmodule, bootstrap, GetProcesses
route: Bootstrap CoreWatch.TickCore -> ProcessHost.RefreshHostingPrivateGame -> HostedChildProbe (was Process.GetProcesses + MainModule per process)
status: verified-live
date: 2026-10-06
evidence: live frame-time bisect 2026-10-06 (vanilla, BepInEx only, Bootstrap with every Core module skipped, while hosting a private game); fix on branch fix/hosting-probe-hitch
---

## Goal

Keep `ProcessHost.IsHostingPrivateGame` true while this client's hosted private-game server child runs, so `WatchCore` does not swap Core under it, and clear it when the child exits.

## Route

Bootstrap's frame ticker runs `CoreWatch.TickCore` once a second. It called `ProcessHost.RefreshHostingPrivateGame`, which, while the flag was set, called `HostedChildProbe.AnyLive`: `Process.GetProcesses()`, then for each process `Process.MainModule`, `OpenProcess` + `QueryFullProcessImageName` and an `NtQueryInformationProcess` command-line read, until the child matched. All of it on the main thread.

Replaced by `HostedChildTracker`: identify the child once on a background thread (the PID the `StunProcess.Start` finalizer reads, else a scan pre-filtered by the `VRisingServer` process name, both matched by path + argv), keep an open handle, and poll it each tick with `WaitForSingleObject(handle, 0)`.

## What the engine did

Observed: hosting a private game, the client showed 80–90 ms frames, 1–25 per 30 s. A vanilla run and a BepInEx-only run were smooth. Bootstrap plus a Core with every module skipped still stuttered, which isolated Bootstrap's per-second work. The hitch count varied with where the child landed in the process enumeration, as expected from a scan that stops at the first match.

Source-checked: `MainModule` walks the target's module list and throws on access denied, so it is the most expensive member per process; `QueryFullProcessImageName` gives the same path.

## Verification

- Verified live (2026-10-06): the bisect above on the Steam client while hosting a private game.
- Built offline and unit-tested (`HostedChildTrackerTests`): the main-thread tick never calls the scan, known-child exit clears, a failed scan keeps the flag, the PID-reuse guard. The fixed build has not been run in the game yet; confirm smooth frames while hosting after the Bootstrap restart.

## Gotchas

### Per-second process work on the main thread

- **Symptom:** 80–90 ms frame spikes only while hosting a private game, irregular in count.
- **Cause:** A once-a-second liveness check enumerated every process and read `MainModule` and the command line of each on the main thread.
- **Fix:** Identify the child once off the main thread, hold its handle, and poll it non-blocking. Never call `Process.GetProcesses`, `GetProcessesByName` or `MainModule` from a frame or periodic main-thread path.

### Bisect with an empty Core before blaming modules

- **Symptom:** Stutter that looks like a mod's per-frame work.
- **Cause:** Bootstrap runs its own periodic work regardless of which Core modules load.
- **Fix:** Compare vanilla, BepInEx only, and Bootstrap with every Core module skipped; a stutter that survives the last one is in Bootstrap.
