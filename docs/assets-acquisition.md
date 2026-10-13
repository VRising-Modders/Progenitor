# Catalog asset acquisition

Client-only first cut of an owned load of one **initially unloaded** presentation prefab from a content archive. Research and the live procedure are in the internal research notes. **Code written, nothing executed. Not verified live. Not in a candidate.** No `ContentNamespace` / `ArchiveFileInterface` / `ContentLoadInterface` call has been executed.

Do not use this to replace [`effects.md`](effects.md) `Assets.Borrow`. Borrow sees **scene** `ParticleSystem`s. This path mounts an archive in Progenitor's own namespace `PGbrowser` and instantiates a clone under `progenitor-effects-<pluginId>`.

## First candidate

`Part_LanternGlow_PaleBlue01` — archive `afdadd4e7d9e05d313c071dd273e505a`, GameObject pathID `8493489897054841949`. Abort if a baseline residency scan already sees that name (a world lantern may have streamed it). Do not force-unload a game-owned copy.

## Opt-in gate

The first native acquisition call is refused unless **both** are set:

1. Dev config `Acquisition.AllowCatalogLoad` (default **false**)
2. Environment `PROGENITOR_CATALOG_ACQUIRE=1`

Either alone is not enough. Ship the candidate with both unset.

## API

```csharp
using Progenitor.Effects;

var handle = AssetAcquisition.Acquire(
    AssetAcquisitionRequest.Lantern(),
    owner: this,
    pluginId: MyPluginInfo.PLUGIN_GUID);
ctx.Track(handle);
var preview = AssetAcquisition.Preview(handle, hostMenu: true);
handle.Dispose(); // UnloadAsync / Unmount only what this handle mounted
```

Dev ops (client, main thread): `catalog_residency`, `catalog_load`, `catalog_status`, `catalog_preview`, `catalog_release`. `catalog_load` returns `refused` / `opt-in-required` when the gate is closed.

Never mount into `"RTC"`. Never `ReleaseObjectAsync` a game id. Never `ContentNamespace.Delete`. Double-dispose is a no-op. A failed acquire unwinds what it already mounted.

Several loader entry points (`GetMountedArchives`, `GetContentFiles`, `WaitForUnloadCompletion`) are icalls **absent from the game's metadata**. The loader probes those fields and does not invoke them when unresolved.

**Audio (narrowed).** The content archives examined via this loader (`catalog-inventory.json` / `catalog-summary.json` on this build) contain no `AudioClip` and no `AudioSource`. That is not a claim that game audio cannot be acquired: FMOD / `StudioEvent` is a different stack.

## Verified and open

**Source-checked.** **Compile-verified** (`dotnet build` of Core/Dev). Offline tests cover the opt-in gate, identity constants and refuse paths. **Verified live:** no. **Not in a candidate.**

Live gate: both opt-in halves granted, then an authorized client run of baseline residency → load → capture → release → dump equal to baseline. The procedure is in the internal research notes.
