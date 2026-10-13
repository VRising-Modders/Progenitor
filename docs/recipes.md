# Recipes — `ctx.Content.DefineRecipe`

> **Offline-tested only, not live-verified.** Roadmap **M3.7.1**. The builder, metadata-offset
> buffer writes, station-row ledger, apply rollback and Sample recipe compile and have
> game-free tests. No recipe apply has been observed in a running game (spikes S1–S11 are
> still owed); only the vanilla sample names and hashes were read from the test server. The research behind every game-component claim is in the internal research notes.

A mod that can define an item but not a way to make one is a mod that hands the item out with a chat
command. Recipes are the first content manager because "a sword you can craft" is what a V Rising content
mod is.

## Author contract

```csharp
ctx.Content.DefineRecipe("sample.bench.crystal-thread")
   .CloneOf("Recipe_Ingredient_CoarseThread")   // source must have RecipeData
   .Output("Item_Ingredient_Thread_Coarse", 1)        // vanilla item, or a Progenitor id
   .Input("Item_Ingredient_Crystal", 1)              // repeatable, 1–8
   .At(RecipeStationKind.Workstation, "TM_CraftingStation_SimpleCraftingBench")
   .CraftTime(8f)
   .HudOrder(1000)
   .AlwaysUnlocked()                                 // default on; writes RecipeData.AlwaysUnlocked
   .Register();                                      // IDisposable, owner-scoped
```

Worked example: [`samples/Progenitor.Sample/SampleRecipe.cs`](../samples/Progenitor.Sample/SampleRecipe.cs),
registered from `Plugin.OnLoad` as `SampleRecipe.Register(ctx)`. That call is the **vanilla-only**
recipe. A custom-output variant (`sample.ember.craft` → `sample.ember`) lives in
`SampleRecipe.RegisterCustomEmber` and is **not** registered from `OnLoad`; it depends on paired
item agreement.

Same shape as `DefineItem` / `DefineBuff`: a fluent builder, a deterministic GUID from the id
(`PrefabGUID.CalculateGuidHash` on the live path), `Register` returning a handle whose disposal
unregisters. Limits, enforced at `Register` with a readable message:

| Rule | Effect |
|---|---|
| `CloneOf` required | Source must resolve to a prefab with `RecipeData` |
| Exactly one `Output` | Amount must be positive |
| 1–8 `Input` rows | Amount must be positive |
| 1–4 `At` stations | `RecipeStationKind.Workstation` only (refinement / character are phase 2) |
| `CraftTime` finite and positive | Written onto `RecipeData.CraftDuration` |
| Id must not hash to the source GUID | Apply also refuses an existing vanilla/foreign GUID |
| `Named` / `Described` | Throw: there is no `ManagedRecipeData`; put the display name on the output item |

**Conflict with another owner.** The station-row ledger is keyed by `(station, recipe, owner)`.
The first owner to find the GUID absent appends it; a later owner records a claim and writes
nothing. Unapply removes the row only when this owner appended it **and** no other owner still
claims it. Vanilla GUIDs are never written as the clone's identity. A vanilla row this owner
only claimed is left in place.

**What unapply restores.** Live then prefab `WorkstationRecipesBuffer` rows this owner appended;
`RecipeHashLookupMap` / `_OriginalRecipeDurations` entries; prefab-collection maps this process
inserted; then **destroy the clone exactly once**. After a full owned unapply the station buffer
is byte-identical to the captured baseline (game-free test). A queued craft that still holds the
GUID is spike S9 / S11 — this increment destroys, which the design's "soft keep" may need to walk
back after those spikes.

**Failed apply.** Resolve source, output, every input and every station **before** cloning.
A missing station or item writes nothing. After the first write, any failure restores the
captured world (clone destroyed if this call created it, maps / hash-map / station rows
reversed, ledger claims dropped). A definition that validates at `Register` can still fail at
apply; that is not a partial clone sitting on a station.

## Primary example (vanilla station, vanilla items)

