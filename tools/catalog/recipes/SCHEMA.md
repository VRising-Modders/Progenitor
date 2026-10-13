# Recipe schema `progenitor.catalog.recipe/1`

One JSON file per recipe in this directory. `../lib/recipes.mjs` (`validateRecipe` /
`validateRecipeDirectory`) enforces everything below; `node ../recipe.mjs validate` runs the
directory-level check. Bump the schema string when a field changes meaning; add fields without
bumping when old files stay valid.

A recipe records **what was actually observed about one operation on one asset composition**.
It is not an inventory row (the generated inventory is `../catalog-inventory.json`, produced by
`index-catalog.mjs` and never edited by hand) and it does not say the asset is loaded, loadable
or safe to play now.

## Top level

| Field | Type | Rule |
|---|---|---|
| `schema` | string | exactly `progenitor.catalog.recipe/1` |
| `id` | string | kebab-case, unique across the directory, stable forever |
| `revision` | int >= 1 | bump on any content change |
| `title` | string | one line |
| `family` | `{id, title}` | kebab-case family id; recipes of one spell share it |
| `kind` | enum | `gameplay-payload` (spawns something with gameplay effects), `visual-sequence`, `isolated-particle`, `visual-instance-override`, `full-ability-cast` |
| `aliases` | string[] | search terms; a display name is never an identity |
| `summary` | string | what it does and what is not claimed |
| `captured` | string | `YYYY-MM-DD` of the newest evidence, or `unknown` |
| `identities` | array | see below; non-empty |
| `assetReferences` | array | names read live or from source, with `unityType` when known |
| `relationships` | array | typed edges with status and provenance |
| `requirements` | object | `side` (`server`/`client`/`both`), `world`, `context` (object), `initialization` (ordered string[]), `cleanup` (string[]) |
| `controls` | array | `{name, tested: [{value, evidence[], ...}], limitations, implementation?}` |
| `operations` | object | one entry per operation key (below) |
| `evidence` | array | one entry per artifact |
| `implementation` | array | `{path, symbol?, status}`; `status` must start with a documentation label: `source-checked`, `compile-verified`, `observed live`, or `acceptance procedure not run`, plus date and image hash when recorded |
| `limitations` | string[] | what is not covered or not claimed |
| `verified` | forbidden | an asset-wide flag is refused by the validator |

## Identities

`{ ref, role, domain, value, name?, provenance: {source, method?} }`

* `ref` is a kebab-case key local to the recipe, used by relationships.
* `domain` is one of `prefab-guid`, `sequence-guid` (signed int32 decimal **as a string**),
  `archive` (32 hex), `archive-object` (`<32 hex>/<pathID>`, pathID a signed int64 string),
  `unity-name` (an `m_Name`; never unique on its own).
* `value` is always a string: sign and 64-bit precision survive JSON and JavaScript.
* A runtime `Entity(index:version)` is refused as a domain. It belongs in evidence summaries.
* A sequence hash lives in the same hash space as a prefab GUID but is kept in its own domain
  because it is resolved through the client-only sequence prefab map.

## Relationships

`{ from, to, role, status, provenance: {source, method} }`

* `from`/`to` are an identity `ref`, `asset:<name>` (must exist in `assetReferences`), or
  `external:<free text>` for an endpoint that has no catalog identity yet.
* `status`: `known` (both endpoints identified and the link observed; needs method `live-log`,
  `live-readback`, `native-analyze`, `source-inspection` or `dump`), `inferred` (the link is
  deduced, or observed towards an endpoint without a catalog identity; any method, typically
  `name-match`, `report` or `reasoning`), `unresolved` (any method; say what would resolve it).
* An archive object can never be `known` by `name-match`, and an `external:` endpoint can never
  be `known`. A gameplay GUID is therefore never silently equated to an archive object.

## Operations

Keys, all required: `discovery`, `playback`, `appearance`, `controls`, `gameplayHits`,
`naturalCleanup`, `explicitCleanup`, `poolReuse`.

Each: `{ status, evidence: [evidence ids], note, sides? }` with status one of `observed-success`,
`observed-partial`, `observed-failure`, `inferred` (source-inspected or reasoned, not observed),
`untested`, `not-applicable`. An `observed-*` status needs at least one evidence id.

