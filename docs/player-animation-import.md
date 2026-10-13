# Player animation reference for character imports

**Status: under development and incomplete.** This experimental asset-import workflow has only scoped example verification. It is not a complete or production-ready third-party armor, character or creature pipeline; the limitations below still apply.

Import an original animated GLB to play its own authored rig and clips as a client visual. `ImportGlb` and schema-2 animated packages support skins with up to four joint influences per vertex, inverse bind matrices and node translation/rotation/scale channels using LINEAR or STEP interpolation. Morph targets, CUBICSPLINE interpolation and additional influence sets are refused. This visual playback does not retarget native player animation, cast abilities, create gameplay entities or synchronize across clients.

**Verification:** compile and offline checks passed, including 113 focused tests. The public Core import API passed scoped in-game skinning, playback, crossfade, rest-pose and cleanup checks using the original two-joint banner. The example controls and animated-package import also passed in-game; arbitrary rigs remain unqualified. The [animated model example](../samples/Progenitor.AnimatedModelExample/README.md) supplies the runnable plugin and acceptance procedure; [asset build](../tools/asset-build/README.md) supplies Blender export and package commands.

## Play an authored clip

Inside a client plugin callback on the main thread, using its context `ctx`:

```csharp
var model = ctx.Models.ImportGlb(@"C:\Assets\animated-banner.glb");
model.Root.transform.position = new UnityEngine.Vector3(0, 0, 0); // Choose a visible world position.
var animation = model.Animations;
animation.Play("Breeze", loop: true, speed: 1, blendSeconds: 0);
animation.Pause();
animation.Seek(0.5f);
animation.Resume();
animation.Play("Gust", blendSeconds: 0.25f);
animation.Stop(); // Restore the GLB's authored rest transforms.
model.Dispose(); // Release the visual, playback callback and generated resources.
```

Import is context-owned, so plugin unload also releases the model. The snippet demonstrates calls in sequence; use the example's registered controls to inspect each state separately. `ClipNames`, `CurrentClip`, `IsPlaying`, `TimeSeconds` and `Duration` expose playback state. `Play` starts at zero, requires positive finite speed and nonnegative finite blend seconds, and optionally blends from the currently displayed pose. A non-looping clip holds its last pose. `Seek` clamps to the current clip's duration and preserves paused/playing state. `Stop` clears the selected clip; call `Play` before `Resume` or `Seek`. Disposing `model.Animations` releases its playback callback; disposing the model releases all visual resources.

## Native player compatibility reference

The following reference separates **animation names found in the installed assets**, **source-checked engine contracts**, and **observed playback**. The name inventory is a candidate map. Offline metadata resolves the standard player prefabs to their shared collection, body-specific weapon overrides and avatars; controller/state and clip-timing evidence is linked below. These reports do not establish playable imported-character support.

## Choose the intended compatibility

- **New appearance on the existing player rig:** fit and skin original geometry to the captured rest skeleton; preserve the native animation controller, avatar, bones and sockets. The current garment workflow supports matching rig captures, not arbitrary full-character replacement. Reusing the rig is the first path to qualify; it avoids asking an author to recreate every player action.
- **Different skeleton with native motion:** requires a validated bone/rest-pose mapping, retargeting and attachment/IK support. Matching bone names alone is insufficient. Automatic character retargeting is not implemented.
- **Custom player animation set:** requires logical-action mappings, layers, blend/exit rules, weapon overrides and declared missing-action policy. Idle/walk/attack/death alone does not establish player compatibility. Native player registration of such a set is planned; own-rig visual clips are supported above.

Use the [rig reference workflow](../tools/rig-reference/README.md) for body and bone captures. Capture each body type independently; a missing male/female clip name does not prove that the other body's animation is shared or absent. Animal/transformation forms and rider/mount skeletons need their own coverage.

## Animation coverage map

Names below are examples recovered from asset metadata. Their action meanings are inferred from names; playback and controller membership remain unverified. Cover all phases, directions, weapon configurations and interruptions that the resolved controller actually uses.

