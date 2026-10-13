# Blender garment authoring

Import a locally captured Progenitor rig, fit original clothing, check captured poses, and export a garment for `WearableGraphics.FromAuthoredJson`. Requires Blender 4.5 and schema-version-1 captures. The Blender tools run offline and never contact the game. Captured `Body_GEO` and original four-piece robe import are **verified live** within 0.44 mm at the sampled pose. `Body_GEO` excludes the separately rendered head and face, so that measurement is not a complete anatomical body reference. An original chest exported through Blender also loaded in game and reimported within 0.35 mm. Fitting/export have offline checks; combat coverage remains unqualified.

## Repeatable outfit project

After importing a reference and preparing original garments, save the local authoring scene and an `outfit.json` beside it. Paths are relative to the project file; object names must match the scene. This example preserves the robe shape and transfers weights only in an explicitly painted loose-panel group:

```json
{
  "schemaVersion": 1,
  "scene": "source.blend",
  "reference": "captures/reference.json",
  "body": "Body_GEO",
  "poses": ["captures/reference.json", "captures/attack-three.json"],
  "garments": [
    {"id": "chest", "object": "Original robe"},
    {"id": "cape", "object": "Original cape", "fit": {
      "weightsOnly": true, "weightRegion": "WeightRegion", "maxDistanceMm": 300
    }}
  ]
}
```

For body fitting, replace the `fit` object with `{"region":"FitRegion","clearanceMm":6,"maxDistanceMm":50}`. Optional `weightRegion` and `bodyRegion` select separate weight-transfer and reference-surface masks. Omitting `fit` preserves the authored geometry and weights. The pipeline does not design new geometry or choose fitting regions for you.

From the repository root, run one offline build into a new directory:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/build_outfit.py -- 'C:\outfits\my-robe\outfit.json' --out 'C:\outfits\my-robe\build-v1'
```

The builder validates each original garment, optionally fits selected regions, checks every captured pose and exports its JSON. It refuses unknown settings, invalid paths/IDs, reference-only garments and existing output directories. A failed build does not publish a partial output directory.

- `package/garments/`: only exported original garment JSON; load each through `WearableGraphics.FromAuthoredJson` in the matching item registration.
- `package/manifest.json`: garment IDs, relative files and checksums. This is a packaging index, not automatic item registration.
- `reports/`: fitting and pose diagnostics for each garment.
- `authoring.blend`: editable local scene, including body references; keep it outside distributed packages.
- `outfit.json`: resumable manifest for `authoring.blend`; it names each built output object and uses the original captures by absolute path. Run this manifest into another fresh directory after editing those objects. Keep the captures available locally.
- `build-report.json`: source, tool and output checksums, Blender version and `requires-visual-review` status. A successful build never approves combat coverage automatically.

Preserve the project file, original scene and local captures. Rebuild into a new directory when an asset, tool or API changes; compare reports and test the package in game. Keep separate projects/assets for different rest skeletons. A build failure is explicit rather than silently re-fitting an incompatible body. This workflow has passed a real female chest build and synthetic shape-preserving loose-panel tests; see qualification evidence above.

## Preview surface orientation

Render an original garment from the front, side and an elevated angle before packaging. The optional body is shown for fit context; camera framing uses the garment's evaluated world-space vertices. Run from the repository root into a new output directory:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/render_preview.py -- --scene 'C:\outfits\my-robe\source.blend' --object 'Original robe hood' --body 'Body_GEO' --out 'C:\outfits\my-robe\preview-v1'
```

The tool hides every other mesh, replaces the garment's material slots on an in-memory mesh copy without changing polygon assignments, and renders back-facing triangles transparent through Cycles `Geometry.Backfacing`. It writes `front.png`, `side.png`, `elevated.png` and `preview.json`; it never saves the input scene. Cloth/trim use authored linear `diffuse_color` values displayed through Blender AgX; void uses unlit black. Inspect missing surfaces and inverted winding against an ordinary Blender view and the pose report. This diagnostic is not an exact Unity/HDRP render: the current runtime requests double-sided materials, and lighting, palette selection and shader behavior differ. Keep previews outside the distributed package and still verify appearance in game.
## Capture the reference

