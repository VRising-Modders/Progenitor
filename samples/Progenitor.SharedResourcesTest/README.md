# Shared Resources Test

Reloadable, administrator-only test mod for comparing the normal build-menu and workstation paths against a fixed per-ingredient source order. It captures real inventory counts; it never stages stock into a bag, performs a fake craft, or implements its own payment path.

Requires the inventory/container API. The provider build additionally requires `Progenitor.Materials`, `ctx.MaterialSource`, and matching client/server framework hooks. This is a controlled test fixture, not a production clan-sharing policy. Use an isolated populated save and one test operator; keep unrelated inventory automation paused during each capture.

## Build

From the repository root:

```powershell
dotnet build samples/Progenitor.SharedResourcesTest/Progenitor.SharedResourcesTest.csproj
dotnet test tests/Progenitor.SharedResourcesTest.Tests/Progenitor.SharedResourcesTest.Tests.csproj
```

The default build supports surveying plots and inventory evidence only. `enable` refuses because it contains no provider adapter. Build against a framework revision containing the material API with:

```powershell
dotnet build samples/Progenitor.SharedResourcesTest/Progenitor.SharedResourcesTest.csproj -p:SharedResourcesMaterials=true
```

For integration work, `SharedResourcesCoreProject` can select the absolute path to a reviewed Core project. Do not deploy a DLL against an older Core lacking the APIs used to compile it. An enabled provider build checks the framework's actual hook status and requires both Build and Craft; failed enable unregisters immediately.

## Acceptance procedure — not run

1. Load the copied test save. Install this DLL on the server through the normal [plugin deployment workflow](../../docs/hot-reload.md). The client needs the matching framework hooks but not this server-only plugin. Any changed Bootstrap must be installed with a process restart before the hot-load tests.
2. Run `.srtest plots <item-hash>` for every ingredient. Choose a small owned/clan plot with no relevant resources and another authorized plot holding ordinary chests. Run `.srtest containers <territory> <item-hash>` to identify their current entity indices/versions. These are session values; never reuse them after a reload.
3. Stand on the poor plot. Bind remote sources using `.srtest source remoteA <index> <version>` and `remoteB`. Bind `.srtest bag`; for crafting also `.srtest watch station <index> <version>`. Empty the test ingredients from the bag and station, recording and preserving anything moved. Heart inventories and station inventories cannot be sources. Duplicate physical inventories cannot be aliased twice.
4. Set `.srtest scope <poor-territory>`. For each ingredient, set `.srtest rank <item> remoteA 0` and `.srtest rank <item> remoteB 10`. Rank 0 is the best deposit destination; the highest rank drains first. Ties use ordinal alias order. Give a second ingredient the opposite ranks. Configure every ingredient using `.srtest cost <item> <whole-operation-amount> <pool-reserve>`. Use the costs displayed by the game, including active bonuses.
5. With the provider disabled, run `.srtest baseline build` (or `craft`). Capture the displayed unavailable materials and attempt exactly one ordinary operation. Run `.srtest verify`; expect no debit and no output/queue entry. Run `.srtest end`.
6. Run `.srtest enable`, then `status`. Enable must succeed with real Build and Craft hooks wired. Without relocating stock, verify displayed availability, run `.srtest begin build` or `craft`, then perform exactly one normal operation. Run `.srtest verify`; compare every source's reported debit with the reversed per-item order. Independently verify the placed object or craft queue and completed output. A matching inventory report alone is never an end-to-end pass.
7. Repeat with the other operation, opposite ingredient preferences, an exact reserve boundary, and insufficient stock for one ingredient (expect zero debit for the entire recipe). No second player, automation or refinement may mutate the observed inventories during a single-operation comparison. Run separate concurrency tests with framework instrumentation.
8. Check craft cancellation separately: preserve native item identities and confirm refund amounts/destination. Do not interpret a refund as an ordinary spending comparison. Record before/after counts and game observations externally.
9. End the capture and disable. Confirm remote availability disappears and the same poor-plot operation fails again. Hot-unload the mod; check the unload log and framework provider status, then hot-load it and confirm no previous actor, sources, scope or capture remain. World loads and Core/plugin unloads clear the fixture.

`clear` detaches the provider and clears all configuration; `end` retains configuration. Only the administrator who started a fixture can change it. The context tracks commands/events and provider handles; no injected types or unmanaged patches are installed by this mod. Restore any changed test stock/buildings, remove the test DLL when finished, and return the client to its agreed idle state.

## Verification limits

The inventory oracle checks exact final deltas, reserve once per pool, no debit from unselected watched inventories, and no partial debit when any ingredient is short. It cannot observe transient staging between snapshots, prove an output exists, authenticate the displayed client count, or prove simultaneous requests are serialized. Capture the normal game UI and framework payment evidence for those claims.