| Family | Coverage to map and test | Example metadata name |
|---|---|---|
| Locomotion | Idle, ready, forward/backward, strafe/diagonals, stationary motion, stopping and turning | `VampireFemale_Unarmed_Run_Forward`, `VampireMale_1H_ReadyToIdle` |
| Equip and weapon stances | Idle/ready/running equip; each weapon's overrides and ready pose | `VampireMale_2H_EquipRunning_Long` |
| Melee/ranged attacks | Combo steps, standing versus moving casts, charge/hold/repeat, weapon skills | `VampireFemale_Axes_Combo03_MoveCast_Exec`, `VampireMale_2WPistol_RapidShot_Loop` |
| Spells/channeling | Cast preparation, execution, channel loops, release, block; upper-body and full-body cases | `VampireFemale_ModSpell_DIR_ChestBeam_Loop` |
| Movement abilities | Dash, leap, travel and landing; interrupted movement and presentation effects | `VampireMale_Dash04_ExecPost`, `VampireMale_Leap05_Landing` |
| Hits and control loss | Directional hits, knockback, stunned, impaled, wounded poses | `VampireFemale_Unarmed_Stunned`, `VampireFemale_Impaled_Loop` |
| Incapacitation and lifecycle | Lying/downed transitions, death, coffin sleep and respawn | `VampireFemale_Incapacitated_Lying_Post`, `VampireFemale_CoffinRespawn_Reg` |
| Feeding and consumables | Feed channel/release, expose vein, eat/drink, mounted variants | `VampireFemale_FeedChanneling_Exec`, `VampireMale_ExposeVein_GiveMale_Reg` |
| Interaction and construction | Hand interactions, conversation, building/channeling, fishing, gate use | `VampireFemale_ConstructionChannel_Loop`, `VampireMale_Fishingpole_FishingThrow_Exec` |
| Social and furniture | Emotes and their phases, chairs, sofa and throne transitions | `VampireMale_Emote_Salute_Loop`, `VampireFemale_Furniture_ChairStandUp01_Reg` |
| Mounted | Mount/dismount, rider gait/ready direction, mounted attacks and interactions | `VampireFemale_Unarmed_MountLeft_Reg`, `VampireMale_MountedAxes_SwingRight_Exec` |
| Procedural/additive | Aim, lean, turn, head/chest/arm IK, foot locking, randomized alternatives | Engine contract fields; not a standalone clip checklist |
| Transformation/customization | Enter/exit forms, model/controller changes, customization presentation | `VampireFemale_Unarmed_FlyTransform01_Reg`, customization controllers |

Retain suffixes such as `Init`, `Antip`, `Exec`, `Loop`, `Hold`, `Travel`, `Landing`, `Post`, `ExecPost`, `MovingPost`, `Repeat` and `Reg` verbatim. They are authored labels, not a universal state machine. Do not invent missing phases, assume a `Loop` clip's actual loop setting, or derive damage timing from `Exec`.

## Engine mapping fields to resolve

The local interop declarations expose the following structure. The decoded standard-player profile below supplies authored values for this installed-build snapshot. Detailed source references and build scope are in the internal research notes.

| Layer | Required mapping data |
|---|---|
| `AnimationCollection` | Required avatar; directional hit reactions; locomotion settings; controllers/layers; additive, randomized and advanced animation entries |
| Locomotion settings | Walk/run/idle/ready states and poses; velocities; transition times; override entries |
| Advanced animation | Logical name; single/multi/blend/locked clip structure, thresholds and parameters; speeds; override and mounted IK flags |
| `HybridModelAnimationRemappingAsset` | Source/destination collection, source/destination logical names and layer |
| Weapon component | Actual controller-array entries and equipped index; **do not assume the index equals WeaponType** |
| Playback | Logical name/hash, speed parameter, duration, priority, blend-in/out, movement exit and chest IK |
| Rig/procedural | Avatar, complete hierarchy/rest matrices, skin palettes, attachment sockets, aim/foot/mounted IK and locomotion parameter copying |

Source enum labels are `BaseLayer`, `StrafeLayer`, `ReadyLayer`, `MainUpperBodyLayer`, `MainLowerBodyLayer`, `SecondaryUpperBodyLayer`, `SecondaryLowerBodyLayer`. The standard controller's authored layer names are listed below; they differ from these enum labels. The separate gameplay types `FullBody` and `UpperBody` are not interchangeable with those controller layers.

Source weapon categories are None, Spear, Sword, Scythe, Crossbow, Mace, Slashers, Axes, FishingPole, Rapier, Pistols, GreatSword, Longbow, Whip, Daggers, Claws and Twinblades. This is an engine enum inventory, not a claim that every category has a distinct active player controller in this build.

## Generate the candidate map

Run from the repository root. These commands read the local installation offline; they do not operate a client or server. Keep generated metadata outside the repository.