On an authorized, available test client, `ctx.Models.CaptureRigReference(modelRoot, renderers).Save(absolutePath)` exports complete readable meshes, their triangle topology, normals, UVs, four skin weights, bind matrices, hierarchy, and current baked pose. It must run on the main thread. `RigReference.Capture` provides the same operation without a plugin facade. Captures contain reference geometry for local authoring; do not redistribute native body geometry as clothing.

The development bridge command `rig_reference` accepts `{"action":"list"}` for body/overlay renderer IDs and ancestor IDs. Then use `{"action":"list","rootId":123}` to inspect **all** skinned renderers under that exact model root, including separate head/face surfaces omitted by the unscoped list. Capture an authored rig with `{"action":"capture","rootId":123,"rendererIds":[456,789],"path":"C:/captures/rest.json"}` using current IDs. For garment export use the `recommendedModelRootId` shared by the selected body and overlay: this is the direct parent of an attached Progenitor wearable, matching the runtime's exact `HybridModelUser` model Transform. If no recommendation exists, equip a procedural wearable during an authorized test or resolve that exact Transform in your plugin. An arbitrary world ancestor or nested skeleton root may produce a valid reference but cannot bind as an authored runtime garment. IDs in this example are placeholders. Follow [bridge request-file conventions](../../docs/bridge.md#calling-without-mcp); do not paste these placeholder IDs into an automated run.

For an unreadable native head surface, select its current renderer ID from the scoped list and capture a **posed surface** with `{"action":"surface","rootId":123,"rendererIds":[456],"path":"C:/captures/head-pose.json"}`. This records one frame of baked vertices and triangle topology in model-root space, plus the current joint transforms. It intentionally has no rest geometry, bind matrices, skin weights, UVs or normals and cannot serve as an authored rig reference or garment source. Both capture actions create a new file and refuse to replace an existing one. A posed surface can guide local hood clearance at the sampled pose; it cannot prove fit through animation.

For authored-rig pose comparisons, use the same selected renderers, model root and body type for every pose. Save each capture to a different filename; capture never overwrites a file. The operation observes the current frame; it does not play or record an animation clip. The authored `capture` action rejects unreadable meshes, blend shapes, non-unit renderer scale, incomplete skin and inconsistent bind matrices. The `surface` action can measure an unreadable mesh but still rejects non-unit renderer scale and missing posed topology. A rig with a different rest shape needs separate authoring and captures.

## Import

If the readable body mesh omits joints used by a garment, add `bindRendererIds` to the bridge `capture` request. Select explicit armor renderers from the same model-root inventory. Their bind matrices supplement the skeleton without exporting their triangles, textures or weights. The capture rejects conflicting rests, duplicate bone identities and renderers carried by joints found in the skin palettes. Callers must choose renderer frames that remain static relative to the model; animation on an unlisted helper transform is not detected. This also works with unreadable donor meshes because only their bind metadata is used.

From the repository root in PowerShell:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/import_reference.py -- 'C:\path\capture.json' --out 'C:\path\reference.blend'
```

The importer compares every evaluated Blender vertex against the captured baked vertex. It saves the scene and a sibling `.verification.json` only when the maximum error is at most 1 mm. Set `--tolerance-mm` explicitly to change this threshold. `--python-exit-code 1` is required for automation to receive failures as nonzero exit codes. A passing import verifies deformation at this captured pose, not garment coverage or other poses.

Import a posed-surface capture into a **new** Blender scene for visual measurement:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/import_pose_surface.py -- 'C:\captures\head-pose.json' --out 'C:\captures\head-pose-reference.blend'
```

The importer refuses an existing `.blend` output and creates static wireframe meshes marked `referenceOnly` and `exportForbidden`. It creates no armature, modifiers, vertex groups or skin binding. Coordinates are converted to Blender axes but remain in the capture's **posed model-root frame** by default. If a full rig reference has an explicit `Head_JNT` rest matrix, add `--align-head-rest --reference 'C:\captures\rest.json'` to rigidly align the captured head pose with that rest frame. This is labeled `head-rest-rigid-approximation` in the scene: it discards scale/shear and cannot undo skin deformation or turn posed points into bind geometry. Keep this reference separate from exportable garment objects.

