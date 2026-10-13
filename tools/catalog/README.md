# tools/catalog — offline index of the shipped content catalog

`index-catalog.mjs` reads V Rising's shipped Unity Entities **content catalog** straight off disk and
writes a JSON index: every cataloged object id → the archive file that holds it, its Unity type,
and any name that can be recovered without running the game. It also enumerates what is *inside*
the archives, which is where the human-readable asset names actually live.

No game process, no Unity, no npm dependencies. The game install is opened read-only.

Design notes, the runtime loading path and the M5 research gate are in the internal research
notes. The discovery that the game uses Entities Content Archives at all is job RD1.

```
node tools/catalog/index-catalog.mjs --deep --inventory --verify
```

Requires Node 18+ (developed on v22). Run time on a warm cache: ~5.5 s for everything.

## Options

| Flag | Effect |
|---|---|
| `--game <dir>` | V Rising install root. Default `C:\Program Files (x86)\Steam\steamapps\common\VRising`. |
| `--out <dir>` | Where the JSON goes. Default: this folder. |
| *(none)* | Decode `archive_dependencies.bin` only. ~130 ms. |
| `--archives` | Also parse every archive's UnityFS header and directory (inner file names, block table). ~340 ms. |
| `--deep` | `--archives` plus: LZ4-decompress the `SerializedFile` metadata of the archives that hold cataloged objects, to resolve each object's Unity class id, `m_Name`, and (for `MonoBehaviour`) the `MonoScript` class name. |
| `--inventory` | `--archives` plus: enumerate the `SerializedFile` object table of **every** archive into `catalog-inventory.json` — this is the real asset-name index. |
| `--verify` | Cross-check the decode against the shipped `archive_dependencies.txt`. Prints `PASS`/`FAIL`. |
| `--deep-cap`, `--name-cap`, `--inventory-cap` | Per-archive decompression budgets in bytes (8 MB / 64 MB / 4 MB). |

The inventory reads the first 1 MB of each archive and, when the object bodies continue past it, re-reads up to `--inventory-cap`. Before 2026-09-28 the re-read happened only when the object *table* was cut short, so names in large prefab archives (for example `HYB_NPCDracula_Evolved`) were missed at every cap.
| `--pretty` | Pretty-print `catalog-index.json` (much bigger). |

## Outputs

| File | Contents |
|---|---|
| `catalog-index.json` | Source stamp + sha256, decoded blob header and root offsets, byte-coverage regions, summary, and the full `archives` / `files` / `dependencySets` / `objects` / `scenes` / `blobs` tables. |
| `catalog-summary.json` | The same minus the big tables — the file to read first. |
| `catalog-inventory.json` | Per-archive object-table census: object count, class-id histogram, and every `m_Name` recovered within the budget. Written only with `--inventory`. |
| `model-sections.json` | Written by `model-sections.mjs` (not by `index-catalog.mjs`): every `HYB_*` prefab root name mapped to the entity-scene section that holds it, `<scene guid>:<section>`. The table `ctx.Models` reads; see `docs/modules.md`, "Models". |

The three JSON outputs are derived data, are specific to one game build, and are `.gitignore`d.
Re-run the tool after a game update; `source.catalogSha256` in the output is the build stamp.

## Where the catalog lives

```
<game>/VRising_Data/StreamingAssets/ContentArchives/
    archive_dependencies.bin     486,664 bytes   the catalog
    archive_dependencies.txt   3,134,616 bytes   a shipped human-readable dump of the same data
    <32 hex chars>             4,409 files       the archives themselves (UnityFS, LZ4, 2022.3.58f1)
```

Those names are the constants on `Unity.Entities.Content.RuntimeContentManager`
(client metadata): `k_NameSpaceString = "RTC"`,
`k_ContentArchiveDirectory = "ContentArchives"`,
`k_ContentCatalogFilename = "archive_dependencies.bin"`.