`sides` is `["client"]`, `["server"]`, or `["server","client"]`: the process images the
observation depends on. Inspect uses it when comparing a cited `rig-run` against
`currentBuild.frameworkSides`. Absent `sides` defaults conservatively to
`["server","client"]`. Client presentation (appearance, pool identity, Charm particles)
requires the client images; a server-driven procedure may also require the server.
Ring gameplay hits and unload readiness require the server. Equality on a side the
operation did not declare is ignored.

## Evidence

`{ id, kind, date, summary, path?, build, stampNote?, observer?, step? }`

* `kind`: `log`, `screenshot`, `clip`, `source-inspection`, `native-analyze`, `report`,
  `readback-file`, `rig-run`. Old files without `rig-run` stay valid.
* `date`: `YYYY-MM-DD` or `null` when unknown.
* `path`: repo-relative (resolved against the Progenitor root; a path starting with `../`, such
  as `../evidence/...`, resolves against the main checkout so linked worktrees find the
  same sibling folder), `<local-evidence>/...` (resolved under `PROGENITOR_LOCAL_EVIDENCE`,
  otherwise reported missing), or absolute. Do not record a machine-specific absolute path.
  Inspect reports whether the file is present **separately** from the claim.
  For `rig-run`, `path` is required and is the **run directory** (the folder that holds
  `report.json`), not `trace.jsonl`. Inspect refuses a path that is not a directory and
  refuses a symlinked directory (`lstat`). It reads only `report.json` (capped at 4 MB) and,
  when `step` is set, a bounded `trace.jsonl` excerpt.
* `step`: optional, `rig-run` only. When set, inspect attaches a bounded trace excerpt whose
  lines mention that step name. Trace is never read in full.
* `build`: `{ contentCatalogSha256, gameIdentity, framework, frameworkSides? }`. The first
  three are each a string or `null`. Null means the recording did not stamp it and it stays
  unknown; the current source hash is never back-filled into old evidence. `stampNote` may
  carry circumstantial context.
* Kind `rig-run` also carries `build.frameworkSides`: `{ server, client }`, each
  `generation:<n>; core:<sha>; dev:<sha>; sample:<sha>` from that side's own ping only
  (`null` tokens allowed), or `null` when that side was not recorded. Recovered only from
  the saved report/ping (`report.json` `images` / `trace.jsonl` ping `result.images[]`);
  never inferred from launch notes or today's binaries. The combined `build.framework`
  string on a `rig-run` is **legacy-combined** (server-first, possibly hybrid): inspect
  reads it as `stamps.framework.state = legacy-combined` and it never matches a current
  build. Do not copy a missing client hash from the server, or assemble a hybrid from
  both sides.
* A `rig-run` is also compared against the run report's top-level stamps
  (`contentCatalogSha256`, `gameIdentity`, `frameworkSides`) when present. New reports
  carry `frameworkSides` and `framework: null`; `stampErrors[]` may name sides;
  `stampPhases: {start, final}` and `stampTransition` record a side that changed during
  the run (that side stays unknown for the claim). Legacy reports carry only the combined
  `framework` string plus per-side `images`. An operation's applicability compares **only
  its required `sides`** against `currentBuild.frameworkSides`. A required side whose
  recorded or current string is null/insufficient is unknown; a changed hash on any
  required side is stale. A framework stamp that names only a generation (`generation:4`),
  or whose core/dev/sample parts are the token `null`, is insufficient and not comparable:
  it never matches a current build. Only stamps that carry image hashes compare, and they
  match only when generation and every recorded hash (core, dev, sample where present on
  both sides of **that process**) are equal. A hash present on one side and missing on
  the other is not a match. A null or unparseable stamp never matches. CLI, inspect and
  MCP share one comparison function (`frameworkSidesCompare` in `lib/rigRuns.mjs`).

## What inspect adds (not stored)

* `evidence[].media.present`, `implementation[].file.present`: file presence now.
* `evidence[].provenanceClass`: `log` (saved log present), `media` (screenshot/clip present),
  `readback` (rig report or readback file present), `agent-reported` (kind `report`, a path
  ending `.md`, or basename `bridge-responses.md`), `source`
  (source-inspection / native-analyze), `missing` (path given but the file or run is
  absent). A markdown report is never `source`. A present file is **not** a current verification.
* `operations[].mediaPresent`: `true` only when at least one screenshot/clip file is present;
  `false` when a cited media path is missing; `null` when the operation cites no media.
