# Model packages

**Status: under development and incomplete.** This experimental asset-import workflow has only scoped example verification. It is not a complete or production-ready third-party armor, character or creature pipeline; the limitations below still apply.

Run from the repository root. Prerequisites: .NET SDK and Blender with its bundled glTF exporter. This offline workflow creates client-side visuals; it does not create inventory items, NPCs, collisions or physics.

1. Export exact mesh names from the active scene of a saved Blender file:

   ```powershell
   & 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/asset-build/blender_static.py -- --blend C:\Assets\prop.blend --objects 'PedestalBase,PedestalColumn,PedestalCap' --output C:\Assets\prop.glb
   ```

   The script exports static meshes and materials and excludes cameras, lights and animations. It rejects shape keys, modifiers, animation/drivers, constraints and armature or non-object parenting; dependency checks include each selected object's ancestors. Apply static modifiers and bake/remove dynamic dependencies in an authoring copy before exporting. Static object parents are flattened while preserving world transforms. The script copies objects into an isolated scene in memory and never saves the source file. Export paths must not already exist.

2. Build a package into a new directory, then validate it:

   ```powershell
   dotnet run --project tools/asset-build -- build C:\Assets\prop.glb C:\Assets\prop-package example.prop
   dotnet run --project tools/asset-build -- validate C:\Assets\prop-package
   ```

   The package contains `model.glb` and a manifest with schema/version and content hash. Validation checks the package contract and parses the GLB through the same `GltfScene` reader used by the client.

For a reproducible original low-poly pedestal without a source file:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --factory-startup --python-exit-code 1 --python tools/asset-build/blender_static.py -- --example --output C:\Assets\pedestal.glb
dotnet run --project tools/asset-build -- build C:\Assets\pedestal.glb C:\Assets\pedestal-package example.pedestal
dotnet run --project tools/asset-build -- validate C:\Assets\pedestal-package
```

Expected offline acceptance: Blender writes the GLB, package build succeeds, and validation succeeds. These offline steps do not establish appearance in the game. Pedestal and Frankenstein static previews subsequently passed scoped isolated-client checks (verified live on 2026-10-08). Repeat live checks only in an authorized client session.

Blender export does not guarantee arbitrary third-party materials work. Support is limited to the `GltfScene` contract: triangle meshes, node transforms, supported own-rig skins/animation, embedded images and supported metallic-roughness material fields. Blender procedural shaders, material extensions and advanced glTF features may need authoring changes; package validation rejects unsupported required features. Inspect the client visual separately after offline validation.

## Animated export and packages

Export the original two-joint banner with `Breeze` and `Gust` clips, build a schema-2 package and list its clips through validation:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --factory-startup --python-exit-code 1 --python tools/asset-build/blender_animated.py -- --example --output C:\Assets\animated-banner.glb
dotnet run --project tools/asset-build -- build-animated C:\Assets\animated-banner.glb C:\Assets\animated-banner-package example.animated-banner
dotnet run --project tools/asset-build -- validate C:\Assets\animated-banner-package
```

For a saved authoring scene, replace the first command with:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe' --background --python-exit-code 1 --python tools/asset-build/blender_animated.py -- --blend C:\Assets\banner.blend --objects 'BannerRig,BannerCloth' --output C:\Assets\banner.glb
```

Include meshes, their armature and every object ancestor. Meshes may use at most four joint influences per vertex and only armature modifiers; shape keys are refused. The exporter samples to LINEAR node translation/rotation/scale channels. The importer also supports STEP channels and inverse bind matrices. CUBICSPLINE, morph animation, additional influence sets and external images/buffers are refused. Output paths and package directories must be new. Validation reports node, mesh, material and skin counts plus animation names.

`ModelPackage.BuildAnimated(glbPath, outputDirectory, assetId)` publishes schema 2 with `kind: "animated-model"` and requires at least one clip. `ModelPackage.Build` continues to publish schema 1 with `kind: "static-model"` and refuses skins or animation. Both packages contain `model.glb` and a content-hashed manifest. `ImportPackage` accepts either schema and exposes `model.Animations` for authored playback.

**Verification:** compile and offline checks passed, including 113 focused parser, sampler and package tests. Blender example export passed offline. The public Core import API passed scoped in-game skinning, playback, crossfade, rest-pose and cleanup checks using the original two-joint banner. The example controls and animated-package import also passed in-game; arbitrary rigs remain unqualified. Use the [animated client example](../../samples/Progenitor.AnimatedModelExample/README.md) for controls and the acceptance procedure. These clips drive their own imported hierarchy; native-player retargeting, gameplay casting and network synchronization are outside this contract.


## Runtime use and acceptance

The [client example](../../samples/Progenitor.AssetPackageExample/README.md) provides a buildable configuration-driven acceptance harness.

In a client plugin, `var model = ctx.Models.ImportPackage(packageDirectory);` returns the same tracked `ImportedModel` as `ImportGlb`. Position `model.Root` at the intended test location on the client main thread. Disposing it or unloading its context releases the visual and its generated resources. This does not register a server entity. Install the package directory on each client that uses it; automatic delivery and version negotiation are not implemented.

**Acceptance procedure:** in an available isolated test client, import the example package, confirm the three-part pedestal appears with the expected proportions and color, then dispose/reload and confirm no duplicate or leftover objects. Try a deliberately changed copy of `model.glb`: import must fail with a hash mismatch before visual creation. Remove the test plugin and restore the client to its normal idle state after the batch.

Schema 1 requires `schemaVersion: 1` and `kind: "static-model"`; schema 2 requires `schemaVersion: 2` and `kind: "animated-model"`. Both require a 1–128-character ASCII asset ID (letters, digits, dots, underscores and hyphens), `model: "model.glb"`, and a hexadecimal SHA256. The ID is descriptive and does not allocate inventory identity. Limits are 64 KiB for the manifest and 256 MiB for the GLB. Symbolic links/junctions are refused. A hash detects changed content; it is not a publisher signature. Keep the original Blender source separately; packages contain no editable authoring scene.

Validation covers the supported structural contract, not complete glTF conformance or image decoding offline. Texture pixels are decoded by Unity at runtime. Supported maps are base color and emissive on UV0; other material maps are not imported. Alpha blending currently uses alpha testing. Normal maps, procedural materials, cloth simulation and gameplay bindings require further pipeline work.

For own-rig playback and future playable-character integration, start with the [animation import reference](../../docs/player-animation-import.md). It documents current visual controls and separates native player controller/rig mapping requirements from supported authored clip playback.
