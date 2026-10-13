# Using the offline recipe catalog

How to discover the five maintained recipes and read a supported usage from them. This page is the walkthrough. Commands, exit codes and inspect-field semantics stay in the [catalog README](../tools/catalog/README.md#recipe-layer-maintained-usage-recipes); the schema is [`progenitor.catalog.recipe/1`](../tools/catalog/recipes/SCHEMA.md).

The catalog never starts the game. A search hit means a recipe file exists, not that an asset is loaded, loadable or safe to play.

**Verification of this page.** MCP tools (`progenitor_catalog_search`, `progenitor_catalog_inspect`, `progenitor_catalog_associations`, `progenitor_catalog_list`) are **source-checked** against [`tools/mcp/catalog-tools.mjs`](../tools/mcp/catalog-tools.mjs) / [`tools/mcp/server.mjs`](../tools/mcp/server.mjs) and offline-tested by `node --test` on `tools/catalog/test/*.test.mjs` and `tools/mcp/test/*.test.mjs`. They are not **verified live** — they do not contact the game. Recipe operation statuses are whatever `inspect` prints; this page does not promote them to **verified live**. Runnable examples: [`tools/catalog/examples/`](../tools/catalog/examples/README.md). Captured output below is from those examples on 2026-09-08 (offline catalog, exit 0).

List the five recipes first if you do not already have an id:

```
node tools/catalog/recipe.mjs list --text
```

MCP: `progenitor_catalog_list` with no arguments.

## Find a green nova effect and show me the supported way to use it

1. Search, then inspect. Do not stop at the search hit.

```
node tools/catalog/recipe.mjs search "green nova" --text
node tools/catalog/recipe.mjs inspect wendigo-frost-nova-appearance --text
```

MCP (same JSON as the CLI):

- `progenitor_catalog_search` `{ "query": "green nova" }`
- `progenitor_catalog_inspect` `{ "recipe": "wendigo-frost-nova-appearance" }`

Equivalent example: `node tools/catalog/examples/find-green-nova.mjs` from the repository root.

2. Read these fields on the inspect result (not on the search hit):

| Field | What to take from it |
|---|---|
| `recipeId` | `wendigo-frost-nova-appearance` |
| `kind` | `visual-instance-override` — a restyle of an existing ring, not a new payload |
| `requirements.initialization` | ordered steps; step 1 is spawn [`wendigo-frost-nova-ring`](../tools/catalog/recipes/wendigo-frost-nova-ring.json) first |
| `controls` | tested values only: radius `18`; crystal color the four property-block vectors below. Not a range. |
| `implementation` | [`SampleFrostNovaAppearance.cs`](../samples/Progenitor.Sample/SampleFrostNovaAppearance.cs), called from [`SampleFrostDash.cs`](../samples/Progenitor.Sample/SampleFrostDash.cs) |
| `operations` | each key’s `status`, `support` (the inspect `--text` line), `applicability` |
| `limitations` | quote these; they bound the supported usage |
| `currentVerification.claimable` | always `false` offline |
| `runtimeAvailability` | always `not-observed-offline` |

Search also returns `wendigo-frost-nova-ring` (`kind` `gameplay-payload`): that is the damaging ring the appearance restyles, not the green look.

3. Supported usage in one line: after spawning the ring, `SampleFrostNovaAppearance` writes `EndRadius=18` and per-slot crystal color on **owned** rings only. It is not a generic recolor API.

Expected observations (captured on 2026-09-08 from the offline catalog, `node tools/catalog/examples/find-green-nova.mjs`):

```
search query: "green nova"
scope: offline recipe search; a hit says a recipe exists, not that its asset is loaded, loadable or safe to play
hits:
  wendigo-frost-nova-appearance  [visual-instance-override]  Wendigo frost nova: radius 18 and green crystals on owned rings only
  wendigo-frost-nova-ring  [gameplay-payload]  Wendigo frost nova ring payload at both dash edges

recipe: wendigo-frost-nova-appearance
kind: visual-instance-override
supported: After spawning wendigo-frost-nova-ring, SampleFrostNovaAppearance writes EndRadius=18 and per-slot crystal colour on owned rings only (not a generic recolour API).
initialization:
  1. Server: after spawning the ring (wendigo-frost-nova-ring), write EndRadius=18 and register the ring for the ownership packet.
  2. Client: on the packet, record the NetworkId; Before presentation, find the replicated ring with that NetworkId and the expected prefab GUID and write EndRadius=18 on it.
  3. Client, Before presentation: restore every property block written last frame (Restore()).
  4. Client, After presentation: for each ParticleData row whose sequence resolves to an owned ring, … set _BaseColor/_EmissiveColor/_GroundTint/_Fresnel_Color, SetPropertyBlock(edited, slot).
  5. Keep restyling retained particles after sequence output ends while their identities still match.
limitations:
  - Sample-specific recolour keyed on one shader name and one ring prefab; it does not establish a generic safe recolour operation for other game effects.
  - Only one radius value (18) and one colour set were tested.
  - The pale mist is not recoloured and its material was not traced.
  …
operations:
  discovery        observed-success  [log, source; media none; run none] sides=client applicability=unknown-stamps
  playback         observed-success  [media, log; media present; run none] sides=server,client applicability=unknown-stamps
  appearance       observed-success  [media; media present; run none] sides=server,client applicability=unknown-stamps
  controls         observed-success  [log; media none; run none] sides=server,client applicability=unknown-stamps
  gameplayHits     untested          [no evidence; untested] sides=server applicability=no-evidence
  naturalCleanup   observed-success  [media, log; media present; run none] sides=client applicability=unknown-stamps
  explicitCleanup  observed-success  [log, readback, source; media none; run complete] sides=server applicability=unknown-stamps
  poolReuse        observed-success  [media, log, readback; media present; run complete] sides=server,client applicability=unknown-stamps
currentVerification.claimable=false
runtimeAvailability=not-observed-offline
controls:
  radius: tested 18; 18
    implementation: SampleFrostNovaAppearance.Radius (const) and SetRadius
  crystal colour: tested {"_BaseColor":[0.12,0.65,0.25,1],"_EmissiveColor":[0.05,1.4,0.18,1],"_GroundTint":[0.15,0.75,0.25,0],"_Fresnel_Color":[0.2,0.8,0.3,0]}; "same four colours"
    implementation: SampleFrostNovaAppearance.ColorParticle
```

`…` marks lines omitted here; the example prints them in full.

## Charm: particles versus the beam

There is no verified Charm beam in this catalog. Search `charm beam`, then inspect all three family members. MCP: `progenitor_catalog_search` `{ "query": "charm beam" }` then `progenitor_catalog_inspect` on each id. CLI:

```
node tools/catalog/recipe.mjs search "charm beam" --text
node tools/catalog/recipe.mjs inspect charm-target-channel-sequence --text
node tools/catalog/recipe.mjs inspect charm-caster-link-sequence --text
node tools/catalog/recipe.mjs inspect charm-loop-particle-borrow --text
```

Equivalent example: `node tools/catalog/examples/charm-particles-vs-beam.mjs`.

| Recipe | Kind | Appearance | What it draws |
|---|---|---|---|
| `charm-caster-link-sequence` | `visual-sequence` (`-389499105`) | `observed-failure` | nothing (no beam, no particles). Link default. |
| `charm-target-channel-sequence` | `visual-sequence` (`-541113174`) | `observed-partial` | red crown/swirl particles at the player; **no beam** between endpoints |
| `charm-loop-particle-borrow` | `isolated-particle` | `observed-success` of an owned `Part_Charm_Loop_01` copy | one particle body; not the spell, not a beam |

Read `operations.appearance.status` as written:

- `observed-failure` — the prescribed call ran and nothing visible was drawn. Do not treat this sequence as the Charm beam.
- `observed-partial` — part of the requested visual was seen (particles) and the rest was not (beam). Do not promote it to `observed-success`.
- `isolated-particle` is a `kind`, not a beam recipe. Its appearance success is the borrowed clone, not Charm channeling.

Expected observations (captured on 2026-09-08 from the offline catalog, trimmed):

```
search query: "charm beam"
scope: offline recipe search; a hit says a recipe exists, not that its asset is loaded, loadable or safe to play
hits:
  charm-target-channel-sequence  [visual-sequence]  … particles observed, no beam drawn
  charm-caster-link-sequence  [visual-sequence]  … nothing drawn (not a beam)
  charm-loop-particle-borrow  [isolated-particle]  … not the Charm spell and not a beam

how to tell particles from the beam:
  charm-caster-link-sequence: appearance=observed-failure  (draws nothing, no beam)
  charm-target-channel-sequence: appearance=observed-partial  (particles observed, no beam drawn)
  charm-loop-particle-borrow: kind=isolated-particle  (isolated particle; not the spell, not a beam)

recipe: charm-caster-link-sequence
supported: ctx.Effects.ServerBeamLink(source, target, seconds: 20f, sequenceGuid: -389499105) — Link default; nothing drawn.
  appearance       observed-failure  [media, log, agent-reported; media present; run none] sides=server,client applicability=unknown-stamps
currentVerification.claimable=false
runtimeAvailability=not-observed-offline

recipe: charm-target-channel-sequence
supported: ctx.Effects.ServerBeamLink(source, target, seconds: 20f, sequenceGuid: -541113174) — particles at the player, no beam between endpoints.
  appearance       observed-partial  [media, agent-reported; media present; run none] sides=server,client applicability=unknown-stamps

recipe: charm-loop-particle-borrow
kind: isolated-particle
supported: ctx.Effects.Borrow(AssetKind.Particle, "Part_Charm_Loop_01") — owned clone of one particle body, not the Charm spell.
```

Quote each recipe’s `limitations` from inspect (the example prints them). Call shapes in [`SampleCatalogRecipeExamples.cs`](../samples/Progenitor.Sample/SampleCatalogRecipeExamples.cs) are recorded as **compile-verified**; that file is not **verified live**.

## How to read provenance, support, and buildSummary

Inspect adds these; they are not stored on the recipe. Field rules: [schema — what inspect adds](../tools/catalog/recipes/SCHEMA.md#what-inspect-adds-not-stored) and [catalog README](../tools/catalog/README.md#what-inspect-can-and-cannot-say).

**A present file is not current verification.** `evidence[].media.present` / `implementation[].file.present` only answers “is that path on disk right now?”. `currentVerification.claimable` stays `false` offline even when every file is present.

| Field | How to read it |
|---|---|
| `evidence[].provenanceClass` | `log`, `media`, `readback`, `source`, `agent-reported`, or `missing` (path given but absent). |
| `operations[].support` | `{classes, strongest, missing, agentReportedOnly, runsNotComplete, runsComplete, runsOtherBuild}`. Inspect `--text` prints this as the bracketed line. `strongest` is `none` for `untested` / `not-applicable`. A missing media path cannot raise `strongest` above `source`. A complete run on another build is `runsOtherBuild`, never `runsComplete`. |
| `operations[].mediaPresent` | `true` only when a screenshot/clip file is present; `false` when a cited media path is missing; `null` when the operation cites no media. |
| `inventory.state` | `indexed` / `inventory-unavailable` / `not-loaded`. `inventory.note` is index presence only; not runtime availability. Never the bare token `available`. |
| `operations[].sides` | `["client"]`, `["server"]`, or `["server","client"]`: the process images the observation depends on. Absent defaults to both. Inspect compares only those sides against `currentBuild.frameworkSides`. |
| `operations[].applicability` | `stale` if a required-side hash changed; `matches-checkable-stamps` only when every stamp is known and matches; otherwise `unknown-stamps`. Offline, game identity and framework sides are `not-checkable-offline`, so live-stamped evidence still shows `unknown-stamps`. A `rig-run` combined `framework` string is `legacy-combined` and never matches. |
| `buildSummary` | census `{stamped, unstamped, stale, matchesCatalog}` over the recipe’s evidence. Not a claim that the recipe is current. |

**Agent-reported warning.** When `support.agentReportedOnly` is true, inspect `--text` prints `agent-reported only` and `not independently recorded`. The operation `note` names that evidence. Do not treat it as independently recorded.

There are currently no agent-reported-only operations (listed in the [catalog README](../tools/catalog/README.md#agent-reported-only-operations)). Inspect of the ring's gameplayHits (offline, 2026-09-08): `status=observed-partial`, `sides=["server"]`, `support.classes=["readback","agent-reported"]`, `agentReportedOnly=false`, text `[readback, agent-reported; media none; run complete] sides=server`, `applicability=unknown-stamps`. Agent-reported L4 is still **present**; presence did not change `claimable` or `runtimeAvailability`. A `rig-run` combined `framework` stamp is `legacy-combined` and never matches; inspect compares `frameworkSides` on the operation's required sides.

## What the catalog cannot do

These tools search and inspect **maintained recipes**. Searching never launches the game.

- no loading (search `load asset` is not an offered operation)
- no preview (search `preview` is not an offered operation)
- no possession
- no archive mounting
- no runtime availability

A search hit is not availability. `progenitor_catalog_associations` is a name match against the generated inventory (`inferred` at best, or `inventory-unavailable`); it does not load or mount. There is still no in-game asset browser. Styx / NPC control is not in this catalog. Queries that are not recipes (`fireball`, `styx`, `possession`, `load asset`, `preview`) return zero hits.

## For AI agents

1. Call `progenitor_catalog_search` (or `node tools/catalog/recipe.mjs search <query>`). Read `scope`: a hit is not loaded, loadable or safe to play.
2. Call `progenitor_catalog_inspect` on the id you intend to use. If several family members hit, inspect each — Charm is the worked example.
3. Quote `limitations` as printed. Quote `operations.*.status` / the support line / `applicability` as printed.
4. Never promote a status (`observed-partial` and `observed-failure` stay those words; `untested` is not success; `agentReportedOnly` is not independent recording).
5. Never claim current verification or runtime availability from a catalog result: `currentVerification.claimable` is `false` and `runtimeAvailability` is `not-observed-offline`.
6. Follow `requirements.initialization` and the tested `controls` values. Use the `implementation` paths; do not invent a public recolor or Charm-beam API.
7. If search `total` is 0, read `suggestions` (closest recipe ids by word overlap) and inspect those before concluding there is no recipe. Only an empty `suggestions` list means this catalog has no nearby record. Do not reconstruct one from older research notes without new evidence.

Spell and NPC investigation beyond these records: [asset research workflow](asset-research-workflow.md). MCP tool list: [mcp.md](mcp.md#mcp-tools).