* `evidence[].run` (kind `rig-run` only): `{found, status, session, images, finalChecks,
  steps, cleanup, contentCatalogSha256, gameIdentity, gameIdentitySides, framework,
  frameworkSides, stampErrors, stampPhases, stampTransition, agentReported:false}` from
  `report.json`, or `{found:false, reason}`. Top-level stamp fields are the report's own
  strings (`null` when absent). New reports set `framework: null` and fill
  `frameworkSides`. `stampErrors` is the report's list (empty on older runs) so a null
  stamp can show why. `agentReported` is always false: a rig report is a saved readback,
  not an agent note.
* `evidence[].stamps.*.state`: `matches`, `changed`, `unknown`, `not-checkable-offline`, per
  stamp against the current view (`contentCatalogSha256` from `catalog-summary.json`; the game
  identity and framework build are not observable offline and stay `null`). For `rig-run`,
  `stamps.framework.state` is `legacy-combined` (never a match). `stamps.frameworkSides` is
  `{server, client}` each `{evidence, current, state}` against `currentBuild.frameworkSides`.
* `operations[].sides`: the declared (or default `["server","client"]`) process images.
* `evidence[].applicability` and `operations[].applicability`: `stale` when any known stamp
  changed (recipe `build` **or**, for `rig-run`, the run's `frameworkSides` on the
  operation's required sides), `matches-checkable-stamps` only when every stamp is known and
  matches, else `unknown-stamps`; `no-evidence` for an operation without evidence. A run
  recorded on a different **required** side is `stale` and cannot appear currently verified.
  A changed client-only Core/Dev/Sample invalidates a client or paired claim; a changed
  server-only image invalidates a server claim; an irrelevant-side change for an explicitly
  single-side operation still matches. A run whose required-side stamp is insufficient
  (generation-only, or null hash parts) is `unknown-stamps` even when the catalog hash
  matches; it is never `matches-checkable-stamps`. A null, unparseable, or legacy-combined
  stamp is never a match. Offline `currentBuild.frameworkSides` is `{server:null, client:null}`
  so live-stamped evidence stays `unknown-stamps` and `currentVerification.claimable` is
  false. Search/inspect never contact the game.
* `operations[].support`: `{classes, strongest, missing, agentReportedOnly, runsNotComplete, runsComplete, runsOtherBuild}`.
  `strongest` is `none` when the operation is `untested` / `not-applicable` or has no
  counting evidence. A `rig-run` whose `status` is not `complete` never counts as support
  (including for `observed-success`) and is listed in `runsNotComplete`; inspect flags
  `run not complete` on any operation that cites it. `runsComplete` lists cited runs
  whose status is `complete` on a matching build. A complete run whose recorded stamps
  differ from the current view or the recipe's build stamps is `runsOtherBuild` and cannot
  raise `strongest` above `source` (framework stamps match only when they carry image hashes
  and generation plus every overlapping hash are equal; a generation-only or null-parts
  stamp never matches). A present `readback-file` is class `readback` but is not a
  rig run.
* `inventory.state`: `indexed` | `inventory-unavailable` | `not-loaded`. `inventory.note` is
  always `index presence only; runtime availability is not observed offline`. This is not
  runtime availability.
* `buildSummary`: `{stamped, unstamped, stale, matchesCatalog}` over the recipe's evidence.
* `inventoryAssociations[]`: for each asset reference, `inferred` (unique name+type match in the
  inventory), `ambiguous` (several distinct objects), `unresolved` (no named row), or
  `inventory-unavailable`. Always method `name-match`; always candidates, never a selection.
* `currentVerification.claimable` is always `false` offline, and `runtimeAvailability.state` is
  always `not-observed-offline`. Presence of an evidence file or a complete run does not flip
  that flag.

## Validation

`validate` and query loading (`list`, `search`, `inspect`, `associations`) share one
directory-level check: per-file schema, unreadable JSON, and duplicate ids across files (each
duplicate names both files). A missing directory is a `recipes-missing` problem. Loading throws
when that check reports any problem, so the two paths cannot disagree.

`validateRecipe` is total over JSON values. Missing, null, wrong-type and nested-wrong-type
fields become problem strings; the function does not throw. An unexpected exception inside the
validator is reported as a single `validator error: <message>` problem.
