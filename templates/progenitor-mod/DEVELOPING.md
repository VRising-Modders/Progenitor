# Developing Progenitor Mod Display Name

Follow [PROGENITOR_GUIDE.md](https://github.com/VRising-Modders/Progenitor/blob/main/PROGENITOR_GUIDE.md) in the Progenitor repository.

## Build and test

Needs the .NET 8 SDK, V Rising started once with BepInEx, and a Progenitor checkout (set `ProgenitorRepo` in a git-ignored `Directory.Build.local.props` if it is not found; see `Directory.Build.props`).

```
dotnet build
dotnet test ProgenitorMod.Tests
```

The tests need no game.

## Try it in the game

Copy `ProgenitorMod/bin/Debug/net6.0/ProgenitorMod.dll` to `BepInEx/progenitor-reload/` on the client and on the server. A rebuilt copy hot reloads. Remove it when you are done testing.

## Files

| File | What it is |
|---|---|
| `ProgenitorMod/Plugin.cs` | Attributes, `OnLoad`, settings, keybind, server request handler. |
| `ProgenitorMod/ModWindow.cs` | The client window (a Basic window preset, text in named styles) and the notice. |
| `ProgenitorMod/ModRules.cs` | Game-free messages and rules, linked into the tests. |
| `README.md` | The Thunderstore listing and the Mods page's About text (embedded). |
| `help/index.html` | The page the Help button on the mod's page opens. Edit only the block between `HELP-CONTENT START` and `HELP-CONTENT END`; keep a page to about 60 words. Everything under `help/` is embedded, so images and stylesheets can sit beside it. |
| `thunderstore/manifest.json`, `CHANGELOG.md` | Package files. Add a 256x256 `icon.png` and a `LICENSE`. |

## Publish

Bump the version in `<Version>` in `ProgenitorMod.csproj`, `thunderstore/manifest.json` and the help page (`version:` in `help/index.html`) together; a test fails when the help page and `<Version>` differ. Change `Peers` only when wire compatibility changes (a removed or retyped field: a new major range). Add a `CHANGELOG.md` section, then follow the Progenitor repository's [publishing guide](https://github.com/VRising-Modders/Progenitor/blob/main/docs/publishing.md) and run its `tools/publish-check.ps1`.
