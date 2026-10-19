# Progenitor on Nexus Mods

The fields for Progenitor's Nexus Mods page. The description is [description.bbcode](description.bbcode); paste it into the description box. The Thunderstore package files are in [../thunderstore](../thunderstore).

## Page fields

| Field | Value |
|---|---|
| Game | V Rising |
| Name | Progenitor |
| Author | VRising Modders |
| Version | The release's version (`year.month.release`), the same number as on Thunderstore |
| Category | Miscellaneous (V Rising's Nexus categories are Gameplay, Miscellaneous and User Interface) |
| Brief overview | In-game mod manager and modding framework for V Rising. Install, update and set up mods from a Mods menu, join modded servers with the right versions, and hot reload for mod authors. Needs only BepInEx. |
| Language | English |
| Links (the page's link fields, if offered) | GitHub https://github.com/VRising-Modders/Progenitor, YouTube https://www.youtube.com/@ProgenitorMods, X https://x.com/ProgenitorMods |
| Tags | Mod manager, Framework, Utilities, Multiplayer, AI Assisted (code written with AI coding assistants), AI-generated media (the artwork) |
| Primary image | `00-primary.jpg` |
| Header image | `header-1920x480.jpg` (a 1400×400 copy is also made) |
| Gallery, in order | `01-three-in-one`, `02-mods-menu`, `03-install-mods`, `04-client-and-server`, `05-keybinds`, `06-hot-reload`, `07-server-admin`, `08-save-safety`, `09-dye-all`, `10-build-search`, `11-chat`, `12-collect-logs` |

The gallery images are 1920×1080 JPEGs from the Help Guide screenshots and the training videos; the hot-reload strip, the banner and the headers are composed from those frames and the Mods menu background.

## Requirements

| Requirement | Where | Note |
|---|---|---|
| BepInExPack V Rising | Off-site: <https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/> | Not on Nexus. Install it first. |

## Files

| File | Category | Description |
|---|---|---|
| `Progenitor-<version>.zip` (the manual install from `tools/package.ps1`) | Main file | Extract into the folder that holds VRising.exe or VRisingServer.exe. The same file works in the game and on a dedicated server. |

Do not upload the `-thunderstore.zip` to Nexus: it carries Thunderstore's root files next to the `BepInEx` folder.

## Permissions and credits

| Setting | Value |
|---|---|
| Upload to other sites | Allowed under the LGPL-2.1 license |
| Modify and reuse | Allowed under the LGPL-2.1 license; changes are welcome as pull requests on GitHub |
| Convert to other games | Allowed under the license |
| Use in mods sold for money | Not allowed |
| Credits | VRising Modders and Progenitor's contributors. BepInEx, Il2CppInterop and HarmonyX are listed in NOTICE. |

## Nexus rules to keep in mind

- Progenitor connects to the internet: the Mods menu reads Thunderstore's V Rising mod list and downloads the mods a player picks, and updates come the same way. Nexus allows internet access only where a mod needs it to work. Installing mods is Progenitor's purpose, and the description says so under **Internet use**.
- Mark the AI tags above; the description also says how AI was used.
- Keep the page hidden until the file is uploaded: Nexus does not allow public placeholder pages.
