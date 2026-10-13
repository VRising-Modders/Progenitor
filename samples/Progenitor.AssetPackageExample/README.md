# Asset package example

Client-only plugin that imports one configured static model package on load and places its tracked root at configured world coordinates. Empty configuration logs setup instructions and imports nothing. Unloading releases the visual, meshes, materials and textures. The example creates no server entity, inventory item, NPC or physics object and registers no keybinds.

**Verification:** compile-verified; pedestal and Frankenstein static import, sample swap/removal and hash refusal verified live on the isolated client.

## Build and configure

From the repository root with the configured game interop references:

```powershell
dotnet build samples/Progenitor.AssetPackageExample/Progenitor.AssetPackageExample.csproj -c Debug
```

Output: `samples/Progenitor.AssetPackageExample/bin/Debug/net6.0/Progenitor.AssetPackageExample.dll`. This sample is built explicitly and is not added to the solution or default deployment set.

Create a package using the [offline exporter and package commands](../../tools/asset-build/README.md). On the authorized test client, set `BepInEx/config/progenitor.assetpackage.example.cfg` before loading the plugin, or let its empty first load generate that configuration. Replace the example directory and coordinates with your package path and a visible, known test location:

```ini
[Package]
Directory = C:\Assets\pedestal-package

[Placement]
X = 0
Y = 0
Z = 0
Scale = 1
```

Coordinates are Unity client world coordinates; Y is height. Scale is a positive uniform multiplier; centered source models may require lifting Y so their lowest vertex rests above the ground. Changes take effect when this plugin reloads. Use a current Core that includes `ctx.Models.ImportPackage`.

## Repeat isolated live acceptance

Use only an available client in an explicitly authorized isolated test session. Follow [hot reload](../../docs/hot-reload.md) for reload admission and loaded-image checks. Never target the live server or a client the user is using to play.

1. Build successfully and validate the package offline. Identify the authorized test client's installation root and ensure it has compatible Bootstrap/Core. Configure the package path and a visible world location.
2. Copy only the built sample DLL into that client's `BepInEx/progenitor-reload` directory. This is a client plugin; do not deploy it to the server. Confirm the reloader loaded the intended image and the log reports `Asset package example: imported client visual at ...`.
3. In the isolated game world, inspect the original pedestal: expect three cuboids with the authored proportions and color. Record appearance separately from the successful import log.
4. Reload the sample through the documented reload procedure. Confirm a clean unload/load transition and exactly one pedestal. Remove the sample DLL, confirm the unload log, then inspect that its visual disappeared. A file copy or removal alone is not proof of completion.
5. Optional failure check: make a separate copy of the package, change `model.glb` without updating its hash and configure that copy. Expect import failure before visual creation. Restore the valid configuration afterward.
6. At cleanup, remove `Progenitor.AssetPackageExample.dll` from this client's reload directory and confirm clean unload. Delete only this sample's configuration and disposable package copies if no longer needed. Remove any other test harnesses deployed for the batch, disconnect normally and verify the title screen as required by the repository's test cleanup rules.

The linked evidence covers the scoped checks above. Do not infer inventory registration, collisions, server replication or arbitrary third-party material compatibility from a visible static prop.
