# Publishing a Progenitor mod on Thunderstore

Guidance for authors who build a V Rising mod on Progenitor and publish it on Thunderstore. These are suggestions from the people who maintain the framework, not rules Thunderstore enforces. Where something is a hard requirement (Thunderstore's or Progenitor's), this page says so.

**Status.** The package rules below are **source-checked** against Progenitor's package inspection and install planner. The checker, `tools/publish-check.ps1`, is **game-free tested** (`tests/Progenitor.Tests/PublishCheckTests.cs`). No package built from this page has been published or installed by a mod manager in a test.

**Contents**

1. [Package layout](#1-package-layout)
2. [Dependencies](#2-dependencies)
3. [The standard Progenitor message](#3-the-standard-progenitor-message)
4. [The Progenitor stamp (optional)](#4-the-progenitor-stamp-optional)
5. [Declare settings and keybindings properly](#5-declare-settings-and-keybindings-properly)
6. [Versions and compatibility](#6-versions-and-compatibility)
7. [Check the package: `tools/publish-check.ps1`](#7-check-the-package-toolspublish-checkps1)
8. [Pre-publish checklist and release discipline](#8-pre-publish-checklist-and-release-discipline)
9. [License](#9-license)

## 1. Package layout

A package is a zip whose root holds:

| File | Required by | Notes |
|---|---|---|
| `manifest.json` | Thunderstore | UTF-8 **without** a byte-order mark. A BOM gets the upload rejected. |
| `README.md` | Thunderstore | The listing page. Include the [requirement block](#readme-requirement-block). |
| `icon.png` | Thunderstore | PNG, exactly 256×256. |
| `CHANGELOG.md` | Recommended | One section per version, newest first. |
| `LICENSE` | Recommended | Your mod's license text. |
| `MyMod.dll` | Your mod | The plugin DLL, **at the zip root**. |
| `progenitor.json` | Progenitor | Generated from the DLL; never written by hand ([below](#progenitorjson)). |

### `manifest.json`

```json
{
  "name": "MyMod",
  "version_number": "1.2.0",
  "website_url": "https://github.com/your-team/MyMod",
  "description": "Client-only · Requires Progenitor. Shows your castle's upkeep on the HUD.",
  "dependencies": [
    "BepInEx-BepInExPack_V_Rising-1.733.2",
    "VRising_Modders-Progenitor-2026.10.1"
  ]
}
```

- **`name`**: PascalCase, letters and digits (`MyMod`). Thunderstore allows `_` and shows it as a space; a hyphen is not allowed. Your team name is not part of it: Thunderstore adds it (`YourTeam-MyMod`).
- **`version_number`**: three numbers, `major.minor.patch`. The same string goes in `[BepInPlugin]` and `progenitor.json` ([versions](#6-versions-and-compatibility)).
- **`website_url`**: the mod's public source. Players and server operators use it to see what they are installing, and players' log zips list it in `mods.md` for AI assistants. An `llms.txt` at the repository root (or a README section) tells an assistant what the mod does and where to report bugs.
- **`description`**: at most 250 characters, starting with the side: `Client-only`, `Server-only` or `Client and server`, in the [standard form](#3-the-standard-progenitor-message).
- **`dependencies`**: see [Dependencies](#2-dependencies).

Write the file as UTF-8 without a BOM. In Windows PowerShell 5.1, `Set-Content -Encoding UTF8` and `Out-File` add one; use `[IO.File]::WriteAllText($path, $json)`, which does not.

### Where the DLL goes, and what each install route does

Put the DLL at the zip root, beside `manifest.json`. That is the only layout both install routes accept:

| Installed by | Where the DLL lands | Loaded by | Hot reload |
|---|---|---|---|
| A mod manager (r2modman, the Thunderstore App) | `BepInEx/plugins/<YourTeam-MyMod>/MyMod.dll` | The BepInEx chainloader, at game start | **No.** Updates need a game restart. |
| Progenitor's **Install** tab or join screens | `BepInEx/progenitor-reload/<plugin id>.dll` | Progenitor's reloader | Yes, when `progenitor.json` declares `hotLoadable` and the image passes the Install tab's checks ([trusted hotLoadable](mods-menu.md#hotdemo-hot-install-and-uninstall-trusted-hotloadable-hotdemo1)); otherwise it loads at the next start. |

What this means for a mod-manager install (source-checked; [mods-menu.md](mods-menu.md#ordinary-plugins-in-the-thunderstore-layout-instplug1), [mod-settings.md](mod-settings.md#ordinary-bepinex-plugins)):

- The mod runs normally, but as a restart-only Progenitor mod: no hot reload, and it is never paused for a server that does not run it ([hot-reload.md](hot-reload.md)).
- Progenitor shows it as **Managed by <tool>** when it recognizes the mod manager, otherwise **Managed elsewhere**. Its join screens and auto-update do not replace or keep versions of it; players update it in their mod manager.
- `[BepInDependency("progenitor")]` matters here: it makes the chainloader load Progenitor first.

Two layouts to avoid:

- **The DLL under `BepInEx/plugins/` inside the zip.** A package with a `progenitor.json` and this layout is refused by the Install tab (`Cannot install — the package layout targets BepInEx/plugins/, patchers/ or core/`).
- **Loose data files next to the DLL** (`localization/en.json`, a `.json` the mod reads). The Install tab places the DLL only. Register strings in code (`Localization.Register`) and keep data where the tutorial says ([Where should my data go?](tutorial.md#where-should-my-data-go)).

**Known limitation.** A mod manager cannot place a DLL in `BepInEx/progenitor-reload/`, so mod-manager installs never hot reload. V Rising's Thunderstore install rules route only `BepInEx/plugins`, `patchers`, `core`, `monomod` and `config`; a file in any other folder of the zip lands in `BepInEx/plugins/<package>/` with its folders dropped (observed with Gale 1.23.2 on 2026-10-19, and the same rule in r2modman's installer). A package that ships `BepInEx/progenitor-reload/MyMod.dll` therefore installs as an ordinary plugin.

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
  "compatibility": { "peers": ">=1.0.0 <2.0.0" }
}
```

| Field | Comes from | Meaning |
|---|---|---|
| `id` | `[BepInPlugin]` GUID | The plugin Progenitor installs and names the file after. |
| `version` | `[BepInPlugin]` version | Must equal `version_number` in `manifest.json`. |
| `side` | `[ProgenitorSide]` (`both` when absent) | `client`, `server` or `both`. Decides where the Install tab offers it. |
| `hotLoadable` | Your choice (`-HotLoadable`) | True when the mod obeys the [unload rules](modules.md#ownership-and-pin-constraints) and can load without a restart. The generator applies the Install tab's own image rule and refuses it unless the DLL has exactly one plugin deriving from `ProgenitorPlugin`, resolvable references, and no `ctx.UnsafeHarmony`, HarmonyLib, IL2CPP type registration, P/Invoke or function-pointer calls, or reflection escapes (`Activator`, `Type.GetType`, `MakeGenericType`, `MakeGenericMethod`, `Assembly.Load*`, `System.Reflection.Emit`). |
| `sha256` | The DLL bytes | A rebuilt DLL needs a regenerated file. |
| `compatibility.peers` | `[NetworkCompatibility(Peers = …)]` | Only when declared. Progenitor refuses a package whose two values disagree. |

## 2. Dependencies

```json
"dependencies": [
  "BepInEx-BepInExPack_V_Rising-1.733.2",
  "VRising_Modders-Progenitor-2026.10.1"
]
```

- **BepInExPack.** Use the pack version you test with.
- **Progenitor** is published as `VRising_Modders-Progenitor`. Depend on the **oldest** Progenitor release that provides every API you call, not the newest: pinning a newer one forces every player to update Progenitor before they can install your mod. Progenitor versions are dates (`2026.10.1`), see [versioning.md](versioning.md).
- **In the plugin**, declare the same dependency so BepInEx and Progenitor order the load:

  ```csharp
  [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
  [BepInDependency("progenitor")]
  [ProgenitorSide(Side.Client)]
  public sealed class Plugin : ProgenitorPlugin<MyContext> { … }
  ```

  A plain BepInEx mod that only uses Progenitor when it is present uses `[BepInDependency("progenitor", BepInDependency.DependencyFlags.SoftDependency)]`, no manifest dependency, no `progenitor.json`, and the second README variant below. Progenitor's Install tab then installs it as an ordinary BepInEx plugin, at the next start.

## 3. The standard Progenitor message

Say the same thing the same way everywhere, so players recognize it.

**Description:** `<Side> · Requires Progenitor. <one-sentence pitch>`

```text
Client-only · Requires Progenitor. Shows your castle's upkeep on the HUD.
Server-only · Requires Progenitor. Weekly boss rush with a leaderboard.
Client and server · Requires Progenitor. Trade items with other players through a market stall.
```

<a id="readme-requirement-block"></a>
**README requirement block.** Paste one near the top of `README.md`:

```markdown
> **Requires Progenitor.** This mod is built on [Progenitor](https://thunderstore.io/c/v-rising/p/VRising_Modders/Progenitor/), a modding framework for V Rising. Install Progenitor first; its Install Mods tab can then install this mod.
```

```markdown
> **Works better with Progenitor.** This mod runs on plain BepInEx. With [Progenitor](https://thunderstore.io/c/v-rising/p/VRising_Modders/Progenitor/) installed it also gets <what it adds: in-game settings, remappable keys, …>.
```

**Wording to avoid:** "official", "verified" or "approved" next to Progenitor. Progenitor does not review or endorse mods; write "built on Progenitor" or "requires Progenitor".

## 4. The Progenitor stamp (optional)

Authors who want to may add the Progenitor stamp to their icon. It is opt-in.

- **Placement.** On the 256×256 icon, a **64×64 px** stamp with its top-left pixel at **(184, 184)**: it covers x 184–247 and y 184–247, leaving 8 px to the right and bottom edges.
- **Keep the corner free.** Keep the 72×72 corner region (x 184–255, y 184–255) free of important art and text.
- **Paste it as is.** Place the stamp PNG unscaled (no resampling) with its alpha channel. Do not recolor, rotate or crop it.
- **Why that size.** Mod-manager listings show icons small; the stamp is drawn to read at that size.
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
- **Declare `Peers`** in `[NetworkCompatibility]` when the mod talks to itself across the network (client and server parts, `ctx.Messages`) and you know which other versions it works with: `Peers = ">=1.2.0 <2.0.0"`. Without it, the attribute's `NetworkVersionRule` decides (`Exact`: that version only; `None`: any version); with no rule declared either, the default is the same minor version. The generator copies `Peers` into `progenitor.json`.
- **Keep message fixtures.** At each release record what the mod sends and check the fixtures in; the next release's tests replay them with `Progenitor.Testing` and `MessageCompat.CheckRange`. Step by step: [tutorial 6c](tutorial.md#6c-make-your-mod-work-across-versions); reference: [modules.md](modules.md#testing-your-mod-across-versions-mod-compatibility-c5).

## 7. Check the package: `tools/publish-check.ps1`

Run it on the finished zip (or the folder you zip) before every upload:

```powershell
powershell -File tools/publish-check.ps1 -Package out\YourTeam-MyMod-1.2.0.zip
powershell -File tools/publish-check.ps1 -Package out\YourTeam-MyMod-1.2.0.zip -Json
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
| `deps.bepinex`, `deps.format` | No BepInExPack dependency; a dependency not in `Team-Package-1.2.3` form | |
| `deps.progenitor` | The DLL needs Progenitor but the manifest does not depend on it | The dependency is not `VRising_Modders-Progenitor` |
| `deps.progenitor-version` | | The dependency differs from the Progenitor Core version the DLL was compiled against (newer: depend on the oldest release that works; older: make sure the APIs exist there) |
| `icon` | Not a 256×256 PNG | |
| `readme.requirement`, `readme.wording` | A mod that needs Progenitor has no "Requires Progenitor" block; a mod that soft-depends on it has no "Works better with Progenitor" block | No block at all for a mod with no Progenitor dependency; endorsement words next to Progenitor |
| `pj.*` | `progenitor.json` is missing (unless the dependency is soft) or not at the root; a field is missing; `id` is not the plugin's GUID or is not a GUID Progenitor can install (letters, digits, `.`, `_`, `-`); `version`, `side` or `compatibility.peers` disagrees with the DLL or the manifest; `compatibility` is not an object or `peers` not a string; `sha256` matches no DLL; `hotLoadable` is true for an image the Install tab would not load hot (the same rule, shared code) | `progenitor.json` has a BOM; `sha256` is another DLL's, not the plugin's |
| `dll.plugin`, `dll.layout` | No DLL declares `[BepInPlugin]`; the DLL is under `BepInEx/plugins`, `patchers`, `core` or `config` | Several plugin DLLs (the one `progenitor.json` names is checked); the DLL is in another subfolder |
| `dll.bepindependency` | The DLL references Progenitor without `[BepInDependency("progenitor")]` | |
| `dll.keys`, `dll.keys-libraries` | The mod's plugin DLL reads keys or devices directly | A bundled library (a DLL with no `[BepInPlugin]`) does |
| `dll.config`, `dll.configfile`, `dll.settings-page` | | Direct `ConfigFile.Bind`; its own `ConfigFile`; settings bound without `ctx.Settings`, `ctx.Menu` or `ctx.Keybinds` |

The DLL scan reports references, not calls that run: dead code counts too. It covers every DLL in the package, not other files the mod loads. Key reads fail only in the mod's own plugin DLL: a third-party library you bundle may read devices for its own reasons, so it only warns.

Run it from a Progenitor checkout; it is not yet shipped with Progenitor's release.

## 8. Pre-publish checklist and release discipline

Before uploading:

1. **Build Release** and regenerate `progenitor.json` from that exact DLL.
2. **Run `tools/publish-check.ps1`** until nothing fails.
3. **Test the real install.** Serve the zip from a local index (`[Install] LocalIndexPath`, see `tools/make-dummy-package.ps1`) or the local Thunderstore stand-in ([test-rig.md](test-rig.md#local-thunderstore-dev-only)), install it from the **Install** tab, and join a test server with it. Then install it once with a mod manager from a local zip and start the game.
4. **Review the listing as a draft.** Read the description, README and icon as a player will see them before you submit.
5. **Credit forks.** If the mod is a fork or contains others' work, credit them in the README as their licenses require.

Release discipline:

- **One play-tested release per batch of changes.** Thunderstore notifies every follower of each version.
- **A published version cannot be edited.** Even a README typo costs a new version, so check everything first.
- **Keep the changelog current**, newest version first.

## 9. License

Progenitor is licensed LGPL-2.1-only. A mod that only references Progenitor, without copying or modifying its code, may use any license you choose. This is not legal advice.
