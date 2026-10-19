# Progenitor

![Progenitor](https://raw.githubusercontent.com/VRising-Modders/Progenitor/main/packaging/listing/00-primary.webp)

**A mod manager and modding framework for V Rising.** Install, update and turn off mods from an in-game **Mods** menu, keep every mod's settings and keys in one place, and join modded servers without hunting for the right versions. Mod authors get a framework with hot reload: rebuild a mod and the running game picks it up.

Progenitor needs only [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/). It works in the game and on dedicated servers, and on private games you host from the game.

![Three things in one](https://raw.githubusercontent.com/VRising-Modders/Progenitor/main/packaging/listing/01-three-in-one.webp)

## For players

- **The Mods menu.** Open **Mods** on the title screen or in the Escape menu. Every installed mod is listed with its status, settings and help page.
- **Install mods from inside the game.** The **Install Mods** tab lists the V Rising mods on Thunderstore. Click **Install**; some mods load at once, others after a restart.
- **Join modded servers.** When a server needs mods you don't have, Progenitor offers to install the exact versions it runs.
- **Every mod's settings and keys in one place,** with a conflict check when two mods want the same key.
- **Built-in extras:** a better chat box (tabs, history, links, mentions), a build menu with search, a **Recent** list and **Copy building** (middle-click a building to build another), **Dye all**, photo mode, and back and forward through menus.
- **Collect logs** in one click when something goes wrong. Personal details are removed before the zip is written.

![Install Mods](https://raw.githubusercontent.com/VRising-Modders/Progenitor/main/packaging/listing/03-install-mods.webp)

![Keybinds](https://raw.githubusercontent.com/VRising-Modders/Progenitor/main/packaging/listing/05-keybinds.webp)

## For server admins

- **The Server tab:** change server-wide mod settings in the game, choose which mods players need, and set join rules.
- **Install and update server mods** from the same menu, with the server's mods shown next to yours.
- **Backups and restores, scheduled wipes and chat moderation** (word filter, Discord relay).
- **Save safety:** a world saved with a content mod still loads after that mod is removed.

![Server tab](https://raw.githubusercontent.com/VRising-Modders/Progenitor/main/packaging/listing/07-server-admin.webp)

## For mod authors

- **Hot reload.** Rebuild your mod and it swaps into the running game, with a player connected. No restart.
- **One context, automatic cleanup.** Register items, buffs, commands, events, settings, keybinds, windows and network messages through your plugin's context; unloading reverses all of it.
- **The same code** on dedicated servers and on games hosted from the client.
- **AI-friendly.** An [MCP server](https://github.com/VRising-Modders/Progenitor/blob/main/docs/mcp.md) connects a coding assistant to your game client and to your own test server, so it can build and deploy your mod, hot reload it, run commands, read the logs and inspect the running game on both sides. A [starter prompt](https://github.com/VRising-Modders/Progenitor/blob/main/docs/ai-starter-prompt.md), an `llms.txt` index and knowledge notes help it work with the framework. The MCP server and its development bridge come from the repository; they are not in this package and never belong on a public server.

Start with the [Help Guide's mod developer chapters](https://vrising-modders.github.io/Progenitor/guide/progenitor-guide.html), `PROGENITOR_GUIDE.md` (included in this package), the `dotnet new progenitor-mod` template and the [tutorial](https://github.com/VRising-Modders/Progenitor/blob/main/docs/tutorial.md). Mods built on Progenitor depend on `VRising_Modders-Progenitor`.

![Hot reload](https://raw.githubusercontent.com/VRising-Modders/Progenitor/main/packaging/listing/06-hot-reload.webp)

## Install

1. Install [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) into the folder that holds `VRising.exe` (for a dedicated server, the folder that holds `VRisingServer.exe`).
2. Quit the game (or stop the server).
3. Download this package (**Manual Download**), open the zip and copy its `BepInEx` folder into the same game folder. Let it replace files when you update.
4. Start the game. **Mods** appears on the title screen.

The same files work in the game and on a dedicated server. Progenitor adds `BepInEx/plugins/Progenitor.Bootstrap.dll`, `BepInEx/patchers/Progenitor.Preloader.dll` and the `BepInEx/progenitor-core` folder. To remove it, use **Remove Progenitor from this PC** in the Mods menu, or delete those three items while the game is closed.

## Help

- The **Help Guide** opens from the Mods menu. It covers players, server admins and mod authors. You can also [read it online](https://vrising-modders.github.io/Progenitor/guide/progenitor-guide.html) before you install.
- Something wrong? **Mods** → **Progenitor** → **Get help** → **Collect logs**, then post the zip in the [Progenitor channel](https://discord.com/channels/978094827830915092/1112888087836631090) on the V Rising Mod Community Discord ([join it](https://vrisingmods.com/discord) first) or [open a bug report](https://github.com/VRising-Modders/Progenitor/issues/new?template=bug-report.yml).

## About

Progenitor is in active development. Its API covers much of what a mod needs, not yet everything; missing pieces are welcome as feedback or as pull requests. Original code under the LGPL-2.1 license; its only dependency is BepInEx. Source, documentation and issues: [github.com/VRising-Modders/Progenitor](https://github.com/VRising-Modders/Progenitor).
