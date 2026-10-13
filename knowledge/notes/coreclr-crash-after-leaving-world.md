---
title: A coreclr 0x1d1fdd crash after leaving a world names no culprit; read thread 0's managed stack
game: V Rising
game_build: Steam 23521359
progenitor: e3b5d9aa
side: client
area: interop
tags: crash, coreclr, executionengineexception, leave, disconnect, minidump, windbg, sos, third-party
route: WER minidump (%LOCALAPPDATA%\CrashDumps) opened in WinDbg with SOS; !clrstack -f on thread 0
status: verified-live
date: 2026-10-04
evidence: internal research notes (CoreCLR crash triage 2026-10-04), _tools/tsrepro/crash1 and crash2 (dumps, cdb logs, client logs)
---

## Goal

Find out whether a client crash a few seconds after leaving a world (hosted or dedicated) is Progenitor's, while reproducing a player's Thunderstore mod set (twelve third-party mods, among them a client UI mod) next to Progenitor.

## Route

Read the Windows Application log for the fault, take the WER minidump from `%LOCALAPPDATA%\CrashDumps`, open it in WinDbg (Store build, SOS loads by itself), and read the managed stack of thread 0 (the Unity main thread) with `!clrstack -f`. Then repeat the scenario without the suspected mod. Rejected: `dotnet-dump analyze` (it cannot initialize the DAC on these minidumps, even pointed at the game's own `dotnet` folder) and `.exr -1` alone (it only shows where .NET gave up).

## What the engine did

Observed on the Steam client:

- Every crash had the same Windows fault: `coreclr.dll` 6.0.722.32202, `0xc0000005` at offset `0x1d1fdd`, with ".NET Runtime: internal error". That offset is where CoreCLR turns a native access violation it cannot handle into a fatal `ExecutionEngineException`. Managed `try`/`catch` never sees it, and the logs just stop.
- In every dump the native frames below the managed caller were the same: game code reached through `il2cpp_runtime_invoke` from an Il2CppInterop call. Only the managed frames above that differ.
- The same signature had three different callers. 2026-10-02 (two crashes): Progenitor Core reading a transmog palette through a by-ref wrapper, and disposing a cached `EntityQuery` after its world was gone. Both are fixed on main (`6109d17f`, `22d6e3f5`). 2026-10-04 (two crashes, one after leaving a hosted game and one after leaving the dedicated test server): the third-party client UI mod's per-frame update reading `World.EntityManager`, about 5 s after the main menu came back.

## Verification

**Verified live** 2026-10-04, Steam client with Core 2026.10.58.2, on a hosted private game and on the isolated test server:

- With that client UI mod loaded, the client crashed on the 4th and 5th leave of the session. Both dumps show that mod's update loop on thread 0, and no Progenitor frame is on the faulting stack.
- After removing only that DLL, the same session switched servers six times (hosted, remote, hosted, remote, hosted, remote) with no crash and all seven handshakes ok.
- The other 11 mods stayed loaded throughout.

Not tested: whether the mod crashes without Progenitor installed (likely, since nothing of ours is on its path), and newer versions of the mod.

## Gotchas

### The fault offset is not the bug

- **Symptom:** Three different crashes share `coreclr.dll+0x1d1fdd`, so a new one looks like a regression of an old one.
- **Cause:** The offset is CoreCLR's fatal-error path, not the code that faulted.
- **Fix:** Name the caller from thread 0's managed frames (`!clrstack -f`) before comparing crashes. Compare callers, not offsets.

### dotnet-dump cannot read these dumps

- **Symptom:** `Failed to load data access module, 0x80004002`, even with `setclrpath` pointed at the game's `dotnet` folder.
- **Cause:** The WER minidump of this .NET 6 host does not give dotnet-dump enough to initialize the DAC.
- **Fix:** Use WinDbg (cdb `-z <dump>`); its SOS loads against the dump and `!clrstack -f` works.

### Third-party per-frame UI outlives the world

- **Symptom:** The game dies a few seconds after reaching the main menu, never at the leave itself.
- **Cause:** A mod's `MonoBehaviour.Update` keeps reading the destroyed client world's `EntityManager`.
- **Fix:** For players, remove or update that mod (report it upstream: it should check `World.IsCreated` or drop the world on disconnect). For Progenitor code, use world-bound handles (`Ecs.WorldBoundQuery`) and re-resolve the world every frame.
