# Developing Progenitor Mod Display Name

Follow [PROGENITOR_GUIDE.md](https://github.com/VRising-Modders/Progenitor/blob/main/PROGENITOR_GUIDE.md) in the Progenitor repository.

## Build and test

Needs the .NET 8 SDK, V Rising started once with BepInEx, and a Progenitor checkout (set `ProgenitorRepo` in a git-ignored `Directory.Build.local.props` if it is not found; see `Directory.Build.props`).

```
dotnet build
dotnet test ProgenitorMod.Tests
```

The tests need no game.

## Keep the template and Progenitor together

Install the template from the same Progenitor checkout you set as `ProgenitorRepo` (`dotnet new install <checkout>/templates/progenitor-mod`), and update both together: a template from a newer checkout uses Progenitor API an older checkout does not have. `ProgenitorMinimumVersion` in `Directory.Build.props` records the Progenitor version the template came from. Before compiling, the build reads the version of the `ProgenitorRepo` checkout from its git history and stops with one error when the checkout is older:

```
This mod's template needs Progenitor 2026.10.139 or newer; ProgenitorRepo ('D:\src\Progenitor') is a checkout of 2026.10.120. ...
```

Fix it by updating that checkout (`git pull`) or pointing `ProgenitorRepo` at a newer one. When the version cannot be read (no git, or no `main` in the checkout), the build warns and continues. Raise `ProgenitorMinimumVersion` when your mod starts using newer Progenitor API. `/p:ProgenitorSkipVersionCheck=true` skips the check.

## Try it in the game

Copy `ProgenitorMod/bin/Debug/net6.0/ProgenitorMod.dll` to `BepInEx/progenitor-reload/` on the client and on the server. A rebuilt copy hot reloads. Remove it when you are done testing.

## Files

| File | What it is |
|---|---|
| `ProgenitorMod/Plugin.cs` | Attributes, `OnLoad`, settings, keybind, server request handler. |
| `ProgenitorMod/ModWindow.cs` | The client window (a Basic window preset, text in named styles) and the notice. |
| `ProgenitorMod/ModRules.cs` | Game-free messages and rules, linked into the tests. |
| `README.md` | The mod's page in Install Mods (read from the package) and the Mods page's About text (embedded). |
| `help/index.html` | The page the Help button on the mod's page opens. Edit only the block between `HELP-CONTENT START` and `HELP-CONTENT END`; keep a page to about 60 words. Everything under `help/` is embedded, so images and stylesheets can sit beside it. |
| `package/manifest.json`, `CHANGELOG.md` | Package files. Add a 256x256 `icon.png` and a `LICENSE`. |

## Publish

Mods built on Progenitor are published on the [Progenitor Index](https://github.com/VRising-Modders/ProgenitorIndex), not on Thunderstore: a GitHub release of your repository with the package zip attached, and a pull request that adds the version's entry to the Index. Players then install the mod from **Install Mods** in Progenitor's Mods menu.

Bump the version in `<Version>` in `ProgenitorMod.csproj`, `package/manifest.json` and the help page (`version:` in `help/index.html`) together; a test fails when the help page and `<Version>` differ. Change `Peers` only when wire compatibility changes (a removed or retyped field: a new major range). Add a `CHANGELOG.md` section, then follow the Progenitor repository's [publishing guide](https://github.com/VRising-Modders/Progenitor/blob/main/docs/publishing.md): run its `tools/publish-check.ps1`, create the GitHub release, and add the Index entry as its [CONTRIBUTING.md](https://github.com/VRising-Modders/ProgenitorIndex/blob/main/CONTRIBUTING.md) describes.

Set `website_url` in `package/manifest.json` to your repository; a player's log zip (`mods.md`) lists it, and AI assistants read it from there. Add an `llms.txt` at the repository root (or a README section): what the mod does, its settings, keys and commands, and where to report bugs.