```powershell
New-Item -ItemType Directory -Force C:\Assets\player-reference\catalog | Out-Null
node tools/catalog/index-catalog.mjs --deep --inventory --verify --inventory-cap 268435456 --out C:\Assets\player-reference\catalog
node tools/catalog/player-animations.mjs --inventory C:\Assets\player-reference\catalog --out C:\Assets\player-reference\player-animations.json
```

The report preserves catalog fingerprint, exact archive/object identities and duplicate names. Name-based groups help find candidates; they do not establish shared clips, playable states or chronological transitions. Non-player-named animations remain available for unresolved/shared references. Check unresolved names and truncated archives: raising the read budget does not prove that all names or bindings were recovered. Never turn “not found by this filter” into “not required.”

## Completion gates for a playable imported character

1. Refresh the decoded standard-player profile after updates. Extend to alternate player forms/listeners and resolve special selector destinations; retain exact identities, build stamp and unresolved references. Standard male/female prefab-to-collection/controller links are now resolved offline.
2. Use decoded clip ranges, sample rates, loop/root flags and serialized events as authoring metadata. Controller conditions and interruption fields are available offline. Qualify effective playback speed, root-motion application, event execution and ability/damage timing separately; stored clip duration is not cast duration.
3. Export a local rig profile containing hierarchy, avatar requirements, rest transforms and weapon/FX/IK sockets. Identify deform bones separately from controls and helpers. Keep game geometry/clips out of distributable original-asset packages.
4. Produce an authoring mapping from each required logical action to native reuse, explicit retarget or supplied original clip. Missing mappings must remain reported; no silent idle fallback. This mapping format and its runtime consumer are not yet implemented.
5. In an available isolated test client, capture the coverage families above on both body types and every supported weapon. Include movement while casting, cancel/interruption, equip swaps, mounted play, transformation return, Veil of Shadow, reconnect and reload. Check seams, grip, feet, attachment visibility, root motion and cleanup separately from gameplay hits.
6. Mark each mapping as metadata-only, source-resolved or observed-live. Approve only the tested character/profile subset. Re-run fingerprint and mapping checks after a game update.

Playable-character qualification still requires effective native playback and interruption testing, alternate-form coverage and runtime remapping. Own-rig GLB visual playback does not satisfy these player integration gates.

## Repeatable gameplay capture