The scene retains complete source triangle topology, UVs, transformed source normals, weights and bone hierarchy. Optional submesh metadata preserves material groups. Known framework cloth/gold/void names receive their captured RGBA (or legacy preview defaults) and export surface tags; native material names are reference labels only. Missing UVs receive zero coordinates and `sourceUvMissing=true`, suitable only for the existing uniform wearable surfaces. Each mesh is marked `referenceOnly`; use the body as an authoring/fitting target, not as a garment to redistribute. Bone custom properties retain the source path. Helpers without a known bind rest use their captured pose as rest and carry `restFromPose=true`; weighted bones require consistent bind matrices. Meshes are transformed into root space, and linear skinning is used without Blender's preserve-volume option.

Coordinates map Unity `(x,y,z)` to Blender `(x,-z,y)`. Matrices are read row-major and conjugated by that conversion. This mapping has positive determinant, so original triangle index order is retained; no geometry reflection or winding reversal is introduced. Source normals are transformed separately. No cloth simulation, topology repair, smoothing or weight modification is performed during import.

Run the original synthetic smoke fixture without any game assets:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/smoke_test.py
```

Expected markers are `SYNTHETIC_PASS` and `TRANSPOSE_REJECTED`. The fixture exercises child-before-parent ordering, mixed weights, a translated/rotated renderer and nonuniform posed-bone scale. It rejects a deliberately transposed pose. Optionally append `-- 'C:\path\fixture.json'` to write the fixture for testing the importer CLI. This synthetic test does not establish live-game capture correctness.

## Prepare original clothing

In the imported scene, create or import your original garment and bind it to the reference armature using matching bone-group names. Keep the reference skeleton's rest transforms unchanged. Use one enabled linear Armature modifier with vertex groups, without envelopes, modifier masks, constraints, animation overrides or preserve-volume deformation. Apply other geometry modifiers before fitting/export. The stored mesh vertices are always the rest geometry even while the viewport shows the captured pose.

If the capture includes your own procedural `progenitor-wear-...` mesh, duplicate that original garment for editing and set the duplicate's custom property `referenceOnly` to `False`. Keep native body meshes marked `referenceOnly=True`. The tools refuse to fit or export a mesh still marked as a reference. This marker is a workflow guard, not an ownership detector.

Assign materials named `cloth`, `gold`, or `void`, or give each material a `wearableSurface` custom property with one of those values. Supply UVs. The exporter stores material `diffuse_color` in `surfaceColors` as four finite shader/linear RGBA values in `[0,1]`, without display-color conversion. One surface must have one color: conflicting cloth/gold/void material colors fail explicitly. Runtime clones only overridden materials for that garment, so recoloring one outfit does not change another. Older JSON without colors uses the shared defaults. Shader models, textures, metallic settings and emission are not exported; the runtime surface still supplies those properties.

Create a garment vertex group named `FitRegion`: weight 1 fully fits to the body; weight 0 preserves the original shape; intermediate weights feather the transition. Select close-fitting cuffs, shoulder attachments or the bodice deliberately. Leave flared skirts, cape panels and hanging sleeves outside this group. Optionally add a body vertex group to restrict reference triangles to the intended limb/torso region, avoiding nearest-surface matches to another nearby limb.

## Fit or transfer weights

For a second body type, first capture its own rest skeleton and body. `retarget_garment.py` carries an **original** authored garment between two explicit reference captures; it validates the source rests and maps only unambiguous bones with matching joint ancestry. Missing target rest data fails rather than substituting the captured animation pose.

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/retarget_garment.py -- --garment 'C:\outfits\robe\female.json' --source-reference 'C:\outfits\robe\female-rig.json' --target-reference 'C:\outfits\robe\male-rig.json' --out 'C:\outfits\robe\male-v1.json' --scene-out 'C:\outfits\robe\male-v1.blend'
```

This supplies a starting shape, not a fit approval. The scene contains a reference-only target body and an editable original garment. Fit deliberately selected regions using the steps below, and inspect front/back, hands, soles and extreme poses. For loose lower panels, `--preserve-below-y` uses a Unity-space height in meters and carries vertices below it rigidly with `--anchor-bone` (default `Pelvis_JNT`), preserving their shape and mapped skin weights. Choose and inspect the transition explicitly; this is not cloth simulation.

