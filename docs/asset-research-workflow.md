# Researching V Rising assets and spell modifications

Start here for spell, effect, NPC, or asset-catalog work. This is a research workflow and a small recipe index, not a completed searchable catalog. Read repository rules and the latest user instructions before any game operation. A previous test session's client authorization does not override the user taking the client back to play.

## Current recipes and verification boundaries

| Recipe | Implementation and evidence | Verified / still open |
|---|---|---|
| Wendigo nova at both dash edges | SampleFrostDash, FrostDashEdgeTracker; payload research; live acceptance 2026-09-07; active-ring unload (internal research notes) | Native ring payload `-1227952025`, independent of a second cast. Recipe `wendigo-frost-nova-ring`: playback/appearance/naturalCleanup/explicitCleanup `observed-success`; gameplayHits `observed-partial` (independently recorded L8 4.0 HP; agent-reported L4 about 9 HP; 0 without rings); poolReuse `untested`. Per-hit attribution unread (HitList not decodable). Unload with a live ring: this recipe's L9 (live-rig `20260907T105917Z-active-ring-unload`, same catalog-cases-5 directory as appearance A9) records an active ring at Sample teardown, ClearRings destroy of that id, and later absence — not a completed-destruction latency. Historical appearance A7/A8 (catalog-cases-4) keep their admitted teardown-command gap. The debug protection is Core's `.debug` now and a Sample reload no longer drops it; read `.debug status` after a Core swap and rearm with `.debug on` if it is not live. |
| Larger nova with green crystals | SampleFrostNovaAppearance; same research and linked clip; live acceptance 2026-09-07; active-ring unload; pool-reuse visual A11 (internal research notes) | Recipe `wendigo-frost-nova-appearance`: radius 12 to 18, unchanged crystal root scale; appearance `observed-success`; poolReuse `observed-success` (subsequent untinted reuse of 10 previously tinted slots after `.froststyle off`, with readback and viewed visual evidence A11, live-rig `20260908T051452Z-pool-reuse-identity`, pool-reuse-visual-2; T=R=10 on attempt 1; report.json `visual.verified=false` (unverified by the rig), viewing verdict in that README; A10 catalog-cases-10 is the earlier readback without capture; A6 122-id comparison is historical; no vanilla Wendigo comparison; Restore() at unload unobserved; the rig does not analyze pixels; claimable false offline). Pale mist was not claimed to become green. gameplayHits `untested`. Active-ring unload `observed-success` (live-rig `20260907T105917Z-active-ring-unload`; Sample ClearRings destroyed a ring that was alive at teardown; later exists false; not a completed-destruction latency. Historical catalog-cases-4 A7/A8 recorded the same disposal with an admitted teardown-command gap). |
| Charm particles | corrected sequence research; live acceptance 2026-09-07; tracked unload; client Sample swaps B6 (internal research notes) | Target `charm-target-channel-sequence` (`-541113174`): appearance `observed-partial` (red crown/swirl particles at the player; no beam to the second endpoint). Caster `charm-caster-link-sequence` (`-389499105`): appearance `observed-failure` (draws nothing). Borrow `charm-loop-particle-borrow`: appearance `observed-success` of an owned `Part_Charm_Loop_01` copy; poolReuse `untested`; tracked Charm unload while active `observed-success` (Removal: live-rig `20260907T093055Z-charm-tracked-unload`; client Sample Swap: live-rig `20260908T045301Z-swap-client`, B6, three cycles, connection retained). Client carriers outlive the server's disposal and need an explicit client-side destroy (B6 destroyed leftover NetworkedSequence replicas at cleanup as manual recovery, not automatic cleanup). A null-owner static `Assets.Borrow` is not swept at unload. |
| Lord Styx control | PlayerForms and Bootstrap ControlFormService; [form results](player-forms.md) | Appearance, timed return, one active-disconnect path and one active-save barrier observed on disposable data. Normal Burst-enabled melee aborts. Public qualification remains empty. Diagnostic Burst-disabled success is not qualification. |

These findings are narrower than universal APIs. In particular, the radius/color implementation is currently a specific Sample consumer; it does not establish a generic safe recolor operation for every game effect. Existing AbilityCast is useful, but the successful two-edge nova solution uses the effect payload directly.

## Investigation sequence

