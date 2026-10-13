---
title: A hosted private game refuses the Core swap; deploy must hold plugins
game: V Rising
game_build: Steam 23521359
progenitor: daf85ad0
side: client
area: hot-reload
tags: deploy, core-swap, hosted, private-game, bridge, typeloadexception, gate, hold, watch
route: tools/deploy.ps1 -Projects Progenitor.Core,Progenitor.Dev -Side client while the Steam client hosted a private game
status: verified-live
date: 2026-10-06
evidence: client BepInEx log of the failed deploy (lines quoted below; that LogOutput.log rotated at the next client start), bridge answers busy:reload_status#1; fix and tests on branch fix/deploy-hold-on-refused-swap
---

## Goal

Deploy a new Core plus a Dev that uses a new Core API to the Steam client and have both running, without restarting the game.

## Route

`tools/deploy.ps1` staged `progenitor-core\Progenitor.dll.next` and held the plugin DLLs (the FDEP1 hold) until the bridge showed the swap. The client was hosting a private game at the time. Rejected: deploying while the client is hosting at all, because nothing in deploy said it would not work.

## What the engine did

Observed live:

- Bootstrap's watcher logged `Core watch refused: this client is hosting a private game; the child keeps the copied Core.` once and left `Progenitor.dll.next` on disk.
- The hold then copied `Progenitor.Dev.dll` into `progenitor-reload` anyway. Under the old "copy anyway" rule it did that on any ending except a landed swap.
- The new Dev loaded against the old Core. `HostCapture.Install` threw `TypeLoadException: Could not load type 'Progenitor.DiagnosticLog'`.
- The bridge then answered every main-thread op with `a main-thread op is already running`. The heartbeat label stayed on `busy:reload_status#1` until the process restarted.

Source-checked:

- The hosting refusal (`CoreWatchDecision.RefuseHosting`; `RefuseHosted` in the child) does not park and does not write `lastReload`. So `previousAlive`, `lastReload.outcome` and `watch.parked`, the fields the hold already read, all look like a swap that has not happened yet.
- The only wire signal is `reload.watch.hostingPrivateGame` (`hostedChild` in the child). `ping` and `reload_status` both carry it, read live from `ProcessHost` (`CoreLoader.DescribeStatus`).
- The stuck gate came from Dev's gate logging. In a Dev build that calls `ManualLogSource.LogDiag` (Core's `DiagnosticLog`), the JIT failure of `MainThreadOpGate.Log` surfaced inside `TryEnter`. That was after the slot CAS and before the caller's `try/finally`, so `_inFlight` stayed 1 for the life of the process.

## Verification

- Live: the failure above, observed on the Steam client on 2026-10-06. The fix has not been run live.
- Offline: Debug build, then `dotnet test`. `DeployHoldTests` runs deploy.ps1's own wait functions against stubbed bridge answers: hosted, hosted child, refused, deadline, landed swap and `-ReleaseHeldPlugins`. `MainThreadOpGateTests.A_logging_failure_inside_TryEnter_does_not_strand_the_slot` covers the gate.

## Gotchas

### A hosted game refuses the Core swap without a refusal record

- **Symptom:** the deploy hold waits out its timeout, or releases plugins against the old Core.
- **Cause:** `RefuseHosting` and `RefuseHosted` are silent apart from one log line and `watch.hostingPrivateGame` / `watch.hostedChild`.
- **Fix:** deploy reads both flags before staging and on every poll. On any ending other than a landed swap it withholds the plugins and exits 3. Leave the hosted game (the watcher then takes the staged Core) or restart, then deploy again. `-ReleaseHeldPlugins` restores the old copy-anyway behavior.

### A plugin built against a newer Core can wedge the Dev bridge

- **Symptom:** `a main-thread op is already running` forever, heartbeat `busy:<op>#<id>`.
- **Cause:** an exception between the gate's CAS and the caller's `try/finally`. Here it was a JIT `TypeLoadException` from a logging helper.
- **Fix:** `TryEnter` logs through `SafeLog` (with `Log` not inlined) and releases the slot if anything else throws. The cleaner fix is still not to load a plugin before its Core: see the gotcha above.