Run from the repository root. Replace the example paths and object names with the saved scene's actual names:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background 'C:\captures\authoring.blend' --python-exit-code 1 --python tools/rig-reference/fit_garment.py -- --garment 'Original robe' --body 'Body_GEO' --region FitRegion --clearance-mm 6 --max-distance-mm 50 --name 'Fitted robe' --out 'C:\captures\fitted-v1.blend'
```

The tool creates a separate garment object, preserves topology/UVs and unselected vertices, and samples the body's rest surface. It interpolates skin weights on the nearest triangle **before** adding clearance, blends partial masks with existing weights, and normalizes the strongest four influences. The `.fit.json` report records discarded influence counts and movement; inspect it. A search beyond `--max-distance-mm` fails rather than reaching an unrelated body part. `--body-region BodyArm` restricts eligible triangles to ones whose three vertices are in that group.

For loose fabric, transfer skin weights without changing any positions:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background 'C:\captures\authoring.blend' --python-exit-code 1 --python tools/rig-reference/fit_garment.py -- --garment 'Original robe' --body 'Body_GEO' --weights-only --weight-region WeightRegion --max-distance-mm 300 --name 'Weighted robe' --out 'C:\captures\weighted-v1.blend'
```

`WeightRegion` is an explicitly painted garment group. For a combined pass, omit `--weights-only` and provide both `--region FitRegion` and `--weight-region WeightRegion`. Geometry moves only within `FitRegion`; weights change only within `WeightRegion`. A body-nearest transfer is a starting point for loose panels, not a drape solution: inspect and paint weights as needed. The tool neither adds cloth physics nor creates secondary-motion bones. Preserve your intended silhouette and validate it in motion.

## Check captured poses

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background 'C:\captures\fitted-v1.blend' --python-exit-code 1 --python tools/rig-reference/check_poses.py -- --garment 'Fitted robe' --body 'Body_GEO' --captures 'C:\captures\rest.json' 'C:\captures\attack-three.json' --out 'C:\captures\pose-report.json'
```

The checker validates correspondence with the imported body/skeleton, replays each pose, verifies its body vertices against the game's baked capture, and restores the prior pose. It reports body/garment triangle intersections and near-surface/inward-facing samples. Large clearance is intentional for loose fabric and is not flagged as a defect. Findings need inspection; absence of findings does not prove coverage, self-intersection freedom, or safety between sampled poses. No continuous collision detection or cloth simulation is performed. Capture both rest and extreme attacks for each supported body type; open body surfaces and inconsistent normals limit signed-distance interpretation.

For a posed authoring scene, inspect visible body exposure in a deliberately chosen rectangle with the separate read-only ray check:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/check_visible_coverage.py -- --scene 'C:\outfits\my-robe\posed.blend' --garment 'Fitted robe' --body 'Body_GEO' --directions front back --u-min -0.16 --u-max 0.16 --v-min 1.30 --v-max 1.57 --step-mm 2 --out 'C:\outfits\my-robe\upper-torso-coverage.json'
```

Coordinates are in the body object's local meters. For front/back, `u` is X and `v` is Z; for left/right, `u` is Y and `v` is Z; for top/bottom, `u` is X and `v` is Y. Use `bottom` to inspect soles, with bounds restricted to the foot footprint. Save a separate posed scene for each pose to inspect. The output counts body-hit rays, rays missing the garment, and rays where the body is ahead of the garment by more than `--epsilon-mm` (default 0.5). It retains at most 64 example cells per direction and rejects a grid above `--max-cells` (default 100,000); choose tight bounds around a region expected to be covered. An intentional opening can yield body-first rays, while a zero count at these views does not prove complete coverage or live-game appearance. This check does not modify geometry or approve a fit.

## Export and register

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background 'C:\captures\fitted-v1.blend' --python-exit-code 1 --python tools/rig-reference/export_garment.py -- --garment 'Fitted robe' --reference 'C:\captures\rest.json' --out 'C:\captures\robe-v1.json'
```

Export uses unposed geometry, resolves object transforms into model-root coordinates in meters, splits triangle corners to preserve UV seams/normals, and retains at most four skin influences. More than four positive influences fails; reduce them deliberately first. The exported limit is 60,000 split vertices, 512 bones and 64 MiB JSON. Fitting and export require new output filenames. Store the authored `.blend`, garment JSON, fitting masks and recipe settings with the outfit's source so an API rebuild does not become the source of truth for the design.

Load the exported JSON with the same client/server outfit registration:

```csharp
var graphics = WearableGraphics.FromAuthoredJson(
    WearableSlot.Chest, System.IO.File.ReadAllText(garmentPath), "DonorRendererPrefix");