| Role | Spawnable name | GUID hash | Provenance |
|---|---|---|---|
| Station | `TM_CraftingStation_SimpleCraftingBench` | `-1107784271` (observed) | Resolved at apply by `PrefabLookupMap`. Buffer type `WorkstationRecipesBuffer` (`RecipeGuid` @0). The game's metadata has no spawnable-name → hash table. |
| Source recipe | `Recipe_Ingredient_CoarseThread` | `217985609` (observed) | Resolved at apply. Vanilla: `CraftDuration` 20, two requirement rows, one output row (`-1562867444` ×1). `RecipeData` layout from the game's metadata. Clone identity is `PrefabGUID.CalculateGuidHash("sample.bench.crystal-thread")`, never the source GUID. |
| Input | `Item_Ingredient_Crystal` | **`-257494203`** | `PrefabGUID.CalculateGuidHash` of that name. Source-pinned in `Plugin.EmberSource` and `SaveSafetyPolicy.KnownVanillaGuids`. Requirement row `RecipeRequirementBuffer` (Guid @0, Amount @4, stride 8). |
| Output | `Item_Ingredient_Thread_Coarse` | `-1562867444` (observed) | Resolved at apply. Output row `RecipeOutputBuffer` (Guid @0, Amount @4, stride 8). |

Sample constants: `SampleRecipe.Id = "sample.bench.crystal-thread"`, input amount 1, output amount 1,
`CraftDuration` 8, `HudOrder` 1000, `AlwaysUnlocked`.

Custom-output variant (not the primary path): `SampleRecipe.CustomId = "sample.ember.craft"` outputs
`sample.ember`. Requires the item-agreement lane. Qualify separately.

## How apply works (server, and a client that registered the same definition)

When the world and prefab collection are up (WorldReady + frame retry until the source resolves):

1. Resolve source, output, inputs, stations. Refuse a missing token, a source without `RecipeData`,
   a clone GUID equal to the source GUID, or an existing GUID this process did not clone.