Use an available isolated client and the [keyboard and mouse bridge controls](bridge-ops-client.md#key). A dedicated MCP wrapper is not required. Check `map` status for an unobstructed world and `mouse` status for screen dimensions and chat state. Choose a world coordinate away from UI; mouse coordinates start at the bottom left. Check the current native binding before testing an ability; do not assume a default key still applies.

Run from the repository root with an animation observer already recording. This example holds the primary mouse button for a combo, then moves briefly. Replace the coordinates with a verified point on the current client's world view.

```powershell
$inputRequest = Join-Path $env:TEMP 'progenitor-animation-input.json'
$utf8 = [Text.UTF8Encoding]::new($false)
try {
    [IO.File]::WriteAllText($inputRequest, '{"action":"click","x":2800,"y":850,"ms":3500,"background":true}', $utf8)
    node tools/mcp/call.mjs client mouse "@$inputRequest"
    if ($LASTEXITCODE -ne 0) { throw 'Attack input failed' }
    [IO.File]::WriteAllText($inputRequest, '{"action":"hold","keys":"W","ms":300,"background":true}', $utf8)
    node tools/mcp/call.mjs client key "@$inputRequest"
    if ($LASTEXITCODE -ne 0) { throw 'Movement input failed' }
} finally {
    [IO.File]::WriteAllText($inputRequest, '{"action":"release"}', $utf8)
    node tools/mcp/call.mjs client key "@$inputRequest"
    node tools/mcp/call.mjs client mouse "@$inputRequest"
}
```

Inspect `ok`, `pressReached`/`positionReached` and `reached`, then correlate the recorder's Animator state hashes, controller, avatar, layer and weight with the decoded profile. A held click can repeat attacks; sampled states do not establish exact phase duration or damage events. Keep captures outside the repository. Remove the observer after testing, confirm clean unload and follow the coordinated client cleanup in [CLAUDE.md](../CLAUDE.md).

Generate a report from the linked research recorder's JSON and decoded controller-state map:

```powershell
node tools/catalog/player-animation-observations.mjs --states C:\Assets\player-reference\controller-state-map.json --capture C:\Assets\player-reference\capture.json --out C:\Assets\player-reference\observations.json
```

The output must not exist. The tool records input byte hashes, separates root/Animator instance/controller/avatar profiles and retains every candidate for an ambiguous state hash. It reports unknown hashes and rows without usable controller/avatar references. Inactive rows and zero-weight overlay layers do not qualify states; layer zero is retained. Counts mean sampled observations, not duration, complete coverage or proof that the named clip played. The tool consumes the research recorder/state-map formats; it neither records the game itself nor accepts arbitrary animation exports.

**Verified live, scoped:** the unfocused-client procedure produced all three `1H_Swing01/02/03_Exec` states and `TwistLocomotion` in the sampled female Animator using `VampireFemale_1H_AnimatorOverrideController`. A separate user-driven capture observed `GeneralDash_Weapon`. The simultaneously observed male Animator remained idle. These observations do not qualify male combat, all weapons, every transition, garment clearance or imported characters.

## Decoded standard-player profile

**Verification: decoded asset metadata only; no live playback in this batch.** Reproducible sources, fingerprints and complete tables are in the internal research notes. Start authoring with the 751-entry logical-action table, not the broader candidate-name count. Some authored entries contain TEST labels; membership does not prove runtime use.

- Both standard body prefabs point to the same `VampireMale_AnimationsCollection`: 751 entries, comprising 633 Single and 118 Blend entries with 901 member references to 893 distinct base clips.
- Each body exposes 17 indexed weapon-controller slots. The combined slots reference 26 distinct override controllers. Preserve indices and exact object identities; display names do not prove the native enum-index selection mechanism.
- The collection's `RequiredAvatar` and male Animator use `VampireMale_Standard_LOD00_rigAvatar`. The female Animator uses **`VampireFemale_Standard_LOD00_rigAvatar`**. A shared collection does not mean interchangeable bind poses or identical avatar requirements.
- The main base controller uses `Locomotion Full Body` (9 states), `Locomotion Upper Body` (9), `Fullbody` (752), `Upper Body` (752), and `Additive` (1). The action layers contain the 751 logical names plus a null state. 819 serialized parameter entries include per-action speed parameters; they are not 819 independent user controls.
- Collection and base-controller clip sets agree for 747 actions in each action layer. `2HLongbow_Shoot01_Init`, `_Antip`, `_Exec`, `_Post` list an additional moving-cast clip in the collection, absent from the corresponding base-controller state. This discrepancy is unresolved; preserve both rather than choosing one automatically.
- All 2,293 distinct non-null override-table clip targets have decoded timing metadata. Null replacement pointers are retained with **unproven fallback** semantics. Neither missing nor null mappings are silently converted to idle or assumed to reuse the original.
- Both prefab Animator assets serialize `m_ApplyRootMotion=false`; this does not establish which runtime systems move the character or whether the setting changes during play.

The authoring template (kept with the internal research notes) lists all 751 logical entries with unassigned choices. It is a planning artifact, **not a runtime package schema**. Native rig reuse, retargeting and original clips must be chosen against the intended body/controller profile and verified.

## Rebuild resolved bindings from decoded metadata

`player-animation-bindings.mjs` consumes local type-tree decode JSON, not raw game archives. Each input records `archiveId`, 1-based `externals`, and `decoded` object bodies with exact `size`/`consumed`, `classID`, `pathID` and `value`. Reject partial decodes. The current extraction scripts and decode snapshots live in the linked research folder; arbitrary export schemas are not supported. Input-file hashes preserve the exact bytes used, but the tool cannot independently prove those decoded files came from the inventory's game build. Refresh them together.

Create a manifest beside the decoded files:

```json
{
  "schemaVersion": 1,
  "inventoryDirectory": "catalog",
  "collection": "collection.json",
  "prefabs": {"male": "male-prefab.json", "female": "female-prefab.json"},
  "overrides": ["male-overrides.json", "female-overrides.json"]
}
```

```powershell
node tools/catalog/player-animation-bindings.mjs --input C:\Assets\player-reference\binding-input.json --out C:\Assets\player-reference\player-bindings.json
```

Paths in the manifest are relative to it. Output must not exist. The report preserves indexed weapon slots, logical members, raw blend/IK fields, base-controller references, original/replacement clip pairs, avatars, Animator settings and unresolved targets. `bindingResolution: complete` means these extracted references resolved; it does **not** mean all runtime branches, controller internals, fallback semantics or character-import compatibility are qualified. Controller state-machine/transition extraction and clip timing are separate evidence reports for now.