ctx.Content.DefineItem("myoutfit.chest")
    .CloneOf("Item_Chest_T09_Dracula_Scholar")
    .Named("My Original Robe")
    .Wearable(graphics)
    .Register();
```

Resolve `garmentPath` from the plugin's packaged assets and replace the donor prefix after inspecting that outfit. Both sides need the registration. Authored weights bypass procedural weight transfer. The runtime verifies bone paths and full bind-rest matrices and refuses a mismatched body instead of silently reshaping the garment. Use `WearableGraphics.FromAuthoredJsonVariants(slot, new[] { femaleJson, maleJson }, hideNames)` for explicit body-specific assets. The runtime selects by complete compatible paths and rests, not a body label. `WithAuthoredOverlayVariants` provides the same selection for native hood inserts. The existing procedural wearable path remains available. This is a garment JSON path, not an expansion of `ImportGlb` to skinned models.

Once registered, the runtime handles some behavior for every garment, with nothing to author: it hides with the wearer's body (a dead player waiting to respawn), follows the game's overlay effects, is removed cleanly on unequip, and takes the player's dye through `surfacePalette`. Do not build these into the garment. See [What every worn item gets](../../docs/wearables.md#what-every-worn-item-gets).

## Offline checks

Run each script with Blender from the repository root:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/test_build_outfit.py
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/test_materials.py
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/test_fit.py
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/test_check_poses.py
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/test_visible_coverage.py
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/test_export.py
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/test_import_pose_surface.py
dotnet test tests/Progenitor.Tests/Progenitor.Tests.csproj --filter 'FullyQualifiedName~WearableAuthored|FullyQualifiedName~RigReference'
```

Expected Blender markers: `FIT_PASS`, `POSE_CHECKS_PASS`, and `EXPORT_PASS`. These use original synthetic geometry. They do not qualify the actual robe's combat coverage or garment rendering in the game.

Optional palette mapping: set a material's `wearablePalette` custom property to `primary`, `accent`, `neutral` or `metal`. The exporter writes `surfacePalette` while retaining `surfaceColors` as the fallback. Materials using the same surface must agree on mapping, including whether it is fixed. Leave the void material unmapped to keep the face black. Palette colors come from the garment's visible equipment slot by default. Set the garment object custom property `wearablePaletteSource` to `Chest` to save inheritance through export, fitting and resumed builds. Its exported `paletteSource` accepts exact public `WearableSlot` names. Alternatively use `.WithPaletteSource(WearableSlot.Chest)` on a Headgear recipe to follow the effective chest slot, including its cosmetic override. This does not add palette choices to items that have none.

**Source-checked:** runtime reads `TransmogPaletteData` through `ManagedDataRegistry.TryGet`, bounds-checks `TransmogIndex - StartIndex`, and uses authored colors when palette data is absent or invalid. A native default choice does not necessarily mean missing data. **Verified live, limited:** choosing the top-right swatch changed the robe chest to red; the hood remained blue because its fixture still read Headgear. Hood-following through `WithPaletteSource` is verified in a live material capture: hood and chest cloth match, with the black recess unchanged. The picker X hides equipment; explicit selection of the desired authored blue remains unresolved. The earlier crash involved a native utility wrapper that current code no longer calls.


Authored color policy: set object `wearablePaletteMode` to `authored` to retain `surfaceColors` despite a selected game palette, or `native` for normal palette following. The API equivalent is `.WithAuthoredColors()`. This does not create a player-facing swatch; selected native equipment data remains unchanged.

Fitting reports include `distortion` metrics for the whole garment and every triangle touched by the fit mask, including seams. `requiresReview` flags strong edge compression/stretch, area loss, normal reversal, or increased dihedral roughness. Read the report definitions: these are local before/after comparisons, not proof of topological inversion, clearance or animation safety. Diagnostics do not change geometry or automatically reject a build. Inspect flagged fits before packaging them for a visual test.

The main `build-report.json` includes per-garment `fitDiagnostics` and `fitRequiresReview`, and uses `status: requires-fit-review` when a new fit has severe distortion flags. A null fit result means no fitting was performed in that build; resuming an already-fitted scene does not reassess or clear its earlier fit warnings. Retain the original fit report when reviewing a resumed scene.