1. **Define the requested behavior and evidence.** Separate visuals, cast/animation timing, radius, collision/damage, attribution, replication, and cleanup. Do not use one successful observation to certify the others.
2. **Find exact identities and relationships.** Search installed-build archive metadata and ECS prefab data. Follow ability group → cast/activation → payload or target buff → sequence → particle entity → renderer → material/shader. Names and loaded-object scans suggest candidates; neither proves identity or complete coverage. Preserve all sequence entries and conditions.
3. **Read native behavior when stubs cannot answer.** Interop stubs provide signatures and fields, not method bodies. Resolve the matching side/build in the game's metadata, then inspect the exact native overload in the game's native code. Record signature, build, call ordering and evidence. Burst/IL2CPP compilation does not establish that authored payload data or presentation cannot be modified.
4. **Choose the right layer.** Use a real ability cast when the full cast is desired, its ownership requirements are met, and the character can cast. Inspect a native payload when the requested effect must occur during an existing action. The dash's one-cast restriction did not prevent spawning the Wendigo ring. A proxy NPC introduces separate lifetime/ownership hazards and was unnecessary for this solved recipe.
5. **Follow native initialization.** For this ring, the verified `GameManager_Shared.InstantiateEntityImmediate(EntityManager, Entity creator, Entity prefab)` overload propagates relevant ownership/team data. Its component checks are conditional: do not generalize it to complete initialization of every prefab. SpawnTag plus initial zero health/Disabled can mean initialization is pending.
6. **Make narrow instance changes.** The ring needed its instance GetTranslationOnSpawn removed to retain departure position. That is not a general component-removal recipe: removing Styx's networked BloodConsumeSource violated its snapshot layout. Inspect each component's consumers and network/save role first. Change radius separately from geometry size; trace actual shader properties rather than assuming one generic tint controls everything.
7. **Respect presentation ownership and pooling.** Match the exact ring, sequence, particle entity/version and pooled object identity. Never recolor global shared materials to customize one spell. Restore per-instance property overrides; retain the visible particle tail after sequence emission ends. Verify the next vanilla reuse separately.
8. **Use measured acceptance tests.** A Cast return value proves a request, not execution; require the cast-started counter for a forced cast. A directly spawned payload need not increment it. Verify position/radius readback, actual screenshots or clips, target hits, and cleanup separately. Inspect both Unity and BepInEx logs. Compare normal runtime settings; never promote a diagnostic engine-mode result to a normal pass.
9. **Keep tests recoverable.** Use isolated saves, fresh player/User identities, rearmed debug protection, bounded lifetimes, and cleanup readbacks. Test off/unload, disconnect, save/restart and pooled reuse where applicable. No live-server experiments. Focus-free bridge and [capture tools](observe-capture.md) avoid sending input into a menu or another application.
10. **Publish what the next agent needs.** Record exact IDs, build fingerprint, relationship provenance, executable recipe location, required context, supported controls, observed failures, evidence paths and verification scope. Correct stale summaries and put a supersession link at the top of historical failed approaches.

## Offline recipe catalog

The Wendigo and Charm findings above are five maintained recipes under
[`tools/catalog/recipes/`](../tools/catalog/recipes/SCHEMA.md). Search then inspect them offline;
do not reconstruct the research.

- Walkthrough (green nova, Charm particles vs beam, how to read inspect): [catalog usage](catalog-usage.md)
- Commands and inspect-field reference: [catalog README](../tools/catalog/README.md#recipe-layer-maintained-usage-recipes)
- Runnable examples: [`tools/catalog/examples/`](../tools/catalog/examples/README.md)

MCP tools `progenitor_catalog_search`, `progenitor_catalog_inspect`, `progenitor_catalog_associations`
and `progenitor_catalog_list` ([mcp.md](mcp.md#mcp-tools)) call the same library as
`node tools/catalog/recipe.mjs`. They are **source-checked** and offline-tested; they never start
the game. A search hit is not availability. Inspect always reports `currentVerification.claimable=false`
and `runtimeAvailability=not-observed-offline`. Quote per-operation status from inspect; do not
promote it.

## Catalog limits

The [offline indexer](../tools/catalog/README.md) inventories shipped archives and names. Its
generated inventory does not certify playback, gameplay, customization or safe teardown. The
recipe layer associates recipes with inventory rows by name only (`inferred`, never `known`); the
Wendigo ring's presentation objects have no inventory association at all. Still out of scope:
general archive loading, possession, archive mounting, runtime availability, an in-game asset browser,
and Styx/NPC work. Searching never launches the game.

For mesh/skinned presentation models, the separate [model preview](model-preview.md) path uses
the existing model-section loader. It is implemented with live qualification pending; it does not
establish arbitrary archive loading or reconstruct gameplay-driven effects.

The complete implementation brief is `<local-evidence>\Progenitor-asset-catalog-agent-handoff.md` (kept outside the repository). Build on that brief and M5 rather than creating another competing inventory. Import these recipes with their actual evidence scopes, including the failed Styx melee case; do not mark every discovered asset usable.

Additional history (AI friction log, ability-cast investigation, archive catalog research) is in the internal research notes. Later corrections take precedence over earlier experimental conclusions.