`archive_dependencies.txt` is the output of `RuntimeContentCatalogDataUtility.Print`
and ships next to the binary. `--verify` uses it as an oracle: it must agree on
archive ids, object ids and the expanded dependency-edge count.

## Binary layout of `archive_dependencies.bin`

The file is a serialized `BlobAssetReference<RuntimeContentCatalogData>` — a 4-byte version int, a
32-byte `Unity.Entities.Serialization.BlobAssetHeader`, then the blob itself.

```
offset  size  field
     0     4  int    version                      = 1
     4     8  void*  BlobAssetHeader.ValidationPtr= 0
    12     4  int    BlobAssetHeader.Length       = 486628   (== file size - 36)
    16     4  int    BlobAssetHeader.Allocator    = 1 (None)
    20     8  ulong  BlobAssetHeader.Hash         = 0xdd23f86e
    28     8  ulong  BlobAssetHeader.Padding      = 0
    36     …  blob data
```

The blob root is `RuntimeContentCatalogData`, six `BlobArray`s, 48 bytes:

| Blob offset | Field | Element | Element size | This build |
|---|---|---|---|---|
| `0x00` | `Archives` | `ContentArchiveLocation` | 16 | 4410 @ blob 63216 |
| `0x08` | `Files` | `ContentFileLocation` | 24 | 4410 @ blob 133776 |
| `0x10` | `Objects` | `ContentObjectLocation` | 64 | 1151 @ blob 239616 |
| `0x18` | `Scenes` | `ContentSceneLocation` | 180 | **0** |
| `0x20` | `Dependencies` | `BlobArray<int>` | 8 | 1904 sets @ blob 48 |
| `0x28` | `Blobs` | `ContentBlobLocation` | 72 | **0** |

A `BlobArray<T>` is `{ int m_OffsetPtr; int m_Length; }` whose payload starts at *the address of
`m_OffsetPtr`* plus `m_OffsetPtr`, so every offset above is field-relative, not root-relative.

Element layouts, all read straight from the client metadata:

```
ContentArchiveLocation  { ContentArchiveId ArchiveId;   }              // Hash128, 16 bytes
ContentFileLocation     { ContentFileId FileId @0x00;                  // Hash128
                          int ArchiveIndex @0x10; int DependencyIndex @0x14; }
ContentObjectLocation   { UnsafeUntypedWeakReferenceId ObjectId @0x00; // 48 bytes
                          int FileIndex @0x30; long LocalIdentifierInFile @0x38; }
ContentSceneLocation    { …ObjectId @0x00; int FileIndex @0x30; FixedString128Bytes SceneName @0x34; }
ContentBlobLocation     { …ObjectId @0x00; int FileIndex @0x30; long Offset @0x38; long Length @0x40; }

UnsafeUntypedWeakReferenceId
  RuntimeGlobalObjectId GlobalId @0x00     // 40 bytes
  short                 GenerationType @0x28
RuntimeGlobalObjectId
  long SceneObjectIdentifier0 @0x00
  long SceneObjectIdentifier1 @0x08
  Hash128 AssetGUID @0x10
  int IdentifierType @0x20
WeakReferenceGenerationType
  Unknown=0 UnityObject=1 Texture=2 GameObjectScene=3 EntityScene=4 EntityPrefab=5
  SubSceneObjectReferences=6
```

`Hash128`'s text form writes each byte **low nibble first**: blob bytes `1e 72 d7 26 …` print as
`e1277d62…`. That is how the tool reproduces the ids in `archive_dependencies.txt`.

Every byte of the blob is accounted for except **20 bytes** of zero padding, reported in
`summary.unattributedBlobRegions` (blob `63212–63216`, `378276–378288`, `443228–443232`).

### What the catalog actually contains in this build

