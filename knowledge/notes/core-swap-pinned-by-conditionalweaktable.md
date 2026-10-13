---
title: A ConditionalWeakTable keyed on a process-lifetime object pins the Core generation, and gcroot cannot see it
game: V Rising
game_build: Steam 23521359
progenitor: 980797f1
side: client
area: interop
tags: core, swap, refused, pin, previousalive, conditionalweaktable, dependent-handle, gcroot, dump, hot-reload, clientpolicy
route: dotnet-dump of the client after a refused swap; ClrMD reverse reference walk from the old LoaderAllocator
status: verified-live
date: 2026-10-05
evidence: internal research notes (Core hot reload, pin class 2026-10-05), _tools/tsrepro/client-LogOutput-pin-*.log, _tools/revroot
---

## Goal

Find what kept Core generation 1 alive on the Steam client, so a client Core swap after joining and leaving a server stops ending in `refused: generation 1 still alive; restored; restart the process` (`previousAlive=True`). The isolated test server's swaps always succeeded.

## Route

Reproduce the refusal (`core_reload` at the title after a join), take a full dump (`_tools/dotnet-dump8`, `collect --type Full`), map the unloading `CoreLoadContext` to its `LoaderAllocator` (`name2ee Progenitor.dll!Progenitor.Plugin` then `dumpmt` prints both), and `gcroot` it. Run forced GCs and take a second dump to check whether the allocator really survives. Then walk the reverse references of the whole heap with ClrMD (`_tools/revroot`) to find the object nothing on the heap references. Rejected: the reflective holder scan and pin-diag lines (they printed `holders=0` and `native-pin-gen count=0`).

## What the engine did

- `gcroot` on the old allocator reported **0 roots**, and only the runtime's own strong handle held the unloading ALC, yet the allocator survived four `GC.Collect` + `WaitForPendingFinalizers` rounds. In the second dump it had moved (compacted) and was still live. So the root set SOS uses is incomplete.
- The reverse walk found one source: a `List<Progenitor.Networking.ClientModObservation>`, the secondary of a **dependent handle** whose primary was a `RuntimeAssembly` (a BepInEx patcher assembly). That is a `ConditionalWeakTable` entry: `ClientModReportClient.PerAssembly`, a `ConditionalWeakTable<Assembly, List<ClientModObservation>>` used to cache plugin rows.
- A table entry keeps its value alive while the key lives. A patcher assembly lives for the whole process. The value's type, an instantiation over a Core type, belongs to Core's allocator, so that one entry rooted the generation. The table itself sits in a Core static. It could never be finalized, because the entry kept Core alive.
- The cache is filled whenever the client mod report is built: the JoinFlow screens' `ClientModsProbe`, the ServerModPause prediction, a policy server's report request, or the `join_admin report` bridge op. So it pinned after a join, not at startup. A plain join with nothing to show does not fill it, which is why some joins left the swap working.

## Verification

Steam client, isolated test server, 2026-10-05:
- Before (Core 2026.10.76.4): after joins, `core_reload` was refused and `previousAlive` stayed true after forced GCs. Two dumps gave the chain above.
- After (Core 2026.10.78.1, commit `980797f1`, image hash checked against the build): restart, join, `join_admin report` (cache held 20 rows, including 1 patcher row), leave, `core_reload` gave `ok gen=2 collected=True`. A second join, report, leave and swap gave `ok gen=3 collected=True`.
- Not tested: a server whose policy requests the report.

## Gotchas

### gcroot says no roots, but the generation is pinned
- **Symptom:** `gcroot <LoaderAllocator>` finds 0 roots; the swap is still refused and the allocator survives forced GCs.
- **Cause:** SOS `gcroot` does not follow dependent handles (`ConditionalWeakTable` entries), so their secondaries look unrooted.
- **Fix:** read `gchandles -type Dependent` and check each Data object's type, or run `_tools/revroot` against the allocator and look up the sources it prints.

### ConditionalWeakTable is not "weak" for its values
- **Symptom:** a cache "that cannot leak" pins a hot-reloaded generation.
- **Cause:** the value lives as long as the key. A key that outlives the generation (an `Assembly`, a Bootstrap or game object) keeps a Core-typed value and the whole generation alive.
- **Fix:** key a Core-owned `Dictionary` by a string or id (here MVID + full name) and clear it on uninstall. `ConditionalWeakTablePinTests` allows the type only in reviewed files whose keys are Core objects.
