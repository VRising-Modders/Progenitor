# Draped hood authoring example

## Male Guide accessory candidates

`retarget_guide_accessories.py` retargets the original shoes and black face insert to an explicit target capture. It uses the measured Guide insert adjustment (2% wider, 3 mm forward), preserves native palette following, and leaves the face black. It does not export native hood or body geometry. Supply the original female bind capture and a coherent male body capture; keep both captures and the generated reference scenes local.

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python samples/Progenitor.RobeSet/authoring/retarget_guide_accessories.py -- --source-reference 'C:\outfits\guide\female-reference.json' --target-reference 'C:\outfits\guide\male-reference.json' --out 'C:\outfits\guide\male-accessories-v1'
```

The output directory must be new. It contains original `boots-male.json` and `face-male.json`, reference-only authoring scenes, and retarget reports. JSON files are packaging candidates; scenes contain game-owned references and must not be distributed. **Verified offline:** recipe runs on Blender 4.5; shoes have no body intersections in two captured idle poses. Combat and live male appearance remain unverified.

For chest/glove starting retargets, `male-helper-bone-map.json` explicitly maps the female elbow/knee helper joints absent from the captured male rig onto their corresponding articulated joints. Pass it with `retarget_garment.py --bone-map`; it is an authored choice for this outfit, not permission to ignore missing bones or rest mismatches. A rig-compatible retarget still needs geometry fitting and pose review.

Create the starting scenes from the repository root, using new output paths:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/retarget_garment.py -- --garment samples/Progenitor.RobeSet/garments/chest.json --source-reference 'C:\outfits\guide\female-reference.json' --target-reference 'C:\outfits\guide\male-reference.json' --bone-map samples/Progenitor.RobeSet/authoring/male-helper-bone-map.json --preserve-below-y .95 --out 'C:\outfits\guide\chest-start.json' --scene-out 'C:\outfits\guide\chest-start.blend' --object-name 'Original male chest'
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/retarget_garment.py -- --garment samples/Progenitor.RobeSet/garments/gloves.json --source-reference 'C:\outfits\guide\female-reference.json' --target-reference 'C:\outfits\guide\male-reference.json' --bone-map samples/Progenitor.RobeSet/authoring/male-helper-bone-map.json --out 'C:\outfits\guide\gloves-start.json' --scene-out 'C:\outfits\guide\gloves-start.blend' --object-name 'Original male gloves'
```

The chest recipe replaces the upper cloth with measured original rings and sleeves, preserving the loose skirt and lower gold. Its body slice centers follow the neck; a fixed waist center is unsuitable for the upper torso. The glove recipe fits separate limb/finger regions and adds original cloth grids around the thumb web. Both recipes are measured Guide designs, not general garment generators:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python samples/Progenitor.RobeSet/authoring/build_male_chest.py -- --scene 'C:\outfits\guide\chest-start.blend' --reference 'C:\outfits\guide\male-reference.json' --garment 'Original male chest' --out 'C:\outfits\guide\chest-fit-v1'
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python samples/Progenitor.RobeSet/authoring/build_male_gloves.py -- --scene 'C:\outfits\guide\gloves-start.blend' --reference 'C:\outfits\guide\male-reference.json' --garment 'Original male gloves' --body Body_GEO --blend-out 'C:\outfits\guide\gloves-fit-v1.blend' --json-out 'C:\outfits\guide\gloves-fit-v1.json' --report-out 'C:\outfits\guide\gloves-fit-v1-report.json'
```

Inspect the output scenes and run the [pose and visible-coverage checks](../../../tools/rig-reference/README.md#check-captured-poses) before replacing packaged assets. Failed runs may leave partial candidate outputs; use a new output path when retrying. These recipes do not simulate cloth or approve coverage between sampled poses. Preserve the accepted female assets and register both explicitly with `FromAuthoredJsonVariants`; native hood inserts use `WithAuthoredOverlayVariants`.

## Original draped hood recipe

This creates an original hood with a compact opening, a thin folded lip, integrated lower outer folds and a short black face recess. It is a shape recipe for the captured female standard robe scene, not a general body fitter or automatic retargeter. The lower cloth blends from Head_JNT toward NeckBase_JNT. Cloth maps to primary and trim to metal; void remains fixed. Runtime palette reads are enabled. A Headgear recipe can call `.WithPaletteSource(WearableSlot.Chest)` to follow the effective chest slot, and a test fixture has verified matching hood/chest colors live.

Inputs are a local reference scene containing Original robe hood/chest/gloves/boots, Body_GEO and its captured armature, plus the matching capture JSON. The hood object's existing head group identifies the rig; this script does not copy body geometry. The scene contains native body references and must stay local. Supply a fresh output directory.

From repository root:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python samples/Progenitor.RobeSet/authoring/build_hood.py -- --scene 'C:\outfits\robe\authoring.blend' --reference 'C:\outfits\robe\reference.json' --out 'C:\outfits\robe\hood-project'
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/rig-reference/build_outfit.py -- 'C:\outfits\robe\hood-project\outfit.json' --out 'C:\outfits\robe\hood-build'
```