| | |
|---|---|
| Archives | 4410 (4409 on disk + one all-zero id used as a null dependency) |
| Files | 4410 — one content file per archive; the file id equals the archive id |
| Objects | **1151** |
| Scenes | 0 |
| Blobs | 0 |
| Dependency sets | 1904 unique, 55 316 unique edges, 56 451 after expansion to the 4410 files |
| Object generation types | `SubSceneObjectReferences` (6) × 1151, nothing else |
| Object identifier types | `0` × 1151 |
| Distinct `AssetGUID`s among objects | **204**, every one of them the guid of a shipped entity scene under `StreamingAssets/EntityScenes/` |
| Object Unity classes (`--deep`) | `MonoBehaviour` 1109, `Mesh` 38, `Material` 4 |
| MonoBehaviour script class (`--deep`) | `ReferencedUnityObjects` × 1109 |

**This is the single most important finding for M5.** The shipped catalog is *not* an asset
catalog. It indexes 1151 entry points, 1109 of which are `Unity.Entities.Serialization.ReferencedUnityObjects`
containers — one per entity-scene section — holding the array of `UnityEngine.Object` references
that the section's entities point at. Individual presentation assets (particle prefabs, materials,
meshes, textures, VFX graphs) are **not** individually addressable through
`UntypedWeakReferenceId`s in this build. They are reached by loading a section's
`ReferencedUnityObjects` and indexing into its array — exactly the mechanism
`SequenceAssetReferences.Objects` uses on the sequence side.

## The archives, and where names come from

Every archive is a `UnityFS` container built with **2022.3.58f1**, LZ4/LZ4HC block-compressed,
flags `0x243`. Directory nodes: 1555 archives hold one node, 2853 hold the serialized file plus a
`.resS` stream sidecar, 1 holds three. The first node's path is always the archive id, which is
also the content file id and therefore the mounted name.

One non-obvious mechanic the tool had to get right: **flag `0x200`
(`BlockInfoNeedPaddingAtStart`) means the data blocks begin at the next 16-byte boundary.** Reading
from the unaligned offset decodes to the wrong length (1616 instead of 1612 for archive
`2cc06ff8…`) and yields a plausible-looking but shifted `SerializedFile` header. Every "unsupported
SerializedFile version 0" style failure during development traced back to this.

Inside each archive is a standard `SerializedFile` version 22 with **type trees enabled**. The tool
walks the type table (skipping the blob-form type trees), the object table, the script types and
the externals. Names are read from object bodies:

| Class | `m_Name` at |
|---|---|
| `NamedObject` subclasses (`Material`, `Mesh`, `Texture2D`, `Sprite`, `AnimationClip`, `Shader`, `VisualEffectAsset`, …) | body offset 0 |
| `GameObject` (1) | `4 + m_Component.Length * 12 + 4` |
| `MonoBehaviour` (114) | 28 — after `m_GameObject` (PPtr, 12) + `m_Enabled` (padded to 4) + `m_Script` (PPtr, 12) |
| `MonoScript` (115) | body offset 0; reached through a `MonoBehaviour`'s `m_Script` PPtr, whose `m_FileID` indexes the file's externals (each external's `pathName` is a sibling archive id) |

`--inventory` results for this build:

```
288 566 objects across 4408 archives (1 archive's metadata exceeds the default budget)
 82 322 names recovered, 24 454 of them distinct
GameObject 83814  Transform 75155  MonoBehaviour 36266  Mesh 13133  ParticleSystem 12917
ParticleSystemRenderer 12916  Texture2D 10636  RectTransform 8659  Material 7532
AnimationClip 7401  CanvasRenderer 6986  Sprite 3297  SkinnedMeshRenderer 1221 …
```

Spot check: searching the inventory for `Charm` finds `GameObject :: Beam_Charm_Channeling_01` and
`_02`, i.e. the presentation bodies of `(Sequence)AB_Charm_Channeling` (`-389499105`) — the beam
the Effects module already plays live. Searching for `Coil` finds the Tesla-coil meshes and
materials. So an offline, name-searchable index of the game's presentation assets **is** achievable
without the game running, and this tool produces a first cut of it.

## Known gaps

1. **Names for components have none to recover.** All 1109 cataloged `ReferencedUnityObjects`
   have an empty `m_Name`; only their script class is meaningful. The same goes for `Transform`,
   `ParticleSystemRenderer`, `CanvasRenderer` and the other component classes in the inventory —
   Unity does not serialize a name for them. The owning `GameObject`'s name is the useful label and
   is reachable through the component's `m_GameObject` PPtr; the tool does not follow that link yet.
2. **`--inventory` name coverage is budget-bound.** 82 322 of 288 566 objects are named, because a
   name lives in the object's *body* and bodies are spread through the whole (up to 349 MB) archive
   while the default budget decompresses 4 MB. Raising `--inventory-cap` raises coverage at a
   proportional cost in time and memory; a future pass should decompress only the blocks that
   actually cover the wanted body offsets instead of a prefix.
3. **No type-tree walk.** The tool skips type trees rather than parsing them, so it reads fields by
   hard-coded offsets. That is exact for the four layouts above and would not survive a Unity
   version change. Field-accurate reads (e.g. `ReferencedUnityObjects.Array`, which would let the
   index resolve *which* assets each cataloged container points at) need the type tree.
4. **`Scenes` and `Blobs` are empty in this build**, so those two decoders are written from the
   struct layout and are untested against real data.
5. **The mount prefix used at runtime is inferred.** `DefaultContentFileNameFunc`
   builds the mounted name from the static `ArchivePrefix`; the only matching
   string literal among the game's string literals is `"ns:/RTC/"`, so the tool emits
   `ns:/RTC/<fileId>`. A neighboring literal, `"a{0}:"`, is an unexplained candidate
   mount prefix. A live `RuntimeContentManager.GetLoadedArchivesStateDump()` readback settles it
   (the research gate in the internal research notes).
6. **The 20 unattributed padding bytes** are reported rather than assumed; they are all zero.

## Recipe layer: maintained usage recipes

Walkthrough (search then inspect, green nova, Charm particles vs beam, how to read
support): [`docs/catalog-usage.md`](../../docs/catalog-usage.md).
Runnable examples: [`examples/`](examples/README.md).

`recipes/*.json` are **maintained by hand** and checked in; the three `catalog-*.json` files are
**generated** and git-ignored. The indexer writes only its three files and never touches
`recipes/` (`test/regeneration.test.mjs` proves it against the installed catalog). A recipe records
what was actually observed about one operation on one asset composition, with typed identities,
role-tagged relationships, per-operation status and per-evidence build stamps. The schema is
`recipes/SCHEMA.md` (`progenitor.catalog.recipe/1`).

Seeded on 2026-09-06 from the frost-dash and Charm research (`docs/asset-research-workflow.md`):

| Recipe | Family | Kind |
|---|---|---|
| `wendigo-frost-nova-ring` | `wendigo-frost-nova` | `gameplay-payload` (damaging ring, spawned at both dash edges; the payload the green nova appearance restyles) |
| `wendigo-frost-nova-appearance` | `wendigo-frost-nova` | `visual-instance-override` (radius 18, green crystals, owned rings only; Sample-specific recolor) |
| `charm-caster-link-sequence` | `charm-channeling` | `visual-sequence` (`-389499105`: one weapon material; nothing drawn, no beam) |
| `charm-target-channel-sequence` | `charm-channeling` | `visual-sequence` (`-541113174`: particles observed, no beam drawn; moving beam unverified) |
| `charm-loop-particle-borrow` | `charm-channeling` | `isolated-particle` (owned copy of `Part_Charm_Loop_01`; not the spell, not a beam) |

Inspect each recipe's `revision` field (bump on any content change). Offline `inspect`
reports `unknown-stamps` for live-stamped entries because the game identity and framework
build are `not-checkable-offline`; only the catalog stamp is compared. The dated live evidence
(catalog live session 2026-09-07, tracked Charm unload, clean and historical active-ring unloads,
the 2026-09-08 pool-reuse visual runs and client Sample swaps) is in the internal research notes.

### Commands

Working directory: the repository root.

```
node tools/catalog/recipe.mjs list
node tools/catalog/recipe.mjs validate
node tools/catalog/recipe.mjs search nova                       # free text over id, family, aliases, title, names, summary, control names, operation keys
node tools/catalog/recipe.mjs search "frost nova radius"        # every word (or stem) must hit; appearance ranks first
node tools/catalog/recipe.mjs search prefab:-1227952025          # typed identity; also sequence: archive: object: name:
node tools/catalog/recipe.mjs search -1227952025                 # bare number: any GUID domain, reports which one hit; payload/primary first
node tools/catalog/recipe.mjs search charm --inventory --limit 5 # also list generated-inventory name matches (distinct objects)
node tools/catalog/recipe.mjs inspect charm-target-channel-sequence [--text]
node tools/catalog/recipe.mjs associations wendigo-frost-nova-appearance
node --test "tools/catalog/test/*.test.mjs"                     # offline tests, no game
```

`--json` (default) prints one structured object; `--text` prints a human summary. `--recipes <dir>`
and `--generated <dir>` redirect the two inputs. Exit codes: 0 ok, 2 invalid input (`invalid-query`,
`invalid-identity-domain`, `invalid-identity-value`, `invalid-limit`, `invalid-option`,
`invalid-command`), 3 `unknown-recipe` (with suggestions and the known ids), 4 invalid recipe data
(`invalid-recipe-file` with every problem, `duplicate-recipe-id`, `recipes-missing`), 5 unexpected.
`--limit` is 1..200, default 20; text results are ordered by number of words matched, then score,
with one best hit per family first; identity hits prefer a role containing `payload` or the
primary identity, not a counterpart. `truncated` says when the limit cut them. Empty result
sets include `suggestions` (closest recipe ids by word overlap).

`validate` and every query command share one directory-level check (`validateRecipeDirectory`:
per-file schema, unreadable JSON, duplicate ids across files), so `validate` can never pass a
catalog that `list` refuses. The validator is total: malformed input yields problem strings, never
an exception.

The generated inventory carries its own generation stamp (`generated`, `source.catalogSha256`,
written by `index-catalog.mjs --inventory`). `loadInventory` reports `generation`: `stamped` when
the inventory's stamp equals the summary's catalog hash, `mixed` when they differ (the indexer was
re-run without `--inventory` after a game update) and `unstamped` for an inventory older than the
stamp; only `stamped` is available for associations, and the summary's hash is never borrowed for
an unstamped file. Rows whose `pathID` arrives as a non-lossless JSON number are skipped and
counted in `counts.rejectedRows`.

The query logic lives in `lib/recipes.mjs` (+ `lib/identity.mjs`, `lib/inventory.mjs`). MCP tools
call the same functions so the JSON matches this CLI. An in-game browser still does not exist.
Nothing here loads, casts, previews or starts the game. A missing inventory is reported
(`inventory-unavailable`), never regenerated.

### MCP access

Four tools on the Progenitor MCP server (`../mcp/catalog-tools.mjs`, registered in
`../mcp/server.mjs`). Same JSON as this CLI. Walkthrough: [`docs/catalog-usage.md`](../../docs/catalog-usage.md).
Listed in [`docs/mcp.md`](../../docs/mcp.md#mcp-tools). Restart the MCP process after a
`server.mjs` change.

| Tool | Arguments |
|---|---|
| `progenitor_catalog_search` | `{query, limit?, includeInventory?}`. `query` may be a JSON string, number or boolean (coerced to string). |
| `progenitor_catalog_inspect` | `{recipe}` (string, number or boolean, coerced to string) |
| `progenitor_catalog_associations` | `{recipe}` (same) |
| `progenitor_catalog_list` | none; returns `{dir, count, recipes}` like the CLI |

Structured errors use the same codes as this CLI (`invalid-query`, `invalid-identity-domain`,
`invalid-identity-value`, `invalid-limit`, `unknown-recipe` with suggestions and known ids,
`invalid-recipe-file`, `duplicate-recipe-id`, `recipes-missing`).

### What the catalog cannot do

These tools search and inspect **maintained recipes**. They do not offer loading or preview.
A search hit is not availability.

- no loading (search `load asset` is not an offered operation)
- no preview (search `preview` is not an offered operation)
- no possession
- no archive mounting
- no runtime availability

### Agent-reported-only operations

Read from offline `inspect` (`operations.*.support.agentReportedOnly`). These operations are
not independently recorded; the JSON flag is the warning, and the operation `note` names the
agent-reported evidence.

None. Offline inspect currently reports an empty list: `agentReportedOnly` is false on every
operation.

### What inspect can and cannot say

* Identities are strings: signed int32 GUID hashes and int64 Unity pathIDs survive JSON and
  JavaScript unchanged (`test/identity.test.mjs` checks 2^53+1 and the int64 extremes).
* Relationships are `known` (both ends identified, link observed), `inferred` or `unresolved`; the
  validator refuses a `known` link to an `external:` endpoint and a `known` archive object by
  name match. A gameplay GUID is never equated to an archive object.
* Inventory associations are computed at inspect time by name (+ Unity type) against the generated
  inventory: `inferred` (unique), `ambiguous` (several distinct objects, none selected),
  `unresolved`, or `inventory-unavailable`. Coverage is bounded by the indexer's name budget
  (known gap 2): the Wendigo ring's crystal shader has no named row and stays `unresolved`.
  `inventory.state` is `indexed` | `inventory-unavailable` | `not-loaded`; `inventory.note` is
  always `index presence only; runtime availability is not observed offline`. Never the bare
  token `available`.
* Build stamps per evidence (`contentCatalogSha256`, `gameIdentity`, `framework`, and for
  `rig-run` `frameworkSides.{server,client}`) are compared with the current view: offline that
  is only the generated inventory's catalog sha256; the game identity and framework sides are
  `not-checkable-offline`. A `rig-run`'s combined `framework` string is **legacy-combined**
  (never a match). An operation declares `sides` (`["client"]`, `["server"]`, or both;
  default both) and inspect compares only those sides against `currentBuild.frameworkSides`.
  Applicability is `stale` when a required-side hash changed, `matches-checkable-stamps` only
  when every stamp is known and matches, else `unknown-stamps`. **Every 2026-09-06 recording
  carries null stamps**, so nothing in the seed recipes can be transferred to the current
  build by this tool; `currentVerification.claimable` is always `false` offline and
  `runtimeAvailability.state` is always `not-observed-offline`. A run recorded on a different
  required-side Sample/Core/Dev hash or generation is `stale` and `runsOtherBuild`, never
  `runsComplete`. Recipe-level `buildSummary` counts `{stamped, unstamped, stale,
  matchesCatalog}` — it is a census, not a claim.
* Every evidence entry is classified as `provenanceClass`: `log` (saved log present), `media`
  (screenshot/clip present), `readback` (rig `report.json` or readback file present),
  `agent-reported` (kind `report`, or a path ending `.md`, or a summary that is agent-reported,
  or basename `bridge-responses.md`), `source` (source-inspection / native-analyze), `missing` (path
  given but the file or run directory is absent). A markdown report is never `source`. Presence of a file does **not** promote the
  operation to verified. `operations[].mediaPresent` is `true` only when at least one
  screenshot/clip file is present, `false` when a cited media path is missing, and `null`
  when the operation cites no media.
* Operations carry `support`: `{classes, strongest, missing, agentReportedOnly,
  runsNotComplete, runsComplete, runsOtherBuild}`. `strongest` is `none` for `untested` / `not-applicable`
  (rendered `[no evidence; untested]`). A missing media path cannot raise `strongest`
  above `source`. An operation whose only evidence is agent-reported or missing is
  `agentReportedOnly` and the text renderer prints `not independently recorded`.
  `run complete` in the text line means a cited `rig-run` finished on a matching build; a `readback-file` is
  not a run. A complete run whose required-side `frameworkSides` differ from the current view or
  the recipe's recovered side stamps is `runsOtherBuild` (text: `run other build`) and cannot raise
  `strongest` above `source`. A client-only hash change does not stale an explicitly server-only
  operation; a missing required-side hash stays unknown, never a match.
* Kind `rig-run` cites a live-rig **run directory**. Inspect reads `report.json` (capped at 4 MB; never
  `trace.jsonl` in full) and attaches `evidence[].run` `{found, status, session, images,
  finalChecks, steps, cleanup, contentCatalogSha256, gameIdentity, framework, frameworkSides,
  stampErrors, stampPhases, stampTransition, agentReported:false}`, or `{found:false, reason}`. A path that is
  not a directory, or a symlinked directory (`lstat`), is refused. A run
  whose `status` is not `complete` never counts as support for an `observed-*` operation
  and is listed in `support.runsNotComplete`; inspect flags `run not complete` on any
  operation that cites it. Research run directories stay outside this repository; recipes
  may name them by path only. Side identities on the recipe were recovered from the saved
  report/ping of each run, not from inspect.
* Media and implementation files are checked for presence separately from the claim they
  support; the logs, screenshots, clip, rig runs and analyze exports live outside the repository.
  Recipes cite them as `<local-evidence>/...`, which resolves under the folder named by the
  `PROGENITOR_LOCAL_EVIDENCE` environment variable; without it they report `MISSING`. Paths that
  start with `../` resolve against the main checkout, so a linked worktree finds a sibling folder
  beside the main checkout. Each run reports `present`/`MISSING`.

### Coverage limits of this increment

Two families, five recipes. See **What the catalog cannot do** above: no loading, no preview,
no possession, no archive mounting, no runtime availability, and no Styx/NPC work. Ability-group
chains (Charm's ability -> buff application is `unresolved`), sequence graph inputs, and archive
dependency costs are also out of scope. Per-hit attribution of the nova's damage is still
open. Current operation status lives in the
[workflow recipe table](../../docs/asset-research-workflow.md#current-recipes-and-verification-boundaries):
appearance poolReuse is observed-success with subsequent untinted reuse and visual evidence (A11);
tracked Charm unload while active is observed-success including client Sample Swap (B6);
unload-with-a-live-ring is observed-success (ring L9 / appearance A9); ring gameplayHits remains
observed-partial; Charm client carriers still need an explicit client-side destroy. A green
offline suite certifies the records and the query code, not the game.

## Player animation import reference

Use [the player animation workflow](../../docs/player-animation-import.md) and `player-animations.mjs --inventory <catalog-directory> --out <new-json-path>` to generate a stamped name-based candidate map. The report preserves all animation rows, exact identities and unresolved bindings; it does not export clips or establish playback.

`player-animation-bindings.mjs --input <decode-manifest.json> --out <fresh-json-path>` resolves standard player prefab/collection/override identities from exact-consumption type-tree exports. See the same workflow for its manifest, input hashes and qualification limits. This tool does not decode raw archives or run animations.

`player-animation-observations.mjs --states <controller-state-map.json> --capture <capture.json> --out <fresh-json-path>` correlates recorded current-state hashes with decoded candidates. It preserves ambiguous matches, reports unknown hashes and separates Animator/body/controller/avatar profiles. It does not record the game or certify full coverage, clip identity or exact timing. The workflow documents the supported research capture format and bridge-driven test procedure.
