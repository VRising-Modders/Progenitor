# Publishing a Progenitor mod

Mods built on Progenitor are published on the [Progenitor Index](https://github.com/VRising-Modders/ProgenitorIndex), the home of mods built on Progenitor. You attach the package zip to a **GitHub release** of your mod and open a pull request that adds an **entry** for that version to the Index. Players then find the mod in Progenitor's **Mods** menu, under **Install Mods**, and Progenitor installs it from your release, hot loaded when the mod supports it.

**Thunderstore does not host Progenitor mods**, so do not upload them there ([why](#10-faq)). Thunderstore is still where BepInEx and ordinary mods come from, and Progenitor's Install Mods tab installs those.

This page covers the package and Progenitor's own rules. The Index's entry format, checks and review are in its [README](https://github.com/VRising-Modders/ProgenitorIndex#readme) and [CONTRIBUTING.md](https://github.com/VRising-Modders/ProgenitorIndex/blob/main/CONTRIBUTING.md); this page does not repeat them. Where something is a hard requirement (the Index's or Progenitor's), this page says so; the rest are suggestions from the people who maintain the framework.

The package rules below are **source-checked** against Progenitor's package inspection and install planner. The checker, `tools/publish-check.ps1`, is **game-free tested** (`tests/Progenitor.Tests/PublishCheckTests.cs`). No package built from this page has been listed on the Index or installed from it in a test.

**Contents**

1. [Package layout](#1-package-layout)
2. [Dependencies](#2-dependencies)
3. [The standard Progenitor message](#3-the-standard-progenitor-message)
4. [The Progenitor stamp (optional)](#4-the-progenitor-stamp-optional)
5. [Declare settings and keybindings properly](#5-declare-settings-and-keybindings-properly)
6. [Versions and compatibility](#6-versions-and-compatibility)
7. [Check the package: `tools/publish-check.ps1`](#7-check-the-package-toolspublish-checkps1)
8. [Publish: a GitHub release and an Index entry](#8-publish-a-github-release-and-an-index-entry)
9. [License](#9-license)
10. [FAQ](#10-faq)

## 1. Package layout

A package is a zip in Thunderstore's package format, the format V Rising mod tools read. Its root holds:

| File | Required by | Notes |
|---|---|---|
| `manifest.json` | The Index | UTF-8 **without** a byte-order mark; `publish-check` fails a BOM. |
| `README.md` | The Index | The mod's page in Install Mods. Include the [requirement block](#readme-requirement-block). |
| `icon.png` | The Index | PNG, exactly 256×256. |
| `CHANGELOG.md` | Recommended | One section per version, newest first. Install Mods shows it with updates. |
| `LICENSE` | Recommended | Your mod's license text. |
| `MyMod.dll` | Your mod | The plugin DLL, **at the zip root**. |
| `progenitor.json` | Progenitor | Generated from the DLL; never written by hand ([below](#progenitorjson)). |

The Index also refuses executables and scripts anywhere in the zip ([its package rules](https://github.com/VRising-Modders/ProgenitorIndex/blob/main/CONTRIBUTING.md#package-rules)).

### `manifest.json`

```json
{
  "name": "MyMod",
  "version_number": "1.2.0",
  "website_url": "https://github.com/your-account/MyMod",
  "description": "Client-only · Requires Progenitor. Shows your castle's upkeep on the HUD.",
  "dependencies": [
    "BepInEx-BepInExPack_V_Rising-1.733.2"
  ]
}
```

- **`name`**: PascalCase, letters and digits (`MyMod`). `_` is allowed; a hyphen is not. Your account name is not part of it: the Index entry's folder adds it (`entries/YourAccount/MyMod/1.2.0.json`, which other mods depend on as `YourAccount-MyMod-1.2.0`).
- **`version_number`**: three numbers, `major.minor.patch`. The same string goes in `[BepInPlugin]` and `progenitor.json` ([versions](#6-versions-and-compatibility)).
- **`website_url`**: the mod's GitHub repository, the one whose release carries the zip. Players and server operators use it to see what they are installing, and players' log zips list it in `mods.md` for AI assistants. An `llms.txt` at the repository root (or a README section) tells an assistant what the mod does and where to report bugs.
- **`description`**: at most 250 characters, starting with the side: `Client-only`, `Server-only` or `Client and server`, in the [standard form](#3-the-standard-progenitor-message).
- **`dependencies`**: see [Dependencies](#2-dependencies).

Write the file as UTF-8 without a BOM. In Windows PowerShell 5.1, `Set-Content -Encoding UTF8` and `Out-File` add one; use `[IO.File]::WriteAllText($path, $json)`, which does not.

### Where the DLL goes, and what each install route does

Put the DLL at the zip root, beside `manifest.json`. That is the only layout both install routes accept:

| Installed by | Where the DLL lands | Loaded by | Hot reload |
|---|---|---|---|
| Progenitor's **Install Mods** tab or join screens | `BepInEx/progenitor-reload/<plugin id>.dll` | Progenitor's reloader | Yes, when `progenitor.json` declares `hotLoadable` and the image passes the Install tab's checks ([trusted hotLoadable](mods-menu.md#hotdemo-hot-install-and-uninstall-trusted-hotloadable-hotdemo1)); otherwise it loads at the next start. |
| A mod manager (r2modman, Gale), from the zip file | `BepInEx/plugins/<YourAccount-MyMod>/MyMod.dll` | The BepInEx chainloader, at game start | **No.** Updates need a game restart. |

What this means for a mod-manager install (source-checked; [mods-menu.md](mods-menu.md#ordinary-plugins-in-the-thunderstore-layout-instplug1), [mod-settings.md](mod-settings.md#ordinary-bepinex-plugins)):

- The mod runs normally, but as a restart-only Progenitor mod: no hot reload, and it is never paused for a server that does not run it ([hot-reload.md](hot-reload.md)).
- Progenitor shows it as **Managed by <tool>** when it recognizes the mod manager, otherwise **Managed elsewhere**. Its join screens and auto-update do not replace or keep versions of it; players update it in their mod manager.
- `[BepInDependency("progenitor")]` matters here: it makes the chainloader load Progenitor first.

Two layouts to avoid:

- **The DLL under `BepInEx/plugins/` inside the zip.** A package with a `progenitor.json` and this layout is refused by the Install tab (`Cannot install — the package layout targets BepInEx/plugins/, patchers/ or core/`).
- **Loose data files next to the DLL** (`localization/en.json`, a `.json` the mod reads). The Install tab places the DLL only. Register strings in code (`Localization.Register`) and keep data where the tutorial says ([Where should my data go?](tutorial.md#where-should-my-data-go)).

**Known limitation.** A mod manager cannot place a DLL in `BepInEx/progenitor-reload/`, so mod-manager installs never hot reload. V Rising's mod-manager install rules route only `BepInEx/plugins`, `patchers`, `core`, `monomod` and `config`; a file in any other folder of the zip lands in `BepInEx/plugins/<package>/` with its folders dropped (observed with Gale 1.23.2 on 2026-10-19, and the same rule in r2modman's installer). A package that ships `BepInEx/progenitor-reload/MyMod.dll` therefore installs as an ordinary plugin.

### `progenitor.json`

Progenitor reads this file to verify the package before installing it: the hash proves which DLL it describes, and the other fields must agree with the DLL's own attributes. A package without it installs only by the slower restart route, if at all. Generate it from the built DLL after **every** build:

```powershell
powershell -File tools/publish-check.ps1 -Dll MyMod\bin\Release\net6.0\MyMod.dll -WriteProgenitorJson package\progenitor.json -HotLoadable
```

```json
{
  "id": "yourteam.mymod",
  "version": "1.2.0",
  "side": "client",
  "hotLoadable": true,
  "sha256": "9532f6ae3071fd12bac81a5964be7585a41e3f7ed3df00993e40f1725d6f24e3",
  "progenitor": ">=2026.10.162",
  "compatibility": { "peers": ">=1.0.0 <2.0.0" }
}
```

| Field | Comes from | Meaning |
|---|---|---|
| `id` | `[BepInPlugin]` GUID | The plugin Progenitor installs and names the file after. |
| `version` | `[BepInPlugin]` version | Must equal `version_number` in `manifest.json`. |
| `side` | `[ProgenitorSide]` (`both` when absent) | `client`, `server` or `both`. Decides where the Install tab offers it. |
| `hotLoadable` | Your choice (`-HotLoadable`) | True when the mod obeys the [unload rules](modules.md#ownership-and-pin-constraints) and can load without a restart. Install Mods hot loads a mod only when this is true. The generator applies the Install tab's own image rule and refuses it unless the DLL has exactly one plugin deriving from `ProgenitorPlugin`, resolvable references, and no `ctx.UnsafeHarmony`, HarmonyLib, IL2CPP type registration, P/Invoke or function-pointer calls, or reflection escapes (`Activator`, `Type.GetType`, `MakeGenericType`, `MakeGenericMethod`, `Assembly.Load*`, `System.Reflection.Emit`). |
| `sha256` | The DLL bytes | A rebuilt DLL needs a regenerated file. |
| `progenitor` | The Progenitor Core version the DLL was compiled against | The Progenitor releases the mod needs, as a range: `>=` that version. The Progenitor Index reads it; it replaces a manifest dependency on Progenitor ([versions](#6-versions-and-compatibility)). |
| `compatibility.peers` | `[NetworkCompatibility(Peers = …)]` | Only when declared. Progenitor refuses a package whose two values disagree. |

The Index accepts this file as it is: it allows fields it does not read ([its `progenitor.json` rules](https://github.com/VRising-Modders/ProgenitorIndex/blob/main/CONTRIBUTING.md#progenitorjson-optional)).

## 2. Dependencies

```json
"dependencies": [
  "BepInEx-BepInExPack_V_Rising-1.733.2"
]
```

- **BepInExPack.** Required. Use the pack version you test with.
- **Progenitor: do not list it.** Progenitor is not a package on Thunderstore or on the Index; players install it from its GitHub releases, and your README tells them how ([requirement block](#readme-requirement-block)). `publish-check` fails a manifest that depends on any `*-Progenitor-*` or `*-Progenitor_VRising-*` package, in any case. The Progenitor release the mod needs goes in `progenitor.json`'s `progenitor` range instead.
- **Other mods** use the `Owner-Name-Version` form. The Index resolves each one from its own entries first, then from Thunderstore ([Dependencies](https://github.com/VRising-Modders/ProgenitorIndex/blob/main/CONTRIBUTING.md#dependencies)).
- **In the plugin**, declare the Progenitor dependency so BepInEx and Progenitor order the load:

  ```csharp
  [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
  [BepInDependency("progenitor")]
  [ProgenitorSide(Side.Client)]
  public sealed class Plugin : ProgenitorPlugin<MyContext> { … }
  ```

  A plain BepInEx mod that only uses Progenitor when it is present uses `[BepInDependency("progenitor", BepInDependency.DependencyFlags.SoftDependency)]`, no `progenitor.json`, and the second README variant below. Progenitor's Install tab then installs it as an ordinary BepInEx plugin, at the next start.

## 3. The standard Progenitor message

Say the same thing the same way everywhere, so players recognize it.

**Description:** `<Side> · Requires Progenitor. <one-sentence pitch>`

```text
Client-only · Requires Progenitor. Shows your castle's upkeep on the HUD.
Server-only · Requires Progenitor. Weekly boss rush with a leaderboard.
Client and server · Requires Progenitor. Trade items with other players through a market stall.
```

<a id="readme-requirement-block"></a>
**README requirement block.** Players install Progenitor from its GitHub releases and then install your mod from Progenitor's Mods menu. Paste this near the top of `README.md`, with your mod's name in the last line (the [mod template](../templates/progenitor-mod/README.md) already has it):

Every command sits alone in its own code block, never inside a sentence, list item or table cell, so the line itself is easy to select. Use a plain fence (three backticks, no `powershell` label): some viewers print the label inside the block, players copy it with the command, and the command then fails.

````markdown
> **Requires Progenitor.** This mod is built on [Progenitor](https://github.com/VRising-Modders/Progenitor), a modding framework for V Rising. Install Progenitor first, then install this mod from Progenitor's Mods menu.

**Installing Progenitor** ([step by step](https://vrising-modders.github.io/Progenitor/guide/progenitor-guide.html#players/install)):

**With the installer.** Quit the game or stop your server, open **PowerShell** from the Start menu, paste this line and press Enter:

```
irm https://vrising-modders.github.io/Progenitor/install.ps1 | iex
```

It asks where to install: press Enter for the place it found, or paste the folder that holds `VRising.exe` or `VRisingServer.exe` ([the script is public](https://vrising-modders.github.io/Progenitor/install.ps1)).

**By hand.** Install [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/), then download `Progenitor-<version>.zip` from the [latest release](https://github.com/VRising-Modders/Progenitor/releases/latest) and extract it into the folder that holds `VRising.exe`. For a dedicated server, extract the same zip into the folder that holds `VRisingServer.exe`.

**Installing MyMod.** Start the game, open **Mods**, then **Install Mods**, search for MyMod and click **Install**.
````

For a mod that only uses Progenitor when it is there:

```markdown
> **Works better with Progenitor.** This mod runs on plain BepInEx. With [Progenitor](https://github.com/VRising-Modders/Progenitor) installed it also gets <what it adds: in-game settings, remappable keys, …>. [Install Progenitor](https://vrising-modders.github.io/Progenitor/guide/progenitor-guide.html#players/install).
```

**Wording to avoid:** "official", "verified" or "approved" next to Progenitor. A listing on the Index means its maintainers reviewed that version, not that Progenitor endorses the mod; write "built on Progenitor" or "requires Progenitor".

## 4. The Progenitor stamp (optional)

Authors who want to may add the Progenitor stamp to their icon. It is opt-in.

- **Placement.** On the 256×256 icon, a **64×64 px** stamp with its top-left pixel at **(184, 184)**: it covers x 184–247 and y 184–247, leaving 8 px to the right and bottom edges.
- **Keep the corner free.** Keep the 72×72 corner region (x 184–255, y 184–255) free of important art and text.
- **Paste it as is.** Place the stamp PNG unscaled (no resampling) with its alpha channel. Do not recolor, rotate or crop it.
- **Why that size.** Mod lists show icons small; the stamp is drawn to read at that size.
- **The file** is [`branding/progenitor-stamp-64.png`](../branding/progenitor-stamp-64.png) in the Progenitor repository (it will also ship with Progenitor's releases). Use the 64×64 file on icons; the 512×512 copy next to it is for large previews only. See [branding](../branding/README.md).

Pillow:

```python
from PIL import Image

icon = Image.open("icon.png").convert("RGBA")
stamp = Image.open("progenitor-stamp-64.png").convert("RGBA")
assert icon.size == (256, 256) and stamp.size == (64, 64)
icon.alpha_composite(stamp, dest=(184, 184))   # no resize: pixel for pixel
icon.save("icon.png")
```

ImageMagick 7:

```sh
magick icon.png progenitor-stamp-64.png -geometry +184+184 -compose over -composite icon.png
```

## 5. Declare settings and keybindings properly

Progenitor gives every mod a page in the in-game **Mods** menu, a **Keybinds** tab where players remap keys, and conflict detection between mods. Those work only for settings and keys declared through `ctx`.

**Settings.** Bind every user setting with `ctx.Bind` (or `ctx.Config.Bind` for a purely local one), with a description and a default. Use `synced: true` when the server should decide the value. Then lay the settings out with `ctx.Settings.Page`:

```csharp
protected override void OnLoad(MyContext ctx)
{
    var showHud = ctx.Bind("Display", "ShowUpkeep", true, "Show the upkeep line on the HUD.");
    var rate = ctx.Bind("Server", "UpkeepRate", 1.0f, "Upkeep multiplier for everyone.", synced: true,
        acceptableValues: new AcceptableValueRange<float>(0.5f, 3f));

    ctx.Settings.Page(page => page
        .Section("Display")
            .Toggle(showHud)
        .Section("Server")
            .Slider(rate));
}
```

A synced entry is a server-wide setting by itself: the page draws it under **Server settings**, read-only while the server's value is in force. A `scope:` argument is not needed for it ([settings scopes](mod-settings.md#settings-scopes-on-the-page-step-4)).

**Keys.** Register every key with `ctx.Keybinds`. Never poll `UnityEngine.Input`, `Keyboard.current`, `Mouse.current`, `Gamepad.current` or the game's input state ([Keybinds](modules.md#keybinds-fui5)).

```csharp
ctx.Keybinds.Register("ToggleUpkeep", new KeyChord(UiKey.F7), "Show or hide the upkeep line.", () => UpkeepHud.Toggle());
```

**Side.** Declare `[ProgenitorSide]`. A mod that declares nothing runs on both sides.

What goes wrong otherwise (source-checked):

| Shortcut | Result |
|---|---|
| Settings bound, but no `ctx.Settings`, `ctx.Menu` or `ctx.Keybinds` call | The cfg is never registered with the Mods menu. A mod installed by Progenitor shows no settings; a mod-manager install shows them read-only, as another mod's. |
| BepInEx `Config.Bind` directly | The setting cannot be synced. `ctx.Config.Bind(..., synced: true)` throws; only `ctx.Bind(..., synced: true)` registers the sync, so the server's value never locks the client's. |
| `new ConfigFile(...)` of your own | Settings in that file never appear in the Mods menu. |
| Keys read from the input devices | Not listed in the Keybinds tab, cannot be remapped, take no part in conflict resolution. When a `ProgenitorPlugin`'s own DLL references such a read, Progenitor logs one warning at load and shows a `direct-key-read` note on its Mods page; helper DLLs the mod ships are not checked at load. |

Describe settings and default keys in the README:

```markdown
## Settings

| Setting | Default | Who decides | What it does |
|---|---|---|---|
| Display / Show upkeep | On | Each player | Show the upkeep line on the HUD. |
| Server / Upkeep rate | 1.0 (0.5 to 3) | The server | Upkeep multiplier for everyone. |

## Keys

| Action | Default key | Remap in |
|---|---|---|
| Show or hide the upkeep line | F7 | Mods → Keybinds |
```

Checklist:

- [ ] Every setting is bound through `ctx.Bind` / `ctx.Config.Bind` with a description and a default.
- [ ] Server-decided settings use `synced: true`.
- [ ] The mod calls `ctx.Settings.Page` (or registers a key or menu entry), so its settings appear.
- [ ] Every key is registered with `ctx.Keybinds`; nothing polls input devices.
- [ ] `[ProgenitorSide]` is declared.
- [ ] The README has the settings and keys tables.

## 6. Versions and compatibility

- **Semantic versions.** `major.minor.patch`, one string in `[BepInPlugin]`, `manifest.json` and `progenitor.json`. Follow the [compatibility contract](mod-compatibility.md#the-compatibility-contract): patch changes nothing on the wire, minor only adds, major removes or changes.
- **Minimum Progenitor version.** `progenitor.json`'s `progenitor` range says which Progenitor releases the mod needs; the generator writes `>=` the Core version the DLL was compiled against. Building against the oldest Progenitor release that provides every API you call keeps the range wide: a newer one forces every player to update Progenitor before they can install your mod. Progenitor versions are dates (`2026.10.162`), see [versioning.md](versioning.md). `publish-check` warns when the range is missing or its lower bound differs from the compiled Core.
- **Declare `Peers`** in `[NetworkCompatibility]` when the mod talks to itself across the network (client and server parts, `ctx.Messages`) and you know which other versions it works with: `Peers = ">=1.2.0 <2.0.0"`. Without it, the attribute's `NetworkVersionRule` decides (`Exact`: that version only; `None`: any version); with no rule declared either, the default is the same minor version. The generator copies `Peers` into `progenitor.json`.
- **Keep message fixtures.** At each release record what the mod sends and check the fixtures in; the next release's tests replay them with `Progenitor.Testing` and `MessageCompat.CheckRange`. Step by step: [tutorial 6c](tutorial.md#6c-make-your-mod-work-across-versions); reference: [modules.md](modules.md#testing-your-mod-across-versions-mod-compatibility-c5).

## 7. Check the package: `tools/publish-check.ps1`

Run it on the finished zip (or the folder you zip) before every release:

```powershell
powershell -File tools/publish-check.ps1 -Package out\MyMod-1.2.0.zip
powershell -File tools/publish-check.ps1 -Package out\MyMod-1.2.0.zip -Json
```

It builds `tools/publishcheck` (a .NET 8 tool; no game install needed) and prints one `PASS`, `WARN` or `FAIL` line per check. Exit code 1 means a check failed. It reads files and DLL metadata only: it loads no assembly and contacts nothing.

| Check | Fails when | Warns when |
|---|---|---|
| `files.*` | `manifest.json`, `README.md` or `icon.png` is missing | `CHANGELOG.md` or `LICENSE` is missing |
| `files.dev` | Progenitor dev bridge files ship (`Progenitor.Dev*`, `*.token`, `progenitor-dev.enable`) | |
| `files.framework` | Progenitor's own DLLs are bundled | |
| `manifest.bom` | `manifest.json` has a BOM | |
| `manifest.fields`, `.name`, `.version`, `.website` | A required field is missing; the name has other than letters, digits and `_`; the version is not three numbers; `website_url` is not http(s) | The name is not PascalCase; `website_url` is empty |
| `manifest.description`, `.side` | Over 250 characters; no side prefix, or one that contradicts the mod's side | No "Requires Progenitor." (`manifest.message`); "official", "verified" or "approved" (`manifest.wording`) |
| `deps.bepinex`, `deps.format` | No BepInExPack dependency; a dependency not in `Owner-Package-1.2.3` form (the Index's rule: the owner may contain hyphens, such as `VRising-Modders`; the package name is the last part before the version) | |
| `deps.progenitor` | The manifest depends on a `Progenitor` or `Progenitor_VRising` package, in any case (Progenitor is not a package; players install it from GitHub) | |
| `pj.progenitor` | The `progenitor` range is not a string, or not a range the Index accepts | No range; its `>=` lower bound is older or newer than the Core the DLL was compiled against |
| `icon` | Not a 256×256 PNG | |
| `readme.requirement`, `readme.wording` | A mod that needs Progenitor has no "Requires Progenitor" block; a mod that soft-depends on it has no "Works better with Progenitor" block | No block at all for a mod with no Progenitor dependency; endorsement words next to Progenitor |
| `pj.*` | `progenitor.json` is missing (unless the dependency is soft) or not at the root; a field is missing; `id` is not the plugin's GUID or is not a GUID Progenitor can install (letters, digits, `.`, `_`, `-`); `version`, `side` or `compatibility.peers` disagrees with the DLL or the manifest; `compatibility` is not an object or `peers` not a string; `sha256` matches no DLL; `hotLoadable` is true for an image the Install tab would not load hot (the same rule, shared code) | `progenitor.json` has a BOM; `sha256` is another DLL's, not the plugin's |
| `dll.plugin`, `dll.layout` | No DLL declares `[BepInPlugin]`; the DLL is under `BepInEx/plugins`, `patchers`, `core` or `config` | Several plugin DLLs (the one `progenitor.json` names is checked); the DLL is in another subfolder |
| `dll.bepindependency` | The DLL references Progenitor without `[BepInDependency("progenitor")]` | |
| `dll.keys`, `dll.keys-libraries` | The mod's plugin DLL reads keys or devices directly | A bundled library (a DLL with no `[BepInPlugin]`) does |
| `dll.config`, `dll.configfile`, `dll.settings-page` | | Direct `ConfigFile.Bind`; its own `ConfigFile`; settings bound without `ctx.Settings`, `ctx.Menu` or `ctx.Keybinds` |

The DLL scan reports references, not calls that run: dead code counts too. It covers every DLL in the package, not other files the mod loads. Key reads fail only in the mod's own plugin DLL: a third-party library you bundle may read devices for its own reasons, so it only warns.

`publish-check` checks Progenitor's rules. The Index's own checks (the release, the fingerprint, dependencies, contents and the entry itself) run in the Index's pull request with its `tools/validate.mjs` ([Checks](https://github.com/VRising-Modders/ProgenitorIndex/blob/main/CONTRIBUTING.md#checks)); run that too before you open the pull request.

Run it from a Progenitor checkout; it is not yet shipped with Progenitor's release.

## 8. Publish: a GitHub release and an Index entry

1. **Build Release** and regenerate `progenitor.json` from that exact DLL.
2. **Run `tools/publish-check.ps1`** until nothing fails.
3. **Test the real install.** Serve the zip from a local index (`[Install] LocalIndexPath`, see `tools/make-dummy-package.ps1`), install it from **Install Mods**, and join a test server with it.
4. **Create a GitHub release** in the mod's repository, from the commit you built, with the zip attached. Write its notes from `CHANGELOG.md`.
5. **Add the Index entry.** Fork [ProgenitorIndex](https://github.com/VRising-Modders/ProgenitorIndex) and add `entries/<YourAccount>/<Name>/<version>.json` pointing at the release asset. In your fork, generate its meta (and lock any new Thunderstore dependency):

   ```
   node tools/validate.mjs --update-lock --write-meta entries/<YourAccount>/<Name>/<version>.json
   ```

   Then open a pull request. The entry format, the walkthrough and what reviewers ask about are in its [CONTRIBUTING.md](https://github.com/VRising-Modders/ProgenitorIndex/blob/main/CONTRIBUTING.md).
6. **Credit forks.** If the mod is a fork or contains others' work, credit them in the README as their licenses require.

Release discipline:

- **One play-tested release per batch of changes.** Each version is another review on the Index and another update for players.
- **A listed version cannot be edited.** The entry pins the exact file, so a replaced release asset simply stops installing. Even a README typo costs a new version and a new entry, so check everything first.
- **Keep the changelog current**, newest version first.

## 9. License

Progenitor is licensed LGPL-2.1-only. A mod that only references Progenitor, without copying or modifying its code, may use any license you choose. This is not legal advice.

## 10. FAQ

**Why aren't Progenitor mods on Thunderstore?**
Thunderstore doesn't list mod managers, and managing mods (installing, updating, hot loading, and joining servers with the right mods) is a core part of Progenitor. So Progenitor ships from its GitHub releases, the mods built on it are listed in the [Progenitor Index](https://github.com/VRising-Modders/ProgenitorIndex), and players install them from the Mods menu. Thunderstore is still the place for BepInEx and for mods that don't use Progenitor.

**I want my mod on Thunderstore.**
Progenitor is licensed under the [LGPL 2.1](../LICENSE) (`LGPL-2.1-only`), so you can use or adapt the parts you need in your own mod and publish it on Thunderstore without Progenitor as a dependency. If your mod includes code copied or adapted from Progenitor, release that mod under the LGPL-2.1, with its source, the license text and Progenitor's copyright notices. Alternatively, keep the adapted code in its own LGPL DLL that your mod references; your mod can then keep its own license. Your mod won't get Progenitor's features, such as hot load and the Mods menu. This is not legal advice.
