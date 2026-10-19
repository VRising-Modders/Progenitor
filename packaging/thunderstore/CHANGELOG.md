# Changelog

## First public release

Progenitor's first package on Thunderstore.

**Players**

- The **Mods** menu on the title screen and in the Escape menu: installed mods with their status, settings, keys and help pages.
- **Install Mods**: install, update and turn off V Rising mods from Thunderstore inside the game. Simple and Advanced views; Advanced shows your mods and the server's side by side.
- Joining a modded server offers to install the exact mod versions it runs.
- **Keybinds** for every mod in one tab, with a conflict check.
- Built-in features: a better chat box, build menu search with a Recent list and Copy building, Dye all, photo mode, back and forward through menus.
- **Get help** → **Collect logs**, with personal details removed.
- The Help Guide for players, server admins and mod authors, opened from the Mods menu.

**Server admins**

- The **Server** tab: server-wide mod settings, required and recommended mods, join rules, server listing text and locked settings.
- Server mod installs and updates, backups and restores, scheduled wipes, chat moderation.
- Save safety: a world saved with a content mod still loads after that mod is removed.

**Mod authors**

- `ProgenitorPlugin<TContext>` with automatic cleanup of everything registered on the context.
- Hot reload of mods and of Progenitor's own Core.
- Items, buffs, recipes, prefab edits, commands, events, settings with server sync, keybinds, windows, networking, persistence and more. Status of each API: [api.md](https://github.com/VRising-Modders/Progenitor/blob/main/docs/api.md).
- The `dotnet new progenitor-mod` template and `PROGENITOR_GUIDE.md`.
