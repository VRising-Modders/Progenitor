# Pool-reuse identity case

Same-instance crystal pool reuse on `wendigo-frost-nova-appearance`. After tinted playback and rest, `.froststyle off` disables the appearance override while frost dash still spawns rings; `poolReuse` then requires a nonempty untinted reuse set **R**.

Case file: `tools/live-rig/cases-catalog/pool-reuse-identity.json`. Census snippet: `tools/live-rig/cases-catalog/pool-census.Snippet.cs`.

**Verified live** 2026-09-07 (`reviews/2026-09-07-catalog-cases-10`, run `20260907T215010Z-pool-reuse-identity`): **T=10**, **R=T** on attempt 1, `capture.available=false` (that run had no capture step). That run proves the same renderer/GameObject ids were present as rest rows and active again untinted after `.froststyle off`. Identity is Unity instance id plus renderer id observed present at rest between the two activations; it is not a general proof that Unity cannot recycle ids after Destroy. Visual reuse capture is an acceptance procedure on the current case (see [Capture](#capture)); it has not been live-verified.

Offline Node tests cover the rest/reuse assertions, including that a T id missing from the rest census fails (`tinted object missing at rest`).

## Timing

Ring life is about **1.5 s**. The census DLL must not be rebuilt on the dash-to-census path.

1. `snippetPrepare` copies the case `Snippet.cs`, builds `tools/scratch/Progenitor.Scratch.csproj` with `--no-incremental`, requires DLL mtime later than copy time, and records `dllHash` / `buildElapsedMs` **before** any dash.
2. Each later `snippet` step for that same file only copies the already-built DLL into the client reload folder, waits for `Reload: image Progenitor.Scratch.dll sha256=` matching the prepared hash, then the `scratch: result` line. It refuses a rebuild. It records `elapsedMs` from step start to the result line. Source bytes and DLL hash must still equal the prepared values.
3. Tinted and reuse censuses run **1500 ms** after `.frostdash dash`, then the load-only snippet. PrintWindow takes about **1 s** and must not be awaited on that wait: each shot is a fire-and-forget child started at its offset by a timer. The step records `censusDelayMs` (dash to census start) and fails `census late: <ms> ms` when that delay exceeds `maxCensusDelayMs` (default **2000**).

CAT76 (2026-09-07) rebuilt the DLL on the tinted step: dash `21:28:44.174Z`, census `21:28:49.932` (**5758 ms**), `zero participants` because the rings had already died. The `--no-incremental` rebuild was ~3.2 s (`copiedAt` `21:28:45.677Z` → `dllMtime` `21:28:48.872Z`). The Scratch **reload** itself was tens of milliseconds (client `Reload: loading` `21:28:49.885` → first POOLID `21:28:49.932`, **47 ms**) — well under 1 s. A later live run awaited three PrintWindow shots sequentially (~850–1030 ms each) before the +1500 ms wait, so the census started at **3.7–4.2 s** and **R** was empty while the +300/+700 frames still showed crystals. Keep `waitMs` **1500**. Expected live dash-to-census: **~1500 ms wait**, then load `elapsedMs` (expect load well under 1 s). Record `censusDelayMs`, snippet `elapsedMs`, and each frame's `startedMs`/`endedMs` (content time is between them).

If a later live path shows Scratch reload `elapsedMs` greater than about **1 s**, lower `waitMs` so `waitMs + elapsedMs` still lands inside the ~1.5 s ring life, and record that delay. Do not put a rebuild or a blocking screenshot on that path.

`poolReuse` uses the same prepared DLL (load only) on every retry. After the reuse dash it **starts** PrintWindow children at **+100, +500, +900 ms** (a ~1 s shot then holds content about **+300 to +1900 ms**), runs the load-only census at **+1500 ms**, then awaits the children (bounded). In-flight spawn count is capped. A retry still waits for the previous rings to rest (`retryWaitMs` 20000) before dashing again. `capturePrepare` runs once at the start of the case (discarded warmup). The tinted comparison frame **starts** at **+100 ms** with `waitMs` 1500 so that census also stays on the 1500 ms clock.

## What must pass

Pass is identity and `_BaseColor` correspondence, not a census-string match and not whole-scene renderer-count equality.

| Phase | Assertion |
|---|---|
| Prepare | `snippetPrepare` of `pool-census.Snippet.cs`. Non-zero build, missing DLL, missing mtime, or DLL older than copy time fails. Records `dllHash` and `buildElapsedMs`. Then `capturePrepare`: one discarded PrintWindow warmup (`warmupMs`); the file is not evidence. |
| Baseline | Load-only census recorded (`record: poolBaseline`). Malformed `POOLID` lines fail. |
| Tinted | `.frostdash on`, `.frostdash dash`, one comparison `capture` that **starts** at +100 ms (`waitMs` 1500, `maxCensusDelayMs` 2000, `record: captureTinted`), then load-only census. The shot is fire-and-forget; the 1500 ms wait does not include PrintWindow duration. **T** is the nonempty set of `rendererId/goId/slot` that are **active** and carry Sample `_BaseColor` `0.12,0.65,0.25,1`, whether or not they were in the baseline (fresh allocations are legitimate participants). Zero participants fails. How many of T were already in the baseline is informational (`tintedInBaseline` / `tintedFresh`). |
| Rest | Wait 20 s, load-only census. Every id in T is **present** in the rest census, inactive, and has no property block, **or** equals that id's own baseline state if it had one. A T id missing from the rest census fails with `tinted object missing at rest: <ids>` (a destroyed and recycled instance id cannot pass as restored). Residual tint fails. |
| Appearance off | `.froststyle off` with `expectLog` `FROSTSTYLE enabled=False`. Then a client log wait for `FROSTSTYLE client enabled=False`. No `FROSTSTYLE` line within the bounded wait fails that step with `no handler line for .froststyle off` (the missing handler is named). The run must not pass silently. |
| Untinted reuse | `{ "kind": "poolReuse", ... }`. Retries `.frostdash dash` + fire-and-forget burst starts at +100/+500/+900 ms + 1500 ms load-only census up to 3 times (each retry a new dash after the previous rings rest, `retryWaitMs` 20000; in-flight capture children are capped). Census first, then await shots. `censusDelayMs` above 2000 fails `census late: <ms> ms`. **R** is the nonempty subset of T that are **present rows**, active again, and carry no override (no property block, or the per-id baseline color). Empty R after retries records `reuse not observed` and fails. Malformed line, timeout, a load without a matching prepare, or a capture failure with `requireFrames` true (the reuse default) cannot pass. A shot still running at the collect bound is recorded timed out and fails the step only when `requireFrames` is true. Readback success (nonempty R) is separate from `visual.verified`. |
| Appearance on | `.froststyle on` with `expectLog` `FROSTSTYLE enabled=True` (Sample logs `FROSTSTYLE enabled=True restored=0`). |
| Final rest | Wait 20 s, load-only census. Every id in T is **present**, inactive, and has no override (or equals that id's baseline). A missing T row fails. Residual tint fails. |
| Cleanup | Teardown `.frostdash off` (Sample `FROSTDASH cleanup alive=`). |

The historical whole-scene **122** rendererId/goId comparison is attached as `informational` output only. It is never the pass condition. Report `qualifiedTinted` (|T|), `tintedInBaseline`, `reusedUntinted` (|R|), the ids (`participants` / `reused`), `retriesUsed`, snippet `elapsedMs`, `capture.available` (true with `paths` only when frames were saved), and `visual: { frames, verified: false, verifiedBy, verifiedAt }`. A frame is inspectable evidence only after a person or the reviewing agent has viewed it. The rig does not analyze pixels.

Zero participants, residual tint at rest, a T id missing at rest, malformed census line, empty R after retries, missing FROSTSTYLE handler, or timeout cannot pass. A timed `snippet` with no matching `snippetPrepare` fails `snippet not prepared`.

## Census lines

The snippet logs one machine-readable line per crystal-shader material slot, then a summary:

```
POOLID rendererId=<int> goId=<int> active=True|False slot=<int> hasBlock=True|False baseColor=<r,g,b,a|none> go=<name>
POOLID summary renderers=<n> active=<n> withBlock=<n> green=<n> failures=<n> truncated=True|False cap=256 radius=60 origin=<x,y,z|none> scanned=<n> emitted=<n> skippedRadius=<n> skippedCap=<n> ids=<sorted renderer ids>
```

Candidates are the recipe crystal-ring family: `Renderer` material slots whose shader name is `Shader Graphs/Shader_VFX_Ice_Crystal_Unlit_Final`. Bounds: count cap 256; radius 60 from `Camera.main` (if the camera is missing, radius is not applied and `origin=none`). Truncation is `skippedCap > 0`.

## Sample commands used

From `SampleFrostDash` (admin-only): `.frostdash on | off | status | dash`.

- **on** — enable dash. `SpawnRing` always calls `SampleFrostNovaAppearance.Track`; appearance is not separately armed.
- **dash** — request the equipped Veil travel ability (request only).
- **off** — disable dash and destroy owned rings (`FROSTDASH cleanup alive=`).

From `SampleFrostNovaAppearance` (admin-only): `.froststyle on | off | status`.

- **off** — disable the appearance override and clear every renderer carrying the Sample property block. Server logs exactly `FROSTSTYLE enabled=False restored=<n>`. Client logs `FROSTSTYLE client enabled=False`. Frost dash keeps spawning rings, with authored default color and radius.
- **on** — re-enable. Server logs `FROSTSTYLE enabled=True restored=0`. Client logs `FROSTSTYLE client enabled=True`.

Override fields written on owned particles (source-checked): `_BaseColor`, `_EmissiveColor`, `_GroundTint`, `_Fresnel_Color`. This case asserts `_BaseColor`.

`poolReuse` step used after appearance-off (load-only census; the DLL was prepared at the start of the case):

```
{ "kind": "poolReuse", "dash": ".frostdash dash", "waitMs": 1500, "maxCensusDelayMs": 2000, "retries": 3, "retryWaitMs": 20000, "baselineRecord": "poolBaseline", "tintedRecord": "poolTinted", "overrideColor": "0.12,0.65,0.25,1", "capture": { "delayMs": 100, "burst": { "count": 3, "intervalMs": 400 }, "timeoutMs": 10000, "requireFrames": true, "maxInFlight": 4, "maxWidth": 1920, "window": "client", "region": "window", "method": "PrintWindow" }, "snippet": { "side": "client", "file": "tools/live-rig/cases-catalog/pool-census.Snippet.cs" } }
```

## Capture

Focus-free `observe.ps1 shot` through Windows PowerShell 5.1. `-Window client -Region window -Method PrintWindow -MaxWidth 1920`. No keyboard, mouse, or focus. Not a workaround for focus. Each shot is bounded (default 10 s). PNGs land under the run directory `captures/` with an ISO timestamp in the filename.

`capturePrepare` runs one warmup shot before any timed window, records `warmupMs`, and discards the file. The tinted dash **starts** one comparison frame at +100 ms (`waitMs` 1500, `record: captureTinted`). `poolReuse` **starts** three children at +100, +500, +900 ms after the reuse dash, censuses at +1500 ms, then awaits those children. Each result records `startedMs`/`endedMs` (PrintWindow content time is between them) and `censusDelayMs`.

`capture.available` is `true` with `paths` only when frames were saved. `visual.verified` is `false` until a reviewer fills `verifiedBy` / `verifiedAt` after viewing. A nonempty PNG is not visual verification. The rig does not analyze pixels. Readback success (nonempty **R**) is separate from visual success. Non-zero observe exit, missing file, zero-byte file, or timeout fails the step when `requireFrames` is true (default for the reuse burst). A dash-to-census delay above `maxCensusDelayMs` (default 2000) fails `census late: <ms> ms`.

## What a live job must run and observe

From the repository root, isolated test pair only, client available:

```
node tools/live-rig/rig.mjs run tools/live-rig/cases-catalog/pool-reuse-identity.json --out <research-run-dir> --attach-readback
```

Observe on the client log (`filter` `POOLID` / `FROSTSTYLE`) and server log (`filter` `FROSTSTYLE` / `FROSTDASH`) and on `report.json`:

1. `prepare pool census` built once (`--no-incremental`, `dllMtime > copiedAt`). `buildElapsedMs` is the compile, not the census. No Scratch reload on this step. `capturePrepare` warmup discarded (`warmupMs` recorded).
2. Baseline load-only census recorded; `elapsedMs` is step start to result line (no `dotnet build`). No `POOLID summary error=`; malformed lines fail the step.
3. After `.frostdash on`, `.frostdash dash`: tinted-window PNG **starts** at +100 ms (fire-and-forget). Census wait remains 1500 ms (`censusDelayMs` ~1500, must be ≤ 2000). Then load-only census. `elapsedMs` well under 1 s (CAT76 Scratch reload was 47 ms). Nonempty **T** of active slots at `_BaseColor=0.12,0.65,0.25,1`. Fresh ids are valid; `tintedInBaseline` is how many of T were already in the baseline (CAT76 would have been 0 of 5 new `Impact_CrystalCenter_*` ids). `qualifiedTinted` is |T|. Zero participants must not complete. Dash-to-census should be ~1500 ms, inside ring life.
4. After 20 s: every id in T is present, inactive, and without an override (or equal to that id's baseline). A missing T row fails (`tinted object missing at rest`). Residual tint must not complete.
5. `.froststyle off` matches server `FROSTSTYLE enabled=False restored=<n>`. Client then shows `FROSTSTYLE client enabled=False`. A Sample image with no `.froststyle` handler produces no `FROSTSTYLE` line within the bounded wait: the off step fails with `no handler line for .froststyle off` and the run is **aborted**.
6. `poolReuse`: `.frostdash dash`, PrintWindow children **start** at +100/+500/+900 ms, load-only census at +1.5 s (same prepared DLL), then await the shots. `censusDelayMs` ~1500 (fail `census late` if > 2000). Nonempty **R** ⊂ T, each a present row, active with no Sample override. `reusedUntinted` is |R|; `retriesUsed` is the dash+census attempts performed (budget 3). Empty R after retries is `reuse not observed` and fails. `capture.available=true` with `paths` only when frames were saved. Record each frame `startedMs`/`endedMs`. `visual.verified` stays false until a person or the reviewing agent views the frames (`verifiedBy` / `verifiedAt`). A nonempty file is not visual verification.
7. `.froststyle on` matches `FROSTSTYLE enabled=True restored=0`.
8. After 20 s: final rest census; every id in T present, no override anywhere in T.
9. `informational` may show a 122-id whole-scene equality; ignore it as a pass condition.
10. Teardown `.frostdash off` matches `FROSTDASH cleanup alive=`. Final checks pass. Admin restored. View the saved PNGs before treating visual reuse as verified.

Do not manipulate the game's pool to manufacture success.