2. Capacity-check `RecipeHashLookupMap` before cloning a new entity.
3. Clone (`Instantiate`, re-add `Prefab`, write the new `PrefabGUID`) or update an owned clone.
4. Patch `RecipeData` field by field in place: `GetComponentDataRawRW`, then write each field at its
   `Layout.RecipeData*Offset`. **Not** `SetComponentData<RecipeData>` (no IL2CPP instantiation;
   the game's metadata has `Get` only), and **not** the typed `EntityExtensions.Write<RecipeData>`:
   on the test server (2026-09-24) native `SetComponentDataRaw` refused its 24-byte write with
   "SetComponentData can not be called with a zero sized component … must have same size as sizeof(T)".
5. Grow `RecipeOutputBuffer` / `RecipeRequirementBuffer` with the `Clear`+`Add` instantiations present in the game's metadata,
   then write Guid/Amount at `Layout` offsets through `RecipeBufferLayout` / `GetBufferRawRW`.
   Read back those offsets; mismatch fails and rolls back. `Clear` `RecipeOutputUnitBuffer` and
   `RecipeLinkBuffer` when present; leftover length after Clear is a failure.
6. Prefab collection maps, same writes as the item path.
7. `GameDataSystem.RecipeHashLookupMap.TryAdd` and `_OriginalRecipeDurations.TryAdd`.
   Map shape matches live-proven `ItemHashLookupMap`; the recipe value type is **inferred** until S2.
8. Per station: read `WorkstationRecipesBuffer` through `GetBufferLength` / `GetBufferRawRO` and
   `Layout.WorkstationRecipesBufferStride`. Append at the tail only when the GUID is absent, and
   only when the ledger says this owner is the writer. Then the same append on live instances.

`RecipeApply.Uninstall()` reverses remaining registrations (TryUnapply, then drop hooks). It is
**not** yet called from `PrefabRegistry.Uninstall` — coordinator wiring item below.

## When the mod is gone

A player's unlocks are saved with the player, so a recipe GUID in `UnlockedProgressionElement`
(added, for example, by the Dev bridge [`unlock`](bridge-ops-player.md#unlock) op) outlives the mod that
registered it. Once the recipe clone is gone, the client looks the GUID up about twice a second
and logs `PrefabLookupMap.TryGet - Prefab with PrefabGUID … does not exist as a registered prefab`
from `ClientProgressionUpdateSystem` and `AlertsUISystem.GetAvailableSpellPoints` (live,
2026-10-06: about 15,000 lines after the Sample was removed with `sample.bench.crystal-thread`
unlocked). Core's `UnlockPrune` removes such rows on the server:

| When | Who | Which GUIDs |
|---|---|---|
| A recipe unregisters and the unload reason is `Removal`, or its handle is disposed outside an unload | every user, offline players included, on the next frame | that recipe's GUID |
| 30 s after the save loads | every user, offline players included | every ledger GUID of a mod that is gone |
| A player connects | that player | every ledger GUID of a mod that is gone |

A `Swap`, `Suspend`, `Shutdown` or `Unknown` unload keeps the rows because the plugin is coming
back (the same rule as [buffs](buffs.md)). An `OnLoad` that throws undoes its registrations as
`Unknown`, so a failed reload keeps them too. A GUID is removed only when no loaded plugin
registers it and it does not resolve to a prefab this process did not clone, so a vanilla or
another mod's prefab under the same hash is never touched. The two ledger sweeps also keep a GUID
whose mod this process knows in any state but unloaded. That covers a mod that is loading, failed
to load, is switched off or is dormant on the server. A mod counts as gone only when it is absent
from the process or unloaded.

The ledger is `BepInEx/config/progenitor-content-guids.json` on the server. It lists every GUID
a Progenitor recipe registered there, with the recipe id and the registering plugin's GUID. Rows
are kept after the mod leaves, because an offline player can still hold the GUID. An invalid
ledger is moved to `progenitor-content-guids.json.<UTC time>.bad` and a new one is started. A
ledger that cannot be read at the moment is never overwritten.

**The client's copy.** The server sends a player the progression list when they join, before
`PlayerConnected` is published, and the prune sends no removal. A connected client therefore
keeps a pruned GUID, and its log lines, until it rejoins. The Dev op's `lock` behaved the same
way live on 2026-10-06. The world-load sweep runs before most joins, so the first join after a
restart normally gets the clean list. A player who joins within 30 s of the load gets it on
their next join.

The prune removes the rows from `UnlockedProgressionElement`, `UnlockedRecipeElement` and
`UnlockedBlueprintElement` by copying the kept rows and writing each changed buffer once with
`UnsafeEntityManagerUtility.SetBufferDataRaw`. Strides and offsets come from the layout file
(`UnlockedProgressionElementStride` 4, `UnlockedRecipeElementStride` and
`UnlockedBlueprintElementStride` 8) and are checked against the interop before the first write.
It does not raise the game's rebuild event (that needs a live character): the rebuild would not
re-add a GUID whose prefab is gone, so it would produce the same buffers. A buffer whose every row
is stale is left alone with a warning, because a zero-row raw write has not been verified. Each
change is logged in this format (from the unit test; not a captured game log):

```text
UnlockPrune: removed 1 stale Progenitor GUID(s) from 'TestAdmin' (progression -1276252187 [recipe sample.bench.crystal-thread, mod progenitor.sample]; recipes -1276252187) after unregister reason=Removal
```

Progenitor registers no blueprint prefabs today. The ledger has a `kind` column for when it does.
Item and buff clones are not pruned: their entities stay for the life of the process after an
unregister (soft unapply), and the save loader drops unknown item GUIDs on load (the reason
`PrefabRegistry` applies clones before `DeserializeLoadFile`).

Status: source-checked against the server metadata, compiled and unit-tested
(`UnlockPrunePlanTests`). **Not run live.** The progression-buffer raw write is the one the
`unlock` op's `lock` ran live on 2026-10-06. The direct derived-buffer write has not been verified live.

## Verified versus inferred

| Type / call | Metadata | Status |
|---|---|---|
| `RecipeData` fields | field offsets | **metadata**; offsets declared in `layout.txt` (`RecipeData*Offset`). No whole-struct length is written. |
| `Write<RecipeData>` | `SetComponentData<RecipeData>` **absent** (Get only) | **refused live** by `SetComponentDataRaw`'s size check (S3, 2026-09-24); replaced by the in-place field patch |
| `RecipeRequirementBuffer` | Guid@0, Amount@4; stride 8 | **metadata**; `RecipeBufferLayout` writes those offsets; `layoutgen --check` agrees |
| `RecipeOutputBuffer` | stride 8 | **metadata**; same |
| `RecipeOutputUnitBuffer` / `RecipeLinkBuffer` | both types | **metadata**; cleared on clone; leftover rows walked at the metadata stride |
| `WorkstationRecipesBuffer` | RecipeGuid@0; stride 4; **not networked** | **metadata** |
| `GetBuffer<T>` / `Add` / `Clear` for those buffers | generic instantiations | **metadata instantiations**; **not live** (S3). Grow uses them; field bytes use metadata offsets. |
| `GetBufferLength` / `GetBufferRawRO` / `GetBufferRawRW` | existing Core helpers | **verified live** (items/castles) for RO; RW used by item NetSnapshot rewrite |
| `RecipeHashLookupMap.TryAdd/Remove/Capacity` | all three methods | **metadata**; same map shape as live `ItemHashLookupMap`; value type **inferred** (S2) |
| `_OriginalRecipeDurations` | field | **metadata**; interop field; **inferred** until S5 |
| `em.Instantiate` + Prefab tag + `SetComponentData<PrefabGUID>` | item applicator | **verified live** for items |
| Live instance query `IncludeDisabledEntities` | trap 4 | **verified** pattern; not run on stations |
| Station visibility = buffer append | `WorkstationRecipesBuffer`, no `Snapshot_WorkstationRecipesBuffer` | **metadata** shows that the list is local; **inferred** that append is sufficient to show the row (S6) |
| `RecipeData.AlwaysUnlocked = true` | field @0x14 | **metadata** field write; **inferred** that this is enough for the UI (S6) |
| `ProgressionUtility.AlwaysUnlocked` | method | **inferred**. Native body unread. A Progenitor recipe is in none of the progression maps; expected fall-through is the bool. Live observation (row grayed or not) settles it. |
| Cost consumption at craft time | `InventoryUtilities.CanCraftRecipe` | **inferred**. Apply writes the requirement/output buffers; the game consuming those amounts is S5/S7. |
| `Item_Ingredient_Crystal` = `-257494203` | not in the game's metadata | **source-pinned** (`Plugin.EmberSource`, `SaveSafetyPolicy.KnownVanillaGuids`). The metadata has no spawnable-name hash table. |

## Gaps the coordinator still owns

These files were out of this lane. Exact hooks:

1. **`PrefabKind.Recipe = 2`** in `Content/PrefabKind.cs`. Today `PrefabWire.TryValidateKind(2)` refuses. Recipes do not go through `PrefabRegistry.Add`, so this is only needed if recipes join the item/buff bookkeeping (`RememberAppliedIdentity`, `PrfU`).
2. **`PrefabApplyGate.ClientPacket.Recipe`** and treating `PrfR` like `PrfD` for per-join bookkeeping (S6b: a recipe-only mod sends no `PrfD`/`PrfK`).
3. **`PrfR` packet** (`0x50726652`, 200 bytes) in a new file; send after that join's `PrfD` batch; receive validation as in the research note §6.3. `PrfD` cannot carry inputs/stations.
4. **Bootstrap `ReceiveSlotPolicy.CoreProtocolIds`** must include `0x50726652` (restart-owed).
5. **`PrefabRegistry` on-connect send** of `PrfR` after `PrfI`/`PrfD`, and a `PrfU` branch that runs the client-side row ledger.
6. **`RecipeApply.Uninstall()` from `PrefabRegistry.Uninstall`.** Source-checked as present (after the buff-bar uninstall). `RecipeApply.Uninstall` TryUnapplies remaining registrations, then drops hooks. Live Core-swap cleanup of a applied recipe is still S9.

7. Live spikes **S1–S3, S5, S6** before calling this supported. Buffer `GetBuffer<T>` / `RecipeHashLookupMap.TryAdd` / `RecipeData` chunk size come from the game's metadata, not observation.

No new public types this increment (`RecipeBufferLayout`, `RecipeApplySession`, `RecipeApplyWorld` are internal). Existing public: `RecipeDefinition`, `RecipeDefinitionBuilder`, `RecipeStationKind`, `RecipeItemRef`, `RecipeStationRef`, `RecipeApplyPlan`.

## Verified and open

**Source-checked.** **Compile-verified** (`dotnet build` of the solution; `dotnet test` filter
`FullyQualifiedName~Recipe|FullyQualifiedName~LayoutConstant`). Independently reviewed offline
(internal review R87; layout constants are used as strides/offsets of reads or writes).
**Verified live:** not this job. **Not in a candidate.** This page is offline-tested only.
Live gates: spikes S1–S3, S5, S6 on the isolated pair. Recipe visibility remains partly
inferred (`ProgressionUtility.AlwaysUnlocked` native body unread).

### Live spikes still owed

Isolated pair only (`<testserver>`, ports 9976/9977). Do not touch `C:\VRisingServer`.
Client must be available. Sample loaded on both sides. Custom-item paired acceptance is **not**
required for the primary vanilla example.

| Spike | Pass criterion |
|---|---|
| **S1** Dump a vanilla `Recipe_` entity | Field values match the metadata offsets; a 2-input recipe reads 2 requirement rows |
| **S2** `RecipeHashLookupMap` get / TryAdd / Remove | No crash; dummy GUID visible between add and remove; count returns to start. Fail ⇒ raw Collections 2.x fallback |
| **S3** `sizeof(RecipeData)` vs chunk; `GetBuffer` add/remove on a workstation prefab | No crash; read lengths match S1; add/remove round-trips. Records real `RecipeData` size for `RawWriteGuard` |
| **S4** Which buffer each station kind has | Workbench / furnace / player each report exactly one of the two recipe buffers |
| **S5** Server-only clone, `CanCraftRecipe`, save/restart | `CanCraftRecipe` true against an inventory holding the declared inputs; no new `GameDataSystem` job 5/6 errors; recipe survives `world_save` + restart |
| **S6** Open the simple crafting bench | Row shows Coarse Thread icon and name, cost 1 Crystal, duration 8 s, **not grayed out**. Settles `ProgressionUtility.AlwaysUnlocked` by observation |
| **S6b** Recipe-only join (no `PrfD`/`PrfK`) | Fresh client sees the vanilla-output row. Requires `PrfR` (coordinator items 2–5) |
| **S7** Craft it | Crystal decreased by 1, one Coarse Thread added. Inventory slot round-trips the *item* GUID |
| **S8** `StartCraftItemEvent` at a station whose buffer lacks the GUID | Either refused or crafted; either answer is a result |
| **S9** Unapply, including two owners | Station buffer equals pre-mod snapshot; vanilla recipes still craft; two owners, one unload, the other row remains |
| **S10** Refinement | Out of this increment |
| **S11** Save without the mod, queued craft | Cross-check `save-safety.md`; do not assume `QueuedWorkstationCraftAction.RecipeGuid` is data-only |

Prepared vanilla-path checks for that sitting:

1. Stamp loaded images on each side from that side's own bridge.
2. Bridge `prefab` / `query` for guid `PrefabGUID.CalculateGuidHash("sample.bench.crystal-thread")`. Server: the clone exists, `RecipeData.CraftDuration` is 8, `RecipeRequirementBuffer` is one row (`Item_Ingredient_Crystal` / `-257494203`, amount 1), `RecipeOutputBuffer` is one row (`Item_Ingredient_Thread_Coarse`, amount 1).
3. Query `TM_CraftingStation_SimpleCraftingBench` (prefab and any live instance, `IncludeDisabledEntities`). `WorkstationRecipesBuffer` lists the recipe GUID. Vanilla recipe GUIDs on that buffer still match a pre-mod snapshot.
4. Open the simple crafting bench. Capture: the row shows the Coarse Thread icon and name, cost 1 Crystal, duration 8 s, not grayed out.
5. Put 1 Crystal in the crafter's inventory. Craft. Inventory readback: Crystal decreased by 1, one Coarse Thread added.
6. Unload / dispose the recipe registration. Station buffer equals the pre-mod snapshot; vanilla recipes still craft.
