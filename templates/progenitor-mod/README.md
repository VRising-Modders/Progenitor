# Progenitor Mod Display Name

> **Requires Progenitor.** This mod is built on [Progenitor](https://github.com/VRising-Modders/Progenitor), a modding framework for V Rising. Install Progenitor first; its Install Mods tab can then install this mod.

**Installing Progenitor** ([step by step](https://vrising-modders.github.io/Progenitor/guide/progenitor-guide.html#players/install)):

**With the installer.** Quit the game, open **PowerShell** from the Start menu, paste this line and press Enter:

```powershell
irm https://vrising-modders.github.io/Progenitor/install.ps1 | iex
```

It finds the game through Steam and installs BepInEx too when it is missing. The script is public; [read it first](https://vrising-modders.github.io/Progenitor/install.ps1).

For a dedicated server, stop the server and use this line instead:

```powershell
& ([scriptblock]::Create((irm https://vrising-modders.github.io/Progenitor/install.ps1))) -Server
```

**By hand.** Install [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/), then download `Progenitor-<version>.zip` from the [latest release](https://github.com/VRising-Modders/Progenitor/releases/latest) and extract it into the folder that holds `VRising.exe`. For a dedicated server, extract the same zip into the folder that holds `VRisingServer.exe`.

Asks the server for its greeting and shows it in a window and a notice.

Install it on the client and the server. On a server without it, the window says so and the rest of the mod keeps working.

## Settings

| Setting | Default | Who decides | What it does |
|---|---|---|---|
| Display / Show notices | On | Each player | Show this mod's notices at the top of the screen. |
| Server / Greeting | Welcome to the server! | The server (admins edit it in game) | What the server answers when a player asks. |

## Keys

| Action | Default key | Remap in |
|---|---|---|
| Open or close the window | Ctrl+F8 | Mods → Keybinds |

## Recommended mods

None. List mods that work well with this one here, and ask server admins to add them to the server's recommended list.
