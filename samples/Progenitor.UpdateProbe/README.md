# Progenitor Update Probe

A client-side test plugin for the auto-update live tests ([auto-update.md](../../docs/auto-update.md)). It does nothing
but log one line when it loads and one when it unloads. Each build carries the version it is built with, so publishing
1.0.0, 1.1.0 and 1.2.0 to the local Thunderstore stand-in gives three different images, each with the `[BepInPlugin]`
version its `progenitor.json` declares.

- `[BepInPlugin("progenitor.updateprobe", "Progenitor Update Probe", <build version>)]`, `[ProgenitorSide(Side.Client)]`.
- Hot-loadable: no Harmony, no `ctx.UnsafeHarmony`, no injected IL2CPP types.
- `-p:UpdateProbeFailLoad=true` builds a copy whose `OnLoad` throws, for the automatic rollback test.
- Server update tests ([auto-update.md](../../docs/auto-update.md#phase-3-as-built)): `-p:UpdateProbeVariant=Server` builds
  `Progenitor.UpdateProbe.Server.dll` (`progenitor.updateprobe.server`, `[ProgenitorSide(Side.Server)]`), and
  `-p:UpdateProbeVariant=Both` builds `Progenitor.UpdateProbe.Both.dll` (`progenitor.updateprobe.both`,
  `[ProgenitorSide(Side.Both)]`, `ServerMustHave` / `Exact`, so clients need the server's version). Package them with
  `make-dummy-package.ps1 -Sample UpdateProbeServer` or `-Sample UpdateProbeBoth` and `-Dll` pointing at that build.
  Source-checked and built; both were used in the phase 3 live run of 2026-10-09.

## Build one version and package it

From the repository root, with Core already built (`dotnet build src/Progenitor.Core`):

```
dotnet build samples/Progenitor.UpdateProbe -p:ProgenitorVersion=1.1.0 -p:BuildProjectReferences=false -o <build>/1.1.0
pwsh tools/make-dummy-package.ps1 -Sample UpdateProbe -Version 1.1.0 -Dll <build>/1.1.0/Progenitor.UpdateProbe.dll -Out <build>
node tools/local-thunderstore/lts.mjs publish --data <data> --zip <build>/Progenitor-UpdateProbe-1.1.0.zip
```

`-p:BuildProjectReferences=false` keeps the version override off Core's own build output. Add `-NotHotLoadable` to
`make-dummy-package.ps1` for a version that takes the restart route. The live test's steps are in
[test-rig.md](../../docs/test-rig.md#recipe-auto-update-phase-2).

## Clean up

Remove `Progenitor.UpdateProbe.dll` from the client's `BepInEx/progenitor-reload` (and `progenitor-pending`), its rows
from `BepInEx/progenitor-install.json`, the kept copies of `progenitor.updateprobe` (the `join_ui` versions action
`forget`), its entries in `BepInEx/config/progenitor-auto-update.json`, and unpublish it from the stand-in. For the server
variants, uninstall them on the test server (remote uninstall or the same files under the server's `BepInEx`), and
remove any pre-update backups the test made.