The first command saves source.blend and outfit.json. The second emits the original four-piece garment package with diagnostics and hashes, plus authoring.blend and a new resumable outfit.json naming the built objects. Edit those objects and run the new manifest into another fresh output directory to continue. Only package/ is distributable; never package reference scenes or captures. Preserve the previous package until the new asset is visually accepted. Existing sample registration remains procedural; this script does not change the running game or select body variants.

**Verified offline:** Blender 4.5.10 recipe/export and repeated hood JSON hash match. The candidate has 3538 authoring vertices and 3640 faces. Pose diagnostics require review; intersections are present. **Verified live, limited:** selecting the top-right swatch changed the robe chest to red. A later fixture with chest inheritance produced matching hood/chest cloth colors and preserved its black face. The picker X hides equipment. Explicit selection of the desired authored blue, hood movement, user visual acceptance and male fit remain unverified. The reference scene is not distributed here.

The current brow falls close to the face instead of projecting horizontally. Dimensions were revised against a local posed capture of the separate female face, approximately aligned through Head_JNT; Body_GEO alone omits that surface. This remains a fixed female shape recipe. The rebuilt hood was loaded on both isolated test sides; visual acceptance and combat coverage remain pending.

## Native hood alternative (female reference)

The original `female-native-hood-insert.json` is a 27-vertex black face cover; it contains no native hood triangles. It requires the matching female Head_JNT bind reference. The isolated test fixture borrows the Razer hood already supplied by the cloned headgear:

```csharp
var hood = WearableGraphics.FromNativeRenderer(WearableSlot.Headgear, "RazerHood_GEO", "Hair")
    .WithNativeColorOverride(0, "_BaseColor", "primary", 0f, 84f / 255f, 182f / 255f, 1f)
    .WithNativeFixedColor(0, "_EmissiveColor", 0f, 0f, 0f, 1f)
    .WithNativeWhiteTexture(0, "_BaseColorMap")
    .WithAuthoredOverlay(System.IO.File.ReadAllText("female-native-hood-insert.json"))
    .WithPaletteSource(WearableSlot.Chest);
```

Distribute the original insert with your plugin (an embedded resource also works). Native geometry/textures remain game-owned. This alternative is build-verified and observed live on the female test character with pale chest colors and black face; male bind compatibility and combat coverage remain unqualified. The Guide sample now embeds the authored pieces and uses this native hood recipe.

## Guide low shoes (female reference)

`make_guide_shoes.py` builds original low cloth shoes with closed soles and gold welts. It uses the measured female shape and transfers weights from `Body_GEO`; it does not copy body triangles. The leg above the shoe opening is intentionally exposed. This fixed shape recipe is not a general shoe fitter or a male-body variant.

The local scene must contain `Original robe boots`, its compatible captured armature/materials, and `Body_GEO`. Use the matching reference capture and fresh output paths:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background 'C:\outfits\robe\source.blend' --python-exit-code 1 --python samples/Progenitor.RobeSet/authoring/make_guide_shoes.py -- --reference 'C:\outfits\robe\reference.json' --out 'C:\outfits\robe\shoes-v1\boots.json' --scene-out 'C:\outfits\robe\shoes-v1\source.blend' --weight-report 'C:\outfits\robe\shoes-v1\weights.json'
```

The export preserves cloth-to-primary and gold-to-metal palette mappings. Review standing and raised-foot poses from below and around the toes before replacing the embedded `garments/boots.json`. The visible-coverage tool supports `bottom` and `top` views; restrict the reference region to the foot so intentionally exposed legs do not count as missing shoe coverage. Local scenes and captures contain native references and must not be distributed.
