# Progenitor

An AI-first modding framework for [V Rising](https://playvrising.com/), built as a [BepInEx](https://github.com/BepInEx/BepInEx) plugin. Original LGPL-2.1-only code, one runtime dependency (BepInEx), no code copied from other mod libraries.

> **Status: pre-release and experimental.** There is no published release yet. APIs can change between commits, and only the features marked *verified live* in [`docs/api.md`](docs/api.md) have been observed working in the game. Read the [roadmap](docs/roadmap.md) for what is done and what is planned.

**New here? Open [`docs/guide/progenitor-guide.html`](docs/guide/progenitor-guide.html)** in a browser: a short, interactive Help Guide for players, server admins and mod developers, with a glossary. It works offline from disk. Mods can ship a help page in the same look from [`templates/help`](templates/help/README.md).

## What it does

For players and server operators:

- **An in-game Mods menu** (title screen and Escape menu): installed mods, their settings and keybinds, installs and updates from Thunderstore, and one-click log collection for bug reports.
- **Settings and keybinds for every mod in one place**, with server-wide settings an admin can edit in game and remappable keys with a conflict rule.
- **Mod compatibility checks** when joining a server: the server names the mods it needs and the client offers to install the exact versions from Thunderstore.
- **Save safety**: a world saved with a content mod still loads after that mod is removed.

For mod authors:

- **Hot reload.** Rebuild a plugin, or Progenitor's own Core, and it swaps into the running game without a restart, with a player connected.
- **AI-first tooling.** A loopback debug bridge and an [MCP](https://modelcontextprotocol.io/) server let a coding agent build, reload, inspect and exercise a plugin inside the running game. [Knowledge notes](knowledge/README.md) record engine traps so the next modder or agent avoids them.
- **A plugin API with automatic cleanup.** A plugin registers through its context (`ctx`), and unloading reverses everything it registered: events, ECS system hooks, chat commands, cloned items and buffs, ordered prefab edits, config with server-to-client sync, persistence, localization, custom networking with a version handshake, castles, units, effects, keybinds, Harmony patches and UI.
- **The same code on dedicated and client-hosted servers.** A private game hosted from the client loads the same mods as a dedicated server, and server code never branches on which one it is.

## Install (players)

Progenitor is one Thunderstore package, tagged **Client + Server**, published as **`VRising_Modders-Progenitor`** once the first release is out. Until then there is nothing to download.

1. Install a Thunderstore mod manager such as [r2modman](https://thunderstore.io/c/v-rising/) or the Thunderstore App, and pick the **V Rising** community.
2. Install **Progenitor**. The mod manager also installs its dependency, **BepInExPack_V_Rising**.
3. Install the same profile for the game client and for a dedicated server. There is no separate server package.

**Manual install (one copy).** [BepInExPack_V_Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) must already be installed; Progenitor never ships or replaces BepInEx files.

1. Quit the game (or stop the server).
2. Download `Progenitor-<version>.zip` from the GitHub release.
3. Extract it into the folder that holds `VRising.exe` (for a dedicated server, the folder that holds `VRisingServer.exe`). Allow it to replace files when you update.

The zip holds a single `BepInEx` folder, so the extract adds `BepInEx/plugins/Progenitor.Bootstrap.dll`, `BepInEx/patchers/Progenitor.Preloader.dll` and `BepInEx/progenitor-core/` (Core, its address tables and the help guide) in one step, at the same paths as the Thunderstore package. It contains no other mod's files and no BepInEx files or settings. To remove Progenitor, use **Remove Progenitor from this PC** in the Mods menu, or delete those three items while the game is closed. The menu removal leaves `BepInEx/patchers/Progenitor.Preloader.dll`; on its own it needs no other Progenitor file: it keeps copies of the session logs and carries out any Mods-menu plugin request still pending, and you can delete it while the game is closed. **Never put `Progenitor.Dev.dll` on a public server**: it is the development bridge, and release packages do not include it.

### Reporting a problem

1. If the game crashed, start it again. Progenitor's preloader keeps the logs of the last three sessions, so the crashed session's log is still there.
2. Open **Mods** (title screen or Escape menu) → **Progenitor** → **Get help** → **Collect logs**, or click the zip icon in the title screen's bottom-left icon row. Progenitor saves one `vrising-mod-logs-client-<date>-<time>.zip` on your Desktop and selects it in a file window.
3. [Open a bug report](../../issues/new?template=bug-report.yml) and attach the zip, or post it in the **#technical-support** forum of the [V Rising Mod Community Discord](https://vrisingmods.com/discord).

The bundle holds BepInEx's log, the game's `Player.log`, a list of your mods and a few Progenitor settings files, plus the logs of a private game you host. SteamIDs, IP addresses, host names, Windows user and computer names, passwords and tokens are replaced before zipping, and player names Progenitor knows become `[player-1]` and so on. Names typed in chat or written by other mods' logs may remain, so look through the zip before posting it publicly. An admin of the server you are on can include that server's logs too; server admins can also type `.progenitor logs` in chat ([commands](docs/commands.md#progenitor-logs-server-logs)).

### Note for server operators

A world save records a mod's prefab GUID like a vanilla one, so a save written while a content mod was loaded normally cannot be loaded once that mod is removed, and the next autosave overwrites the good save. Progenitor's Bootstrap guards against that: on load it drops root entities whose prefab the current game cannot resolve, within a cap (`SaveSafety.MaxDropEntities` 5000, `MaxDropPercent` 0.5) that falls back to the vanilla failure rather than deleting a world. The switches are under `[SaveSafety]` in `BepInEx/config/progenitor.cfg`; `FailLoadInstead` restores the vanilla behavior.

## Quick start (mod authors)

**Start from the template and the guide.** [`PROGENITOR_GUIDE.md`](PROGENITOR_GUIDE.md) maps each need to its API and sample; [`templates/progenitor-mod`](templates/progenitor-mod) is a buildable starter mod (`dotnet new install templates/progenitor-mod`, then `dotnet new progenitor-mod -n MyMod`). Working with an AI assistant: paste the [starter prompt](docs/ai-starter-prompt.md); [`llms.txt`](llms.txt) indexes the repository for it.

1. Read the [tutorial](docs/tutorial.md): a progressive guide from a first item and command to keybinds, UI, units, patches and the hot-reload loop.
2. Start from the [minimal reload example](samples/Progenitor.ReloadExample/README.md), then the larger [`samples/Progenitor.Sample`](samples/Progenitor.Sample).
3. Look up each facade in the [module reference](docs/modules.md) and its status in [`docs/api.md`](docs/api.md).
4. When the mod is ready, follow [publishing a mod on Thunderstore](docs/publishing.md) and check the package with `tools/publish-check.ps1`.

A plugin references `progenitor` at runtime (`[BepInDependency]`) and `Progenitor.Core` at compile time (a project reference to `src/Progenitor.Core`; there is no NuGet package yet). Subclass `ProgenitorPlugin<TContext>`, keep the context constructor empty, and register work in `OnLoad`:

```csharp
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
[ProgenitorSide(Side.Both)]
public sealed class Plugin : ProgenitorPlugin<SampleContext>
{
    protected override void OnLoad(SampleContext ctx)
    {
        ctx.Content.DefineItem("sample.ember")
            .CloneOf(new PrefabGUID(-257494203))
            .MaxAmount(50)
            .Named("Ember Shard")
            .Described("A shard that still holds a little heat.")
            .Register();

        ctx.Handshake.DeclareSelf();

        if (ctx.IsServer)
        {
            ctx.Commands.Register(typeof(SampleCommands));
            ctx.Events.On<PlayerConnected>(OnPlayerConnected);
            ctx.Systems.Add("sample.users", OnUsersTick);
        }
    }
}

public sealed class SampleContext : PluginContext { }
```

Reloadable plugins live under `BepInEx/progenitor-reload/`; the [hot-reload guide](docs/hot-reload.md) covers deployment and how to confirm a swap completed.

### Context facades

`PluginContext` (`ctx`) is the per-plugin bag. Register through it so unload can reverse the work.

| Facade | Role |
|---|---|
| `Log` | This plugin's BepInEx log. |
| `Config` / `Bind` | Typed binds over `GUID.cfg`; `ctx.Bind(..., synced: true)` adds server→client sync after the handshake ([config sync](docs/config-sync.md)). |
| `Events` | Event-bus subscriptions (`WorldReady`, `PlayerConnected`, `PlayerDisconnected`, `ChatMessage`, `WorldLoaded`, …). |
| `Hooks` | ECS system before/after hooks ([ordering](docs/hooks-ordering.md)). |
| `Systems` | Named virtual systems (`ctx.Systems.Add`): callbacks, not injected `SystemBase`. |
| `Commands` / `Chat` | Chat commands (`[Command]`, prefix `.`) and server chat sends. |
| `Content` | Cloned item and buff definitions (`DefineItem` / `DefineBuff` → `Register`), recipes and ordered prefab editors (`Edit`). |
| `Castles` | Server castle plot, tile and building reads. |
| `Units` | Server unit spawn and steering (`Spawn`, `Follow`, `GoTo`, `Despawn`). |
| `Effects` | Client vanilla-asset borrow and server-authored networked beams ([effects](docs/effects.md)). |
| `Keybinds` | Remappable keybinds, listed in the Mods menu. Every key a mod acts on goes through this facade. |
| `Patches` | Tagged Harmony patches with an unload-safe unpatch. Prefer this over `UnsafeHarmony`, which opts the plugin out of hot reload. |
| `GodMode` | Mark entities the game's unguarded kill paths may not kill (server). |
| `WorldState` | The world-load epoch: stamp it beside any `Entity` kept past the current frame. |
| `Save` | Per-save JSON storage next to the world save. |
| `Net` / `Handshake` | Custom network events, and mod-version rows that kick a peer missing a required mod with a message. |
| `Track(IDisposable)` | Anything the wrappers do not cover. |

The UI toolkit is called statically (`Progenitor.Ui.Ui`) rather than through `ctx`; see the [UI style guide](docs/ui-style.md) and [UI toolkit](docs/ui.md). The full list, including side and status, is in [`docs/api.md`](docs/api.md).

## Building from source

Building the framework and the samples needs a local V Rising install, because they compile against the game's assemblies, which are never redistributed.

1. Install the **.NET 8 SDK**.
2. Install V Rising and **BepInExPack_V_Rising** (through a mod manager, or unpacked into the game folder).
3. **Launch the game once with BepInEx.** The first launch generates `<game>\BepInEx\interop`: managed interop assemblies (`ProjectM.dll`, `Unity.Entities.dll`, …) that Progenitor compiles against.
4. If the game is not in the default Steam location, set `VRISING_GAME_DIR` or create a git-ignored `Directory.Build.local.props`:

   ```xml
   <Project>
     <PropertyGroup>
       <VRisingGameDir>D:\Games\VRising</VRisingGameDir>
     </PropertyGroup>
   </Project>
   ```

5. Build from the repository root:

   ```
   dotnet build
   ```

If `BepInEx\interop\ProjectM.dll` is missing, the build stops with a named error.

**The tests need no game install.** The game-free test projects are what CI runs:

```
dotnet test tests/Progenitor.Tests
dotnet test tests/Progenitor.Testing.Tests
```

To test a plugin on both sides, host a private game from the client, or stage a throwaway dedicated server with `tools/stage-test-server.ps1` and run it with `tools/test-server.ps1`. `tools/deploy.ps1` builds and copies into both, and `tools/package.ps1` builds the Thunderstore zip. The [development loop](docs/dev-loop.md) describes all of it. See [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request.

## Documentation

| Topic | Pages |
|---|---|
| Getting started | [Mod authoring guide](PROGENITOR_GUIDE.md), [mod template](templates/progenitor-mod), [AI starter prompt](docs/ai-starter-prompt.md), [llms.txt](llms.txt), [Tutorial](docs/tutorial.md), [hot reload](docs/hot-reload.md), [glossary](docs/glossary.md), [basic authoring roadmap](docs/basic-mod-authoring.md) |
| API reference | [API status](docs/api.md), [module reference](docs/modules.md), [commands](docs/commands.md), [config sync](docs/config-sync.md), [hook ordering](docs/hooks-ordering.md), [input](docs/input.md) |
| Content | [Recipes](docs/recipes.md), [buffs](docs/buffs.md), [content files](docs/content-files.md), [content packs](docs/content-packs.md), [item removal](docs/item-removal.md), [edits on the wire](docs/edits-on-the-wire.md), [key table](docs/key-table.md), [boss AI](docs/ai.md) |
| Visuals and UI | [UI style guide](docs/ui-style.md), [UI toolkit](docs/ui.md), [effects](docs/effects.md), [wearables](docs/wearables.md), [character sheet](docs/character-sheet.md), [chat](docs/chat.md), [asset acquisition](docs/assets-acquisition.md), [player animation import](docs/player-animation-import.md), [player forms](docs/player-forms.md) |
| Mods menu and settings | [Mods menu](docs/mods-menu.md), [mod settings](docs/mod-settings.md), [settings organization](docs/settings-organization.md), [settings scopes](docs/settings-scopes.md) |
| Installing and compatibility | [Join flow](docs/join-flow.md), [mod manifest](docs/mod-manifest.md), [client mod policy](docs/client-mod-policy.md), [mod compatibility](docs/mod-compatibility.md), [package capabilities](docs/mod-package-capabilities.md), [mod deactivation](docs/mod-deactivation.md), [auto-update](docs/auto-update.md), [install test matrix](docs/install-test-matrix.md) |
| Servers | [World restore](docs/world-restore.md), [server list description](docs/server-list-description.md) |
| Publishing | [Publishing a mod](docs/publishing.md), [versioning](docs/versioning.md), [releasing Progenitor](docs/releasing.md) |
| Development tools | [Development loop](docs/dev-loop.md), [development bridge](docs/bridge.md), bridge operations ([core and entities](docs/bridge-ops.md), [players and world](docs/bridge-ops-player.md), [Mods workspace UI](docs/bridge-ops-ui.md), [join flow](docs/bridge-ops-join.md), [input and install](docs/bridge-ops-client.md)), [MCP tools](docs/mcp.md), [test rig](docs/test-rig.md), [watching the client](docs/observe-tool.md), [focus-free capture](docs/observe-capture.md), [asset research workflow](docs/asset-research-workflow.md), [catalog usage](docs/catalog-usage.md), [content pack scale tests](docs/content-pack-scale-tests.md), [player AI companion](docs/player-ai.md), [headless player bots (experimental, early development)](tools/bots/README.md) |
| Project | [Specification](docs/spec.html), [roadmap](docs/roadmap.md), [documentation rules](docs/documentation.md), [knowledge notes](knowledge/README.md) |

## Contributing and support

Contributions are welcome: read [CONTRIBUTING.md](CONTRIBUTING.md). AI coding agents read [AGENTS.md](AGENTS.md) and [CLAUDE.md](CLAUDE.md). Report security problems privately as described in [SECURITY.md](SECURITY.md). Everyone taking part follows the [code of conduct](CODE_OF_CONDUCT.md).

## License

GNU Lesser General Public License, version 2.1 only (`LGPL-2.1-only`). See [`LICENSE`](LICENSE). [`NOTICE`](NOTICE) lists the dependencies and the few borrowed ideas, with attribution. No third-party source code is copied into this repository.

V Rising is a trademark of Stunlock Studios. Progenitor is an unofficial community project and is not affiliated with or endorsed by Stunlock Studios.
