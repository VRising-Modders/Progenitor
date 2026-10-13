---
title: Outside-tick hitches: measure seams, collectors and IL2CPP bytes per source before blaming GC
game: V Rising
game_build: Steam 23521359
progenitor: 0632396e
side: client
area: perf
tags: perf, hitch, stutter, gc, il2cpp, boehm, outside, seams, hosted, unverified-live
route: Dev perf op (il2cpp, clrGc, patches, seams, slowestFrames) over IL2CPP.il2cpp_gc_get_used_size and PerfSeams
status: reported
date: 2026-10-06
evidence: perf captures and LogOutput.log "ui hitch summary" lines on a Steam client hosting a private game (127.0.0.1:9876), build 2026.10.87.2
---

## Goal

Find why a client hosting a private game stutters with Progenitor loaded when Progenitor's own timed frame work is tiny (p99 0.39 ms): 1 to 16 frames of 80 to 90 ms per 30 s, every one "outsideOnly". A live bisect on 2026-10-06 named Bootstrap's 1 s hosted-child process scan (fixed separately). **UNVERIFIED LIVE**: the new perf fields below have not yet been run in the game; the IL2CPP GC hypothesis was not the cause.

## Route

`perf start` then `stop` (Dev bridge), reading `slowestFrames[].largestGap` and `gc`, `il2cpp.gcFrames`, `il2cpp.bytesPerSecondBySource` and `patches`. Rejected: inferring the cause from `progenitorMs` (it only covers the Plugin.Frame dispatch, hooks and events), and from the IL2CPP GC count alone.

## What the engine did

- Observed: the IL2CPP (Boehm) collection count rose about once per hitch at first (4 in 72 s), but later 24 collections in 400 s against a hitch every 4 to 5 s, and a forced `Il2CppSystem.GC.Collect()` took 28.8 ms (heap 181 to 155 MB). A full collection alone does not explain a steady 80 to 87 ms.
- Observed: the hitch threshold is 80 ms and the median hitch was 83 to 91 ms, so the real spike is a recurring ~70 to 90 ms frame that only sometimes crosses the line.
- Source-checked: every Il2CppInterop method or getter that returns a value type goes through `il2cpp_runtime_invoke`, which boxes the return on the IL2CPP heap (`Time.frameCount`, `World.IsCreated`, `List.Count`, `UnityEngine.Object` `==`/`!=` null checks). Per-frame UI and world lookups made about 100 such boxes per frame before the fixes in this change; a few hundred KB/s at most.
- Observed by live bisect: Bootstrap's `CoreWatch.TickCore` calls `ProcessHost.RefreshHostingPrivateGame` once a second while hosting, which runs `HostedChildProbe.AnyLive` on the main thread outside Core's tick (`Process.GetProcesses()` plus `MainModule` and command-line queries per process). That was the hitch.

## Verification

The hitch cause was found by live bisect (Bootstrap probe). The instrumentation is offline only: `dotnet build` and the unit suite pass (report shape, seam gaps, GC accounting). Nothing here was observed with the new instrumentation yet. Run `perf start` for 60 s in the hosted world, `stop`, and read the fields above; update this note's status after that.

## Gotchas

### progenitorMs cannot see outside-tick work

- **Symptom:** every hitch is `outsideOnly` and Progenitor looks free.
- **Cause:** Bootstrap's drain, `CoreWatch`, Harmony trampolines on every system update, the late-frame slot and other BepInEx plugins all run outside the timed dispatch.
- **Fix:** read `slowestFrames[].largestGap` (which two seams held the time) and `patches` before concluding anything. A gap from `bootstrapTick.drain` to `coreTick.start` is Bootstrap's own pre-tick work (drain, CoreWatch, plugin scan), which is where the hosted-child scan ran.

### The GC count correlates with hitches but is not the hitch

- **Symptom:** IL2CPP collections roughly line up with hitches.
- **Cause:** a busy main thread that allocates (process scans, boxed interop returns) both stalls and triggers collections; a forced full collection measured 29 ms, not 80.
- **Fix:** look at `gcFrames[].frameMs` next to frames without a collection; attribute bytes with `bytesPerSecondBySource` and treat `outside` as everything no timed callback was running for.

### IL2CPP used size moves in heap-block steps

- **Symptom:** per-source IL2CPP bytes look noisy or zero for small callbacks.
- **Cause:** `il2cpp_gc_get_used_size` is heap size minus free heap blocks, so it moves when a new block is taken, not per object.
- **Fix:** compare sources over a 30 to 60 s window, never per frame.
