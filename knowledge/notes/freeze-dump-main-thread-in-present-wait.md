---
title: Client freeze dumps whose main thread waits for the last presentation are a GPU present stall, not Progenitor code
game: V Rising
game_build: Steam 23521359
progenitor: 692b6a0ad
side: client
area: performance
tags: freeze, hitch, dump, present, d3d11, main-thread, bridge, symbols
route: the Dev bridge's freeze dump opened in cdb with the Unity symbol server on the symbol path; k on thread 0, then !clrstack
status: verified-live
date: 2026-10-07
evidence: client BepInEx/progenitor-dumps freeze-20261007T051804270Z, freeze-20261008T005550226Z, freeze-20261008T022421704Z, freeze-20261008T030130041Z, freeze-20261010T132645843Z and every other freeze dump from 2026-10-06 to 2026-10-10; client LogOutput.log hitch summaries
---

## Goal

Name what blocked the client's main thread for 39 s to 214 s during live tests on 2026-10-07 (a bridge op waiting 30 s, `ui hitch summary … max:39525`), suspected to be new Progenitor code (a process scan, the notice's Open Install Mods).

## Route

Open the freeze dump with cdb (`-z <dump>`), put the Unity symbol server on the symbol path (`-y srv*<cache>*http://symbolserver.unity3d.com/`), and read thread 0 with `k`, then `!clrstack`. Without Unity's symbols the main thread only reads `UnityPlayer!UnityMain+0x100b80`, which names nothing.

## What the engine did

Observed on the Steam client:

- All four dumps from two days show the same main-thread stack: `UnityPlayer!GfxDeviceD3D11::WaitForLastPresentation` under `TimeUpdateWaitForLastPresentationAndUpdateTimeRegistrator::Forward` in `ExecutePlayerLoop`, waiting in `WaitForMultipleObjectsEx`. One dump predates every change under suspicion.
- `!clrstack` on thread 0 shows no managed frame: no Progenitor (or other mod) code was running on the main thread.
- `UnityGfxDeviceWorker` was idle (waiting for commands); the job workers and the managed threads (the bridge's accept loop, a bridge op waiting on the main thread, the freeze watchdog) were idle too.
- So the frame loop was waiting for the GPU to finish presenting the previous frame. That wait does not end while the presentation is held back, which is what a minimized or fully hidden game window under the flip present model does. The freezes line up with the game window being off screen during focus-free testing.

### 2026-10-10: "the heartbeat stopped but the game kept running"

- After a bridge `disconnect` to the title screen, `tick hb` stopped at frame 109842, bridge ops timed out after 30 s, the watchdog dumped at frame 109849, and Windows showed the game as Not Responding. It looked as if only Progenitor's per-frame hook had stopped.
- It had not: the frame number is the engine's own frame count. The next heartbeat was `frame=109850`, and the hitch summary read `intervalMs=max:479830`. The engine ran one frame in 480 s. The held ping finished with `elapsedMs=476782`, and frames then ran normally until the game was closed.
- The dump's main thread was the same presentation wait, with no managed frame. The other 18 client freeze dumps from 2026-10-06 to 2026-10-10 show the same stack. Ten of them predate the feature branch that was running at the time, so that branch did not cause it.
- The stall blocks `MainMessageLoop` as well, so Windows marks the window Not Responding. A game that looks fine later was observed after the stall ended.

## Verification

**Verified live** 2026-10-07 on the four dumps above, and 2026-10-10 on all nineteen client freeze dumps from 2026-10-06 to 2026-10-10 (Steam client). Not established: which window state held the present each time. Since `fix/frame-tick-stall`, the watchdog logs `freeze began … window=… userIdleS=… hint=…` and `freeze ended frozenForMs=…`, so the next stall records the window state and the input idle time. The coordinator's 63.7 s freeze after a disconnect has no dump (the per-session cap of 3 was reached), so its stack is unknown.

## Gotchas

### An unsymbolized UnityPlayer frame looks like a hang in our code path

- **Symptom:** thread 0 is `UnityPlayer!UnityMain+0x100b80` in `WaitForMultipleObjects`, right after a bridge op, so the op looks guilty.
- **Cause:** without Unity's symbols the frame names nothing, and the timing points at whatever ran last.
- **Fix:** load Unity's symbols before blaming a code path; `!clrstack` empty on thread 0 means no managed code was on the main thread.

### The rule still holds

- **Symptom:** a process scan or command-line read on the frame can still cost tens of milliseconds every few seconds.
- **Cause:** route and rejoin decisions run on the main thread and used to scan `VRisingServer` processes and read their command lines there.
- **Fix:** Progenitor reads the running server and this PC's server facts from caches refreshed on the thread pool (`ServerProcessControl.RunningCache`, `LocalFolderFactsCache`); only an explicit one-shot user action (Force stop and its wait for exit) scans on the frame.
