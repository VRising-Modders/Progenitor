---
title: A plugin generic instantiated with Core types pins the plugin to that Core generation
game: V Rising
game_build: Steam 23521359
progenitor: 22b55258
side: client
area: reload
tags: hot-reload, generic, loaderallocator, pin, alc, core-swap, refused, found=0, runtime-generic, controls-sample
route: heap analysis of the client after a refused Core swap; game-free repro with two collectible contexts on .NET 6.0.35
status: verified-live
date: 2026-10-16
evidence: internal research notes (plugin-alc-pin-controls-sample-2026-10-16), CrossContextGenericsTests
---

## Goal

Find what kept the removed Controls sample's load context alive, so a client Core swap stops ending in `refused: plugin ALC progenitor-reload:Progenitor.ControlsSample:… still alive` while `pin-diag plugin-holders found=0`.

## Route

Take a full heap snapshot of the client after the refusal, find the sample's `LoaderAllocator` and list its roots. Then reproduce outside the game with a fake Core and a fake plugin in two collectible contexts, one case per instantiation shape. Rejected: the static field walk (`PluginPinHolders`), which can only see managed fields and printed `found=0`.

## What the engine did

- Observed: every root path of the sample's allocator ended `Core LoaderAllocator -> Object[] (its handle table) -> sample LoaderAllocator`. No sample object was left on the heap.
- Observed: the sample defines `private T On<T>(int tab, T handle) where T : IUiHandle` and instantiates it 18 times, all over Core interfaces. `Progenitor.Sample` had the same shape in `SampleFx.BorrowAs<T>`.
- Reproduced on .NET 6.0.35: the runtime creates an instantiation of a generic defined in a collectible assembly in the newest *other* collectible context among its type arguments. Arguments from the defining context, and from contexts that never unload, are skipped. That context then records a permanent reference to the defining context. So `On<IUiButton>` lives in Core, and Core keeps the plugin until that Core generation unloads. JIT-compiling a caller is enough.
- Reproduced: `Pair<MyType, IUiButton>` and `Pair<string, IUiButton>` also pin; `Pair<string, int>`, `One<Dictionary<IUiButton, MyType>>` and Core generics over plugin types (`IUiTabs.Add<T>`) do not.
- Bootstrap waits for retired plugin contexts before it unloads Core, but these can only go after Core unloads, so every swap is refused and the refusal latches until a restart.

## Verification

Steam client, 2026-10-16, Core 2026.10.29.8 (PR #33 branch): swap refused; heap analysis as above, with the sample's allocator held from Core's handle table slot 3729. Repro: 14 shapes on .NET 6.0.35, each checked by whether the plugin context survives 20 forced collections. `CrossContextGenericsTests` checks the same shapes on .NET 8 against the runtime's own handle table. Not tested live: the fixed samples, the admission warning and the new diagnosis line.

## Gotchas

### found=0 but the plugin context is still alive
- **Symptom:** a Core swap is refused for a plugin context that the static walk says nothing holds.
- **Cause:** the holder is the runtime's own cross-reference from Core's allocator, not a managed field.
- **Fix:** look for `pin-diag plugin-holder runtime-generic <member>` in the log, or scan the plugin with `CrossContextGenerics`; make that member non-generic.

### A plugin type next to the Core type does not help
- **Symptom:** a helper over `(MyType, IUiButton)` still pins.
- **Cause:** the plugin's own types are in the defining context, so they are skipped; Core is the only candidate.
- **Fix:** no generic helper over Core types in a plugin. Call Core's generic directly, or take the base interface and cast at the call site.
