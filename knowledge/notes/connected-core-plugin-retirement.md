---
title: Retire plugin contexts before Core and avoid reverse generic dependencies
game: V Rising
game_build: Steam 23521359
progenitor: a6b69f69
side: both
area: reload
tags: core, hot-reload, allocator, generics, config, connected
route: tracked plugin retirement followed by Bootstrap Core reload; connected client and isolated server
status: verified-live
date: 2026-10-16
evidence: internal scratch core-retire-ui-roots3.txt and core-retire-ui-oldtypes.txt, retire-helper-client1.json through retire-helper-client4.json, retire-helper-server1.json and retire-helper-server2.json, retire-helper-search-after.json
---

## Goal

Complete compatible Core swaps while a client remains connected, including after opening a consumer mod's UI.

## Route

Track plugin contexts before calling Unload, release parked config entries before waiting, and keep Core-independent plugins and their independent runtime providers running. Inspect allocator roots for any remaining consumer that prevents collection.

## What the engine did

- Observed: an unloading context disappeared from AssemblyLoadContext.All while its allocator remained alive. Enumeration alone incorrectly allowed Core retirement to overlap consumer retirement. The original crash was inside the embedded .NET 6.0.7 loader-allocator finalizer; its stack matches the upstream failure class in dotnet/runtime issue 116953, but no runtime replacement was tested.
- Observed: parked ConfigSync entries retained a retired mod through ConfigFile event handlers. RestoreLocal releases these holds before the plugin collection wait; a refused swap refreshes server settings after restoring plugins.
- Observed: opening the consumer's UI left a direct Core LoaderAllocator-to-consumer LoaderAllocator reference. The consumer defined generic UI helpers instantiated with Core interface types. Source-checked: .NET 6.0.7 ClassLoader::ComputeLoaderModule selects a collectible argument's module ahead of the defining module, creating the reverse dependency. Replacing the consumer helpers with direct framework calls and concrete overloads allowed collection.

## Verification

Dirty working tree based on the commit above; installed Core SHA starts 987862b2, Bootstrap 18bab7c9, and corrected consumer 4487fb5c. Four successive connected client Core swaps with the consumer UI open completed, generations 1 through 5, with previousAlive=false. Two connected server swaps completed, generations 3 through 5. Find reopened and returned 25 search matches; the fresh handshake reapplied 40 server-controlled settings. Focused lifecycle/config/API tests passed (194).

These observations qualify the tested compatible generations and consumer, not arbitrary native registrations, all third-party plugins, or replacement of Bootstrap without restarting.

## Gotchas

### Unloading is not collected

- **Symptom:** the plugin enumeration is empty but Core retirement crashes or overlaps live dependents.
- **Cause:** AssemblyLoadContext.All stops listing a context when unloading starts.
- **Fix:** track resurrection-aware weak references before Unload and wait for those references to die before unloading Core; refuse and restore if they remain alive.

### Consumer generic methods can keep the consumer in Core

- **Symptom:** collection works before opening UI but fails afterward, with Core's allocator directly retaining the consumer's allocator.
- **Cause:** a consumer-defined generic method is instantiated with a type from the separately collectible Core assembly.
- **Fix:** use concrete consumer helper signatures or call Core's own generic API directly. Closing the UI cannot remove a runtime allocator dependency already created; restart once after correcting the consumer.

### Parked settings survive plugin cleanup

- **Symptom:** retired mod config callbacks remain rooted after normal plugin unregister.
- **Cause:** session config locks deliberately park entries for ordinary plugin reload, but Core swaps need them released before waiting for the consumer to collect.
- **Fix:** restore local config values and release save holds in the pre-collection Core hook; reacquire server settings if the swap restores the old Core.