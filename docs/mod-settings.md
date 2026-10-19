# Mod settings on the mod page

Current scope grouping supersedes the earlier two-section layout below: **Your preferences**, **Your settings on this server**, and **Server settings**. Server-delivered rows need no local mod DLL and use the same controls. See [settings scopes](settings-scopes.md) for the current contract and qualification.

Roadmap M3.1a (job SETTINGS1). This page is the design of a mod's page in the [Mods workspace](mods-menu.md): the framework-owned status bar, the mod's own header area, and the mod's settings drawn on the page in sections. It covers all three stages; each section says what is delivered and what is planned.

**Status:** stages 1, 2 and 3 are **source-checked and game-free tested, not live-verified** (`tests/Progenitor.Tests/ModSettingsPageTests.cs`, `ModSettingsStage2Tests.cs`, `ModPageTests.cs`, `ModStage3Tests.cs`, `PluginFileActionsTests.cs`, full suite green). Nothing on this page has been observed in the game with the stage 2 or stage 3 code; neither [acceptance procedure](#acceptance-procedure-stages-1-and-2-not-run) ([stage 3](#acceptance-procedure-stage-3-not-run)) has been run. Planned, not in any stage yet: game UI sounds and transitions. Keyboard and controller navigation is NAV1 ([ui.md](ui.md#controller-and-keyboard-navigation-nav1); verified live at the title screen, not in a world).

## Decisions (2026-09-29)

- **Header ownership.** The top line of every mod page belongs to the framework and no mod can change it: an on/off switch, the status (`Enabled` / `Disabled` / `Paused for this server` / `Pending restart`), the version, the sides and, for a two-sided mod, whether its server part is running on this server. Below it each mod has its own header area. The default is the title (large) and `by <author> · <one-liner>`; a mod can fill or replace that area but has no way to reach the framework line.
- **Enabled is a status with a real switch.** The switch is implemented in stage 3. It must never be faked: until it works, it shows the real position and does nothing.
- **Controls.** Toggle, text (single and multi-line), number (stepper, min/max, units, step), slider (with exact entry), dropdown (automatic for enums and choice lists), radio group, multi-select, keybind, color, button/action (optional confirm), info line (live, read-only), section heading/divider. Later: list editor, duration, position/vector, link.
- **Common options.** Label, help/tooltip, reset-to-default with a modified marker, show-if/enable-if, needs-restart, needs-server-part (grayed with a reason and a fallback), scope local or server-set.
- **Layout.** Settings are on the mod page in sections (headings), label and help on the left, control right-aligned, a search field when there are more than about ten settings, and **Reset all**.
- **Redesign (after the live stage 1 page).** One solid status bar; a default header of title, muted by-line and one row of small buttons; a one-row settings header; small muted section headings; every setting one row of a three-column grid (name and help, fixed control column, reset gutter); display names derived from the cfg key; short choice lists as segmented pickers. See [Page design](#page-design-stage-2).

## Page design (stage 2)

Top to bottom:

1. **Status bar** (`page-framework`): one cell row drawn as a solid band (`CellRowSpec.Band`: an opaque fill in the existing `MenuPalette.RowHover`, no row click or hover tint), 48 px tall. Left to right: the switch drawn as a track and knob (`CellKind.Switch`, knob right when on; still not interactive), the status word in its tone, the version as plain muted text (`v0.1.0` — never a mono cell, whose `mspace` run drew it letter-spaced live), the sides chip in plain words (`Client and server` / `Client only` / `Server only`), then right-aligned the server part (`Server part running` / `Server part not on this server`; hidden when unknown, and rewritten in place when the joined server's handshake evidence arrives or ends) and the framework's action (**Uninstall** / **Cancel uninstall** / **Cancel install**) at the far right.
2. **Required-server warning**, when a manifest-verified two-sided package has no server half anywhere this client can see.
3. **Mod header.** Default: the title (large), one muted line `by <author> · <one-liner>`, then one row of small buttons, each sized to its caption (`HeadBlockLayout.ButtonWidth`: the glyph estimate widened by 30 % and padded; live the plain estimate cut `Example` to `Exam…`) — the mod's custom pages (captioned with the page title) and **Store page** when a store url is known (absent otherwise; it replaced the stray `»` icon at the row's end) — with the release date after them, muted. There is no Like control ([likes and ratings are removed](mod-package-capabilities.md#rating-control-acceptance)). A mod's own `Header(...)` content takes the title and by-line's place; the button row still follows it, so custom pages stay reachable.
4. **Provenance** (`page-provenance`): one small muted line (14 px, wrapping), not a full body row.
5. The update offer and the last action sentence (unchanged from MODSPAGE1).
6. **Settings action row** (`page-settings-head`, 40 units): a small muted **Settings**, then at the right the search field and **Reset all**. It is drawn at the rows' width, so it never sits over the description column.
7. **Sections**: the game's section header (`ModSettingsView.GameSectionRow`): the title (28 units, about 30 px on screen), middle-aligned in a 48-unit row, with the game's red rule (`Divider02_LeftToRight`) directly under the left half of the row. The Keys section carries **Open in Keybinds** at the right of the same line. Then come its rows.

An empty header button row (no custom page, store link or release date) is hidden, and its readback moves to the title row.

**The settings grid: the game's Options page look** (MENUKIT2, 2026-09-30; source-checked and unit-tested, **not live-verified**; the measurements are in the internal research notes). Every setting is **one line at a fixed 70-unit pitch** (`ModSettingsRowGrid`: the game's 58-unit entry at its 1.207 canvas scale), whatever its help says.

- **Row.** The name is 18 units (about the game's 20 px on screen; text is not scaled like the geometry), regular, at the game's resting gray (161). It turns 231 while the row is hovered or selected, under the game's row tint band (the band's hue at 0.1 alpha: the game's UI blends in linear color space, where the earlier 0.3 read as a saturated blue). Every row's top carries the game's resting top gradient, the faint separator.
- **Columns.** The control column starts at 45 % of the row. Bars end 116 units short of the right: dropdown, text field, keybind cap, read-only value and button. Compact controls end 19 units further in: the checkbox and the value box. The reset arrow is centered 39 units from the right, before the scrollbar band.
- **Controls.**
  - A bool is the game's **checkbox**, a 24-unit sprite whose frame draws at about 18-20 px (`Checkbox_Normal`, `Checkbox_Hover` on hover and `Checkbox_Disabled` when grayed, with `Checkmark_Normal` when on). The mod's own on/off in the status bar stays a switch.
  - A bounded number is the game's **slider**: the `Slider_BG` track, the diamond `Slider_Handle_Normal`, and its exact **value box** (`SimpleBox_Normal`, 87 units wide) at the compact edge. On a column too narrow for both, the track takes the column.
  - A finite choice, including the two-to-four-option `segmented` control, is the game's **dropdown bar** (`DropdownButton_Normal` with the `DropDownButton_Arrow`). A click anywhere on the bar opens the list of options under the field, the current one in gold; picking an option closes the list and applies it once (one write, one audit line; a rule's own confirmation still asks first, for example Wipe on's scheduled-wipes confirmation). Escape, a click outside the list, a second click on the bar or picking the current value closes it with no write. One list is open at a time. The list is drawn on its own sorting canvas above the panel's rows (`ModsCanvasBand` panels + 41, set again each time it opens) over an opaque back, with a full-screen click-catcher behind it, so no row under it shows through or takes a click (OPTIONHOVER1, user screenshot 2026-10-06: the next row's checkbox drew over an open list; source-checked and built, not live-verified). Arrow-key navigation inside the list is not built (the kit has no list navigation keys yet). DROPDOWN1, user review 2026-10-06 (each click on Game mode wrote the next value); source-checked, built and game-free tested (`ModSettingsPageTests`, `ModSettingsStage2Tests`, `ServerSettingsStage5Tests`), not live-verified.
  - A keybind is the Keybinds tab's key cap (180 by 36, right-aligned).
  - Number, text and color fields use the value-box frame. The other controls the game has no prefab of are composed from its parts; see the [control gallery](#control-gallery).
  - A text area, a builder `.Radio` and a multi-select keep their own height.
- **Reset arrow.** A resettable row always draws the game's `Icon_Reset` arrow: dark gray at the default (clicking it does nothing) and steel blue when changed.
- **Row chip.** A small bordered chip immediately left of the row's value or control: **Restart** while a needs-restart change waits. On a server-synced value it is **Server** (the server's value is in force) or **Awaiting server** (not received yet); these replace the old "(awaiting server synchronization)" suffix.
- **Description column.** The help is in a column at the page's right, the game's details panel (title 28 units, body 18, with 20 units of inner padding at each side). While the column is shown:
  - The page's scrollbar is thin (6 units): a dark track with a gray handle, in the gap between the rows and the column. It starts at the first settings row.
  - Plain rows below the first settings row (notes, inline fields) share the rows' width.
  - What the column shows: the setting's name as the heading, the `Divider_Big_02` ornament, the help, then the notes (the rest of the help line, in the warning tone for a refusal, a grayed row or a key conflict).
  - The column describes the **hovered row**, else the **selected row** (the one last changed), else the first row.
  - **Hovered options** (OPTIONHOVER1, requested 2026-10-06). While the pointer is over an option of a dropdown's open list or a line of a radio list, the column describes that option: the setting's name as the heading, then `Label: explanation` (the option's [description](#option-descriptions); the label alone when it has none), then the setting's own notes: its help's sentences that start with `Applies` (`Applies at the next restart.`) and the notes the row carries (a refusal, `The default is …`, a status). When the pointer leaves the options, or the list closes (a pick, Escape, a click outside), the column describes the row again. Source-checked, built and game-free tested (`OptionHoverDetailsTests`), not live-verified.
  - It stays in view while the rows scroll.
  - The column is drawn when the content area is at least 1100 units wide. It is 28 % of the width, 300 units at least and 531 at most. On a narrower pane there is no column, and each row's help is one line under its name, cut with an ellipsis.
- **Art.** The art is the Options page's own sprites, copied into Progenitor-owned textures on idle frames once the title or a world has settled, never on the workspace's open path. The log line is `Settings kit: prepared in N ms (owned-copy=K) over W ms of idle frames` (`SettingsArtDonors`, the Keybinds tab's ladder: live, owned copy, held, resident or loaded sprite, then kit), so a world load does not lose them. A part with no sprite draws the kit's flat look in the same colors.
- **Custom pages.** Custom pages drawn on the grid (`GridControls`) get the same rows without the column.
- **Raw settings page.** In the workspace, a mod's raw **Settings and keybinds** page (`mods_ui select page=settings`) is drawn the same way (`ModSettingsLayout.RawSections`). It has a left-aligned `{mod} · Settings` title (`page-raw-title`) and **Back to mod** on the action row, beside **Reset all**. Each config section, humanized (`ForServer` → `For server`), is a section; each name is the key humanized; the description is the entry's description plus what it accepts (`One of: …`, `From 1 to 10.`); the control is chosen automatically. The standalone settings overlay keeps its list.
- **Buttons.** The action row's buttons, **Open in Keybinds** and a row's button use the game's standard button: the Keybinds tab's owned key-cap copy of `SimpleMenuButton_Normal`, prepared on the same idle frames. The Options page's Back button (`Button_Normal`) is a second donor.
- **Preferences tab** (was Configuration; three homes, [mods-menu.md](mods-menu.md#preferences)).
  - The tab's title carries the red rule; its groups are gold small-caps subheadings, or drawers when they hold more than four settings (Preferences, the Server tab and Progenitor's page only; a mod's own page has no drawers).
  - Its bools and its default-install choice are the grid's controls.
  - Its buttons are labeled rows: **Local server** [Stop server] and **Force stop** [Stop server (force)], in the danger tone. Settings organization replaced the **Progenitor** [Progenitor settings] row with a link row to Progenitor's page (stage 3), and moved **Uninstall from this client** to Progenitor's page as **Remove Progenitor from this PC** [Uninstall], still in the danger tone (stage 4); [mods-menu.md](mods-menu.md#where-progenitors-settings-are-drawn).
  - The explanations (the private-game note, the install-target help, what each row does) are in the description column for the hovered row.
  - The left nav uses the Mods list's row look.

**Display names** (`ModSettingsLayout.DisplayName`): derived from the cfg key — camelCase, PascalCase, underscores, hyphens and dots become words in sentence case (`ShowHints` → `Show hints`, `spawn_multiplier` → `Spawn multiplier`); a run of two or more capitals keeps its case (`HTTPPort` → `HTTP port`); the words of `ModSettingsLayout.Acronyms` keep their own spelling whatever the key's case (UI, HUD, PvP, PvE, ID, FPS, XP, HP, MP, NPC, VFX, SFX, AI, API, URL, DPS, AoE, UTC, GUID, IPv4, IPv6: `UiDemoGreeting` → `UI demo greeting`, `PvpTweak` → `PvP tweak`, `IPv6Address` → `IPv6 address`); a number starts a word and keeps the capitals after it (`Enable3DAudio` → `Enable 3D audio`, `Level2Cap` → `Level 2 cap`). A builder `label:` or a registered `SettingPresentation.Label` wins. `[Section] Key` is never drawn; it stays the readback's `label` and is searchable.

**The help line** (`ModSettingsLayout.HelpText`), sentences in order: the help (the builder's `help:`, else the entry's description), the read-only or grayed reason and the fallback, the declared scope (`Server-set.` / `Local to this client.`), `The default is <default>.` while the value differs from its default, `Pending restart.` while a needs-restart change waits, and a transient `Saved.` or the refusal.

**Read-only values** show `Locked · <value>` right-aligned in the control column (the game font has no lock glyph), refreshed each second, and say why once on the help line: `Set by the server. Join a server to see its value.` (synced, not yet received; since step 4 a Server setting grayed for the connection says the [framework reason](#settings-scopes-on-the-page-step-4) instead), `Set by the server.` (the server's value is in force), `Belongs to another mod; change it there.`, or `Not editable here.` A declared `scope: Server` adds no second `Server-set.` to a read-only or admin-request row. An empty read-only value (`Locked`, `Admin only` or an `Info` row) reads `Not set` in a muted 0.5 gray instead of blank (decided 2026-10-06; a row's `EmptyShows` still wins; built and game-free tested, not live-verified).

## Framework status line

| Part | Behavior | Source |
|---|---|---|
| Switch | The player's choice (`On` / `Off`) as a track and knob. Stage 3: **clickable only while it can act** (`ModSwitchView.Interactive`: the [switch](#stage-3-the-enable-switch) is live or takes effect at the next start); the cell is then a clickable switch cell (`CellSpec.Clickable`), never a button captioned On/Off. Otherwise it stays a plain switch cell and the readback says `interactive: false` with the reason (Progenitor's own parts, an ordinary plugin whose files cannot be told apart). | `ModFrameworkLine.BuildRow`, `ModSwitch.Decide` |
| Status | Worst first: `Failed to load` (danger), `Loaded with problems` (warn), `Disabled` (the player's switch), `Paused for this server` (or `Resuming…`) under [per-server mod pause](mods-menu.md#per-server-mod-pause), then a restart owed — `Pending restart · to load` / `Pending restart · to uninstall` for a written-but-unloaded install or a pending removal, `Turns off at next start` / `Turns on at next start` / `Removed at next start` for a switch or an uninstall request that waits for a start, `Pending restart · settings changed` while a needs-restart setting differs from its value at load — then `Enabled`. `Not on this client` for a mod only the joined server listed (no switch then). A conflict never changes the word: it adds a chip (`2 conflicts`). The status cell is rewritten in place after a commit. | `ModFrameworkLine.DecideStatus`, `ModPagePane.SetSettingsPendingRestart` |
| Version | `v` + the version the loaded (or paused) plugin reports, else the installed file's (ledger), else the listing's. | `ModMenuInventoryRow.LocalVersion` |
| Sides | `Client and server` for a manifest that requires the server half or a listing that derives both; `Server only` / `Client only` from the listing; else the plugin's own declaration (`ClientOnly` → client, `ServerMustHave` → both). Unknown draws nothing. | `ModFrameworkLine.DecideSides` |
| Server part | Two-sided mods only: `Server part running` when the server's handshake request named the mod; `Server part not on this server` when the mod is paused for this server, or (stage 3) when it declares a [counterpart](#counterparts) and the server's full handshake request did not name it — then `Server part not on this server · limited`. The `limited` words show whenever the readback's `limited` is true, even if the sides read client only, and are never the cell dropped for width. Anything else is unknown and omitted. A declared counterpart makes a mod two-sided. | `ModFrameworkLine.DecideServerPart` |
| Conflicts | Stage 3: a warn-toned chip after the sides, `1 conflict` / `2 conflicts`, when the mod has keybind or command [conflicts](#mod-problems). | `ModProblems.ConflictChip` |
| Help | **Help**, left of the action, only when the mod ships an HTML help file ([Help button](#help-button)). | `ModPagePane.HelpButton` |
| Action | **Uninstall** / **Cancel uninstall** / **Cancel install** for a Progenitor-installed mod, at the far right, with the confirmation dialogs described in [mods-menu.md](mods-menu.md). Stage 3: **Uninstall** / **Cancel uninstall** for an [ordinary plugin](#ordinary-bepinex-plugins) whose files are known. | `ModPagePane.BuildFramework` |

Status words and tones: `Enabled` green, `Failed to load` danger, `Loaded with problems` and `Paused …` warn, `Pending restart · to load`, `· settings changed`, `Turns off at next start` and `Turns on at next start` yellow, `… to uninstall` and `Removed at next start` orange, `Disabled` / `Not on this client` muted. Every word has one source (`ModFrameworkLine.WordOf`, `ModSwitchWords`), which the Mods list and the dialogs read too. Colors come from the existing kit tones (`LabelTone`, `MenuPalette`); SETTINGS1 adds no palette entries.

## Help button

Status: game-free tested (`ModHelpTests`, `ModPageTests`), not live-verified.

A mod that ships an HTML help file gets a **Help** button in the page's actions bar, left of the action button. Pressing it opens the file in the player's default browser (`Application.OpenURL` with a `file://` URI); pointing at it shows the tooltip `Open <Mod Name> help in your browser`, and the description column says the same. Without a help file the button is not drawn. Opening the browser takes the player's focus, so only the button opens it.

**Ship the help one of these ways.** The first that exists wins:

1. **A file in the mod's own folder.** `help/index.html` next to the DLL, with its stylesheet and images beside it under `help/`:

   ```
   BepInEx/plugins/Owner-MyMod/MyMod.dll
   BepInEx/plugins/Owner-MyMod/help/index.html
   BepInEx/plugins/Owner-MyMod/help/style.css
   ```

   This works only when the mod's DLL is the only plugin DLL in that folder (a helper library beside it does not count); a folder with two plugins belongs to neither, and each uses its embedded help. For a DLL one folder down in a mod-manager package (`Owner-MyMod/plugins/MyMod.dll`), the package folder's `help/index.html` counts too, under the same rule. `help/index.html` directly in a shared folder (`BepInEx/plugins` or `BepInEx/progenitor-reload` itself) belongs to no mod and is not used; a DLL there uses the plugin-local folder `<shared folder>/<AssemblyName>/help/index.html` instead (the same folder its localization and assets use). `<dll name>.help.html` beside the DLL also counts, in any folder.
2. **Embedded in the assembly.** Embed the folder with its paths as resource names, so the stylesheet and images come along:

   ```xml
   <ItemGroup>
     <EmbeddedResource Include="help\**" LogicalName="help/%(RecursiveDir)%(Filename)%(Extension)" />
   </ItemGroup>
   ```

   The page uses the resource `help/index.html` (or the SDK's default name `<AssemblyName>.help.index.html`; that spelling turns subfolders into dots, so `help/img/logo.png` would land as `help/img.logo.png`: use the `LogicalName` above), else a single `Help.html` (or `<AssemblyName>.Help.html`). Only the names are recorded when the mod loads, and only on the client. When the button is pressed the files are read from the assembly and written to `BepInEx/progenitor-cache/help/<guid>-<hash>/<version>/` (`<hash>`: 8 hex digits of the GUID's SHA-256, so two GUIDs that clean to the same folder name never share one), keeping their paths, and that copy is opened. Files in that folder that are no longer in the assembly, and the mod's other version folders, are deleted then. Up to 512 files and 16 MiB are read per assembly.
3. **A path you name.** When the file is elsewhere in the mod's folder, name it relative to that folder, in source or at load:

   ```csharp
   [ModHelp("docs/manual.html")]
   public sealed class Plugin : ProgenitorPlugin<PluginContext> { ... }

   // or, in OnLoad (tracked; wins over [ModHelp]):
   ctx.Menu.Help("docs/manual.html");
   ```

   The path must be an `.html` or `.htm` page. It may also name an embedded resource, which is extracted with the resources in its folder (its stylesheet and images are written, but only the page is opened). `ctx.Menu.Help` throws `ArgumentException` for an absolute path, a URL or a `..` segment; for any other file type it logs a warning and declares nothing, and a `[ModHelp]` path of another type is skipped with one log line.

**What is opened.** Only an `.html` or `.htm` file under the mod's own folder or its cache folder (never a shortcut, script or program, which the system would run): a path that would leave it, an absolute path, another drive and a remote URL are refused, and an embedded resource whose name would leave the cache folder is skipped. The URI is built from the full path with every segment percent-encoded, so spaces, `#`, `%` and non-ASCII folder names reach the browser intact. A file that cannot be opened is logged once per mod, and the page's last-action line says so.

**Progenitor's own entry** opens `BepInEx/progenitor-core/docs/guide/progenitor-guide.html` (else `BepInEx/progenitor-core/help/index.html`) when the package ships it. The same guide is the Mods tab overview's **Help guide** tile, drawn only while it is installed ([mods-menu.md](mods-menu.md)).

**Readback.** `page-help {kind: "button", caption: "Help", tooltip, cell: "help", shown, source: folder|embedded|declared|guide|none, file}` on the actions bar (`page-framework`); `file` is the path under the mod's folder or the cache folder. A bridge test presses it with `{"action":"widget_click","label":"page-framework/help"}`, which opens the browser (a user-visible focus change).

## The mod's header area

A mod replaces the title and by-line with its own rows:

```csharp
ctx.Menu.Register("HeaderDemo", "A header demo.")
    .Header(panel => panel.Label("Custom banner for HeaderDemo"));
```

`ModMenuRegistration.Header(Action<IUiPanel>)` runs on every page build, after the status bar and the warning, so the mod's rows land under the bar. **What is enforced:** the builder gets a restricted panel (`ModHeaderPanel`) that can only add rows — `Label`, `Heading`, `Note`, `Fact`, `Button`, `Toggle`, `Slider`, `Input`, `Dropdown`, `Number`, `SliderField`, `ColorField`, `KeyField`, and (stage 2) `TextArea`, `Radio`, `MultiSelect`, `Info`, `Divider`, `ConfirmButton` — plus `BeginInline`/`EndInline` and its own `ContentAlignment`. `Show`, `Hide` and `Dispose` do nothing, the `Text`, `Visible`, `Footer` and `CaptureMode` setters are ignored, and the kit's internal blocks are not reachable. After the builder, an inline cluster it left open is closed and the page's alignment is restored. A builder that throws has every row it added disposed, is logged (`Ui: mod header of <id> threw; removed N row(s) and drawing the default: …`) and the default area is drawn. Its widgets are disposed with the page. Once per registration (a second call throws `ArgumentException`). Not enforced: what the rows say, and how many there are. The snippet is the one `ModSettingsPageTests.A_custom_header_draws_under_the_framework_line_and_a_throwing_one_falls_back` compiles and drives; the Sample does not use `Header`.

## Settings scopes on the page (step 4)

**Live (2026-10-04, Steam client 3840×1080 and the isolated test server, branch feat/settings-scopes-page 973993a5 + c5eee331):** the Sample's page as admin showed "Your settings" then "Server settings · Applies to everyone on Progenitor Forms Test" (`canEdit: true`, Example limit `server-request`, a "Demo" subheading); at the title screen the line read "Applies to everyone on the server you join" and Example limit was `readonly`, grayed "Not connected to a server."; rejoined as a non-admin (adminlist emptied) Example limit read "Admin only · 10" with "Only server admins can change this." and Ember stack "Locked · 99", while Your settings stayed editable; HotDemo's page showed no "Server requirement unknown." line, an untruncated band, and on a fresh Core generation before any Install tab visit read "Client: Installed 0.1.0 · Server: Installed 0.1.0" (c5eee331; before it, "Not installed" on both). **Not live-verified:** a client-only and a server-only mod's pages, `HideWhenAlone`, and the waiting / no-Progenitor / version-mismatch reasons.

**Status: source-checked and game-free tested (`ModSettingsScopesPageTests`, the updated stage 2 and counterpart tests), not live-verified.** Design and decisions: [settings-scopes.md](settings-scopes.md) (decisions and mockup of 2026-10-03). This step covers the settings a client already knows: its own cfg rows and the synced rows ConfigSync knows. Server-owned personal values (`ctx.Settings.PerPlayer`) and the settings of mods this client lacks arrive as published definitions over Core channel PrSt; their contract, presentation hints and item lists are in [settings-scopes.md](settings-scopes.md).

- **One list, two sections.** **Your settings** (Personal) then **Server settings** (Server-wide), each the game's section header with its red rule (`page-scope-N`). There are no tabs. A section with nothing to show is omitted: no row at all, or every row hidden.
- **Which section.** A synced row is Server-wide. A builder button or info line is Server-wide only when declared `scope: Server`. Everything else is Personal: the player's own cfg rows, keybinds, registered actions, and a row declared `scope: Server` on an entry that is not synced.
- **Author sections are subheadings.** The sections the author declared (`Section(...)`, `.Settings(title, …)`, General, Keys, Actions) are small, muted subheadings in small caps (`page-section-N`) inside the scope their rows belong to, in declared order.
  - A section holding both scopes is split, keeping the row order. A divider goes with the row before it.
  - A title that only restates the scope (`Personal`, `Server`, `Your settings`, `Server settings`, `Client`, `Local`, `Server-wide`) draws no subheading. Its rows keep their order, and their readback `section` keeps the author's title. Search still matches that title.
- **The Server settings line.** One muted row under the heading:
  - `Applies to everyone on <server>`: the name the server's settings catalog carries (its own `-serverName`, else the `Name` in its `ServerHostSettings.json`, the same name its signed catalog gives), else the name the Install tab's picker knows for it, else `Applies to everyone on this server`. The line never shows the server's `address:port`; the stock `V Rising Server` counts as no name. The personal-server line follows the same rule (`Only affects you on <server>. Saved by this server.`, else `Only affects you on this server. Saved by the server.`).
  - `Applies to everyone on the server you join` when not connected.
- **Admin only.** The Server settings rows an admin could edit (opted into administrator edits) are read-only for a player the server did not authorize. The control column shows `Admin only · <value>` and the help line says `Only server admins can change this.` The player's authorization is the server's remote-install status (admin list or adminauth; [Settings rows](mods-menu.md#settings-rows)). With no status, as from an older Core, the **Apply** control is drawn and the server decides. A synced row that is not opted in stays `Locked · <value>` with `Set by the server.` for everyone, admins included.
- **Framework reasons** (`SettingAvailability`, worst first), drawn grayed on the help line:
  - `Not connected to a server.`
  - `Waiting for the server…` (joined, the handshake has not settled)
  - `Server doesn't run Progenitor.`
  - `Needs the server part of this mod.` (paused for this server, or not named by its handshake request)
  - `Server runs a different version of this mod.` (the server named the mod but no synced value arrived)
  
  They apply to Server settings rows and to Personal rows marked `needsServerPart`. Personal rows otherwise stay editable in every state. A false `enableIf` grays a row with `Not available right now.`, and a false `showIf` hides it. For an absent half, the author's `fallback` follows the reason. A Server settings row grayed this way does not repeat `Set by the server. Join a server to see its value.`
- **Hide when alone.** `.HideWhenAlone()` after a row hides it, instead of graying it, while the half it needs is absent or unreachable: not connected, a server without Progenitor, or one without the mod's server part. A wait for the server, a version mismatch and `enableIf` still gray it. A Server settings section left empty is omitted. `HideWhenAlone()` before any row, or after a divider, throws `InvalidOperationException`.
- **Reset all** covers Your settings only. Server settings keep each row's own reset arrow.
- **In place or rebuilt.** A change in the joined server's evidence (the handshake request, its result, the end of the join, the remote-install status) re-grays the open page in place. When it changes which control a Server settings row needs (joined or not, admin or not), the page is rebuilt (`Ui: mod page <id> settings rebuilt for the server's answer (<reason>)`).

**Readback.**

| Key | Value |
|---|---|
| `page-scope-section` *(live)* | `{scope: personal\|server-wide, heading, line, sections [subheading titles, "" for none], rows, shown, canEdit, context {connection: not-connected\|waiting\|not-progenitor\|progenitor, serverPart, canEditServerWide, serverLine}}` |
| `page-section` | adds `scope` |
| `page-setting` *(live)* | adds `scopeSection` (`personal` / `server-wide`), `owner` (`client` / `server`), `adminOnly`, `hideWhenAlone`, `availability {state: editable\|admin-only\|grayed\|hidden, code, reason}`; `greyed` / `reason` now report any framework gray (not only needs-server-part). `scope` keeps its old meaning. |

**Live check list (not run).** Use the isolated test server only, with a Core containing this change on both sides. Read each state once with `mods_ui widget_read pane=content`, not by polling. Restore the admin list afterward and leave the client at the title screen.

1. **The Sample, as admin** (SteamID in the test server's `adminlist.txt`, joined).
   - `page-scope-section` rows: `Your settings` then `Server settings`, the latter with `line: Applies to everyone on Progenitor Forms Test` (or `this server`) and `canEdit: true`.
   - The `Demo` subheading is drawn; no `Personal` or `Server` subheading.
   - `Menu/ExampleLimit`: `scopeSection: server-wide`, `control: server-request`. Applying a value is answered `Saved by the server.`
   - **Reset all** counts only Your settings rows.
2. **The Sample, not an admin** (admin list emptied, no adminauth; rejoin).
   - `Menu/ExampleLimit`: `control: readonly`, `adminOnly: true`, `availability.state: admin-only`.
   - The control column reads `Admin only · <value>`; the help line reads `Only server admins can change this.`
   - Your settings are still editable.
3. **The Sample, not connected** (title screen).
   - `Menu/ExampleLimit` is grayed: `availability.code: not-connected`, help line `Not connected to a server.`
   - The Server settings line reads `Applies to everyone on the server you join`. Your settings are editable.
4. **HotDemo** (installed on the test server, joined).
   - No `Server requirement unknown.` line under the header.
   - The band's server cell reads `Server: Installed …` in full (not cut), and `page-framework.serverColumn` still names the server.
5. **A client-only mod** (no synced rows).
   - Only `Your settings`, with no `page-scope-section` for Server settings.
   - The rows are editable joined and offline.
6. **A server-only mod** (for example the Server-led sample's server part alone, or HotDemo's server half).
   - The client has none of its rows: its page draws the settings the server publishes over PrSt ([settings-scopes.md](settings-scopes.md)), under **Your settings on this server** and **Server settings**. A server that sends version 2 hints gives them the mod's labels, controls, sections and show/enable rules; an older server's rows use the default editors. Confirm no stray section.
   - Then with its client part: its Server settings follow states 1 to 3, and `Needs the server part of this mod.` shows when the server lacks it.

Sections, in order (`ModSettingsLayout.Build`); the page then draws them under the two scope sections as above:

1. Stage 2: each section of the mod's settings page (`ctx.Settings.Page`), in the order the builder declared them.
2. Each `.Settings(title, entries…)` group, in registration order, with its entries in the order given.
3. **General** — the mod's other config entries from the settings catalog.
4. **Keys** — the mod's keybinds.
5. **Actions** — registered setting actions (`ctx.Menu.RegisterAction`).

**A row appears once, and the builder wins:** an entry the page places is claimed first, so a group or the catalog's walk that names it again is skipped (and within the page an entry placed twice keeps its first place). Empty sections are dropped; a page section with only buttons or info lines is kept. A group entry whose config file is not the mod's own (`SettingsCatalog.IsOwnedElsewhere`) is read-only unless `[Settings] EditOtherModsSettings` is set, and is excluded from **Reset all** either way; its own reset link works when it is editable. The same holds for a builder row.

While a row is modified the reset link (`«`, tooltip `Reset to <default>`) shows in its gutter; it hides again once the value equals the default, and while the row is grayed. **Reset all** acts on — and counts — only the rows whose own reset link would work: a row show-if hides or enable-if / needs-server-part grays keeps its value (`ModSettingsView.ResettableRows`). It asks first in the kit dialog (`Reset <mod> to its defaults?`, `N settings go back to the value the mod ships with. Server-owned settings are not changed.`, **Yes, reset** / **No**; a destructive dialog since DANGERENTER1, so Enter does not answer it and **Yes, reset** is in the danger tone) and then resets every modified row through the same path. The dialog belongs to the page: it closes when the page is rebuilt, another mod is selected, the workspace closes or the mod unloads, and **Yes** re-reads the rows from the page's live view and is refused (logged) if that view is no longer the one on show. Rows whose entry lives in another plugin's config file are never part of it.

The **search field** is on the settings header when the page has more than ten settings, buttons and info lines (`ModSettingsLayout.SearchThreshold`; dividers do not count). It filters as the player types (case-insensitive: display name, caption, cfg key, cfg section, help text, a builder label; a matching section title keeps all its rows) by **hiding and showing the rows already drawn** — the page is not rebuilt. The text is kept for a later rebuild of the same mod's page.

**The separate settings pane** stays reachable through `mods_ui select page=settings` and `SettingsPanel.Open()` (the global list).

## Which control a row gets

Decided by `ModSettingsLayout.ControlFor` (and, for a builder row, `ControlFor(row, options)`), in this order:

| Condition | Control |
|---|---|
| A synced entry opted into admin editing (`ServerRequest` path) | Field (the text area when the builder declared `.Text(multiline: true)`), **Apply**, status on the help line — the [admin path](mods-menu.md#settings-rows). JOINFLOW1: a date or choice row (SERVERDESC1: and a bool, as a switch) gets its own control instead ([below](#administrator-date-and-choice-rows)) |
| Nothing may write it (server-owned, another mod's, unsupported type) | Read-only: `Locked · <value>` and the reason on the help line |
| `bool` | Toggle |
| Enum (not `[Flags]`) | Dropdown of its names |
| `[Flags]` enum with 1–8 single-bit values | Stage 2: multi-select of those values; more is text |
| Number or string with a finite list (`AcceptableValueList`, `SettingPresentation.Choices`) | Dropdown |
| Number with both bounds (`AcceptableValueRange`, `SettingPresentation.Min`/`Max`) | Slider with an exact field; an explicit `EditorHint = Numeric` keeps the stepper |
| Any other number | Number field with steppers, bounded by its type's own range |
| String with `EditorHint = Color` | Color field |
| Other string | Text field (the allowed set in the help when there is one) |
| Keybind | Key capture control |
| Registered action | A button in the control column; its result on the help line |

**Choice values** (`SettingsBounds.Options`): an `AcceptableValueList`'s options are read from its own `AcceptableValues` array, not from the `Acceptable values: A, B` description BepInEx joins with unescaped commas, so a single-choice option may contain a comma (`Always, even in combat` is one option, saves and reads back). Only a bounds type with no readable array falls back to splitting that description. A string multi-select stores its selection comma-separated (below), so its options must not contain a comma.

A builder asks for a control by method (`.Radio`, `.MultiSelect`, `.Text(multiline: true)`, …). The edit path still decides first — a read-only or admin-request row stays one — and a control the value cannot take (a radio on a free number, a text area on an int) falls back to the automatic choice.

**Drawn as** (`ModSettingsLayout.DrawnControl`): an automatic dropdown with two to four options, each at most 14 characters (`UiOptionsRules.FitsSegmented`), is a **segmented picker** (readback `control: segmented`), which the settings grid draws as the dropdown bar. Longer lists stay a selector. A builder `.Dropdown(...)` always stays a selector. A builder `.Radio(...)` is a **radio list** (RADIO1, decided 2026-10-06, "these controls are not good for what they do aka this or that"): one option per line with a radio circle (`●` filled and gold for the chosen option, `○` for the others), the chosen line bright and the others dimmed, and an optional one-line muted explanation under an option; a click on a line chooses it (one write, never a toggle; a click beside the lines does nothing). It replaced the segmented picker. Pick-one admin rows with two options (**Mods break rules while playing**, **If mods can't be checked**) are radio lists too, each option with its explanation; the vanilla Server rules (Difficulty, Game mode, …) stay dropdowns. A multi-select is always a vertical list of check boxes. Readback: `control: radio` and `current`, `currentLabel`, `options [{value, label, description, selected, mark, part}]`; the bridge's `mods_ui radio_choose {label, value}` chooses a line. Built and game-free tested (`RadioListTests`), not live-verified. How each is drawn: the [control gallery](#control-gallery). Outside the settings grid (a plain `IUiPanel`) the kit look stays: the chosen segment in `MenuPalette.Gold`, a list option the kit's check box. A radio or multi-select longer than `ModSettingsLayout.MaxListOptions` (12) is drawn as a selector (radio) or a text field (a string multi-select), so no list row reaches the panel's row cap.

**Number step and decimals** (`ModSettingsLayout.NumberFor`): a declared `Step` wins; an integer steps by 1; a decimal steps by the finest decimal the stored, default and bound values use, or a readable hundredth of a declared range when that is finer. Units follow the name in brackets. The committed number is snapped to the step and rounded to the decimals in double before it is formatted, so a slider on a `double` setting writes `0.3`, not `0.30000001192092896`.

**Multi-select values** (`ModSettingsLayout.TryFormatSelection`): a `[Flags]` enum is written as its selected names joined with `", "`; bits of the stored value that no single-bit name covers are kept, and then the value is written as its number (`9`), which `SettingsEditors.TryParse` accepts for a `[Flags]` enum (a plain enum still refuses a bare number). The empty set is the enum's zero-valued name, and is refused (`choose at least one`) when it has none. A flag at `1 << 31` is an option like any other. A string list is read by splitting on commas and matched case-insensitively (the option's own spelling is written back); it is written as the selected options in option order, then every stored token that is not an option, unchanged — a click never drops a value the list does not know. A string entry with an `AcceptableValueList` is never a multi-select (that list accepts one value, so every multi-value write would be refused): the builder's `.MultiSelect` on one logs why and draws the entry's normal control.

A commit that repeats the stored value writes and logs nothing. The slider commits once it has rested 0.35 s; a drag still inside that window when the page closes is committed as the control is disposed. Writes go through `SettingsCatalog.Submit`: parse, bounds (`AcceptableValue*` refusal instead of BepInEx's silent clamp), a registered binding's policy, and the synced refusal are unchanged.

### Administrator date and choice rows

JOINFLOW1. **Status: source-checked and game-free tested (`DateTimePickerTests`), not live-verified.** On the admin request path a row that is not free text is drawn with the control its value needs, and each change is one request (`ServerSettings.Request`, with the current server value as the expected value), with the same help-line status as **Apply**: `Waiting for the server…`, then `Saved by the server.` or the refusal. A refused or failed request puts the control back to the server's value; a change made while a request is on its way, or on a grayed row, is put back and sends nothing. While the page is open, every synced value re-reads these rows (APPLYONCE1, game-free tested, not live-verified): a change by another admin, the dev bridge or a cfg edit shows at once, and the next request is checked against it ([mods-menu.md](mods-menu.md), Apply rows).

| Admin row | Control | Sent on | Readback |
|---|---|---|---|
| Builder `.DateTime(...)` | The date-time field and picker, in the row's `zone` (UTC without one), `Server time: …` on the zone line | Done, when the value changed | `control: datetime`, `editor: datetime` |
| A finite choice (`ModSettingsLayout.ChoicesFor(row)` not empty) | The builder's `.Radio` (12 options or fewer), otherwise the selector; drawn as the settings grid draws them | each pick | `control: server-request`, `editor: radio \| dropdown \| segmented`, `choices` |
| A bool (SERVERDESC1; source-checked and built, not live-verified) | The switch | each flip | `control: server-request`, `editor: toggle` |
| Anything else, numbers included | The text field (or text area) and **Apply**; a number is typed and sent once, so a stepper cannot send one request per step | Apply | `control: server-request` |

The bridge's `field_apply` presses **Apply** on a text or number row. On a date-time, choice or switch row it is refused (`... sends each change itself (no Apply)`): drive the picker with the `datetime_*` ops, or pick the choice, instead. For a player who is not an admin a date-time row reads `Admin only · <wall-clock value>` (the row's `zone`, else the player's own) instead of the raw ISO string; `Locked · …` likewise.

## Control gallery

**Live (2026-10-03, Steam client 3840×1080 in game on the isolated test server, branch ui/settings-controls-vanilla 21154b4e, Core generation 37, Sample built from the same branch on both sides):**
- **Seen on the Sample page** (bridge `select` plus content-pane scrolls, focus-free captures), in the gallery's described look:
  - the `ListButton_Glow2` gradient band on the lit row;
  - the framed Short / Full segments with the position bar;
  - the game checkboxes for the multi-select and the long radio (Demo when), with identical frames for chosen and unchosen options;
  - the number with − / + steppers;
  - the color swatch beside its hex box;
  - fields and slider boxes on the dim value-box rim (no bright rim on the lit row's field);
  - plain section headings;
  - the info row and "Locked · 99" in gray.
- **Buttons:** row buttons, key caps, Reset all and Open in Keybinds drew the game's steel button art.
- **Readback:** `page-settings-drawn` read `pendingSkins: 0` and `buttonArt.donor: menu-button`.
- **Earlier generation, before a rejoin:** these buttons kept the crimson fallback in the same world (generation 36). The art reached them only after the rejoin.
- **Not live-verified:**
  - pointer hover and focus tints (no OS input was used);
  - the title-screen host;
  - narrow widths;
  - the Keybinds tab band;
  - Admin-only and grayed rows on this build.

Every control a mod can put on its settings page, and the game parts the settings grid draws it with (CONTROLS1, 2026-10-03). **Status: source-checked and game-free tested (`SettingsControlsVanillaTests`, `MenuKitSettingsTests`), not live-verified.** The sprites come from the [settings art ladder](#page-design-stage-2) (owned copies of the Options page's sprites); a part that is not ready draws the kit's flat look in the same colors. The game's references are probe M1's prefabs. Author code is unchanged: the builder methods and the automatic choice decide the control as before.

| Control (builder / automatic) | Readback `control` | Game reference | Drawn with |
|---|---|---|---|
| `Toggle`, a `bool` | `toggle` | `CustomGameSettings_Checkbox` | `Checkbox_Normal` 24 (hover and selected `Checkbox_Hover`, grayed `Checkbox_Disabled`), `Checkmark_Normal` 20 when on, at the compact edge. |
| `Slider`, a bounded number | `slider` | `CustomGameSettings_Slider` | `Slider_BG` track (fill in the lighter tint), diamond `Slider_Handle_Normal` (hover sprite), then an 87-unit `SimpleBox_Normal` value box with the number centered. |
| `Dropdown`, an enum or finite list, a 2-4 option automatic `segmented` | `dropdown`, `segmented` | `CustomGameSettings_Dropdown` | `DropdownButton_Normal` bar (hover `DropDownButton_Focus`), the value left at 0.85 gray, `DropDownButton_Arrow` at the right. A click opens the option list under it; a pick writes once (DROPDOWN1). |
| `Radio` with 2-4 options of at most 14 characters | `radio` | `CustomGameSettings_Selector` (value box, arrow buttons, index bar) | One `SimpleBox_Normal` segment per option (hover `SimpleBox_Hover`), the chosen frame full and the others dimmed, the chosen caption 0.961 and the others 0.55 (shrinking to 13 before a long caption is cut), and the selector's index bar under every segment: opaque steel under the chosen one, dim under the rest. |
| `Radio` with longer options, `MultiSelect`, a `[Flags]` enum | `radio`, `multi-select` | the checkbox | One line per option: the game's checkbox and checkmark, the caption at the row name's grays (161, 231 when checked). |
| `Text` | `text` | the value box | `SimpleBox_Normal` across the bar column, text left at a 12-unit inset, middle-aligned, 0.85 gray. |
| `Text(multiline: true)` | `textarea` | the value box | The same frame at the text area's height, text from the top left. |
| `Number`, an unbounded number | `number` | the selector's arrow buttons and the value box | The value box with the number centered, and `SimpleBox_Normal` − and + squares at its sides. |
| `Color` | `color` | the value box | The value box with the hex centered, and the color swatch (steel-framed square) immediately left of it. |
| `DateTime` (JOINFLOW1; source-checked and game-free tested, not live-verified) | `datetime` | the value box and the row button; the game popup (`CommonPopup`) for the picker | One cell row: the name, a read-only `SimpleBox_Normal` value box (`Sat, Oct 10, 2026, 6:00 PM (UTC-04:00)` or `Not set`), a `SimpleMenuButton_Normal` key-cap button (`Set` / `Change`) at the bar edge, and the `Icon_Reset` arrow in its own gutter cell. The button opens the picker in the dialog chrome: `SmallerButton_*` popup buttons for the month arrows, the 42 day buttons and the `-` / `+` steppers, the zone line muted and the daylight-saving note orange. |
| `Keybind`, a registered bind | `keybind` | the Bindings menu's key cap | `SimpleMenuButton_Normal` key cap, 180 by 36, right-aligned. |
| `Button`, a registered action | `button`, `action` | `CustomGameSettings_Button` | `SimpleMenuButton_Normal` in the control column, caption 0.9. |
| `Info`, a read-only value, `Admin only · <value>` | `info`, `readonly` | none (the game has no read-only row) | The value right-aligned at the value box's size and 0.85 gray. |
| Server request (an admin's synced row) | `server-request` | the value box and the row button | Its field and **Apply** take the field and button looks above. |
| `Divider()` | `divider` | none | A thin rule in the row rule tone. |
| Section heading, subheading | `page-scope-section`, `page-section` | `CustomGameSettings_Label` | Scope sections: 28-unit title with `Divider02_LeftToRight` in dark red; author sections: small muted subheadings. |
| Reset arrow | `page-setting-reset` | the entry's `ResetButton` | `Icon_Reset`: 0.25 gray at the default, steel blue when changed. |
| Settings search, **Reset all**, **Back to mod**, **Open in Keybinds** | header cells | the value box; the row button | The search on `SimpleBox_Normal`; the buttons on the game button art below. |

**Game button art** (row buttons, key caps, header buttons), first that is ready: `SimpleMenuButton_Normal` from the Keybinds tab's key cap, then from the settings kit's copy, then the game's popup button `SmallerButton_Normal` (with its hover sprite), which is loaded in a world as well as at the title. A Core hot-swapped in a world never saw the title's Options page, and there neither `SimpleMenuButton` donor drew (live 2026-10-03).

**Art that arrives late.** The settings kit prepares its sprites over idle frames. A control (or header button) drawn before its part is ready keeps the kit look and is skinned again every 15 frames until the part arrives, for at most 3600 frames; the retries end with the workspace.

States, for every control: the row band and the name's 231 on hover or selection. With the game's band sprite (`ListButton_Glow2`) the band is composed as the game does (hue at 0.753 × 0.102 alpha); without it, the flat hue at 0.1. A value box, stepper, radio segment or option check box answers the pointer with a tint of its normal frame, not the game's hover sprite: 0.85 of itself at rest, full under the pointer, a quiet steel while it has the keyboard; the frame's geometry never changes (`SimpleBox_Hover` is a bright warm rim and `Checkbox_Hover` a heavier frame, and with the game in a background window the hover stays wherever the last click was). The bool checkbox keeps the game's own `Checkbox_Hover`. Section headings, the scope line and the settings header are plain rows: the pointer never lights them; a grayed row (enable-if, needs-server-part, framework reasons) at the grayed alpha with the game's disabled sprites where the control has one; read-only, locked and admin-only rows as the value text; the **Restart**, **Server** and **Awaiting server** chips left of the control. The **Server** tab draws no **Server** chip: every row there is a server row (decided 2026-10-06). Pages that mix personal and server rows keep it.

## The settings page builder (stage 2)

```csharp
ctx.Settings.Page(page => page
    .Section("Personal")
        .Toggle(hints, help: "Show the help text on the Example page.")
        .Radio(style, showIf: () => hints.Value)
        .MultiSelect(topics, choices: new[] { "Combat", "Travel", "Building" }, showIf: () => hints.Value)
        .Text(notes, multiline: true, lines: 3)
        .Toggle(layout, needsRestart: true)
    .Section("Server")
        .Slider(limit, scope: SettingScope.Server, help: "Demonstration limit; the server's value wins while joined.")
    .Section("Demo")
        .Info("Local time", () => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
        .Number(count, min: 0, max: 99, help: "A number field with - and + steppers.")
        .Color(tint, help: "A color: the swatch and its hex value.")
        .Radio(when, help: "A radio with long options: one option per line.")
        .Divider()
        .Button("Log a line", () => ctx.Log.LogInfo("Sample settings: button confirmed and run."),
            confirm: "Write a line to the Sample log?", help: "A confirmed action: it asks first."));
```

This is `samples/Progenitor.Sample/SampleMenu.cs`, abridged (its Dialogs section and the bindings are left out; compile-checked with the Sample's Release build; compile against that file rather than copying this block).

| Member | Contract |
|---|---|
| `ctx.Settings` (`PluginSettings`) `.Page(Action<SettingsPageBuilder>)` → `IDisposable` | Runs the builder **once**, now, and registers the page for this plugin. A builder that throws registers nothing and the exception reaches `OnLoad`. Once per plugin: a second page while the first is registered throws `ArgumentException`. Tracked: every unload reason disposes it. Registers the plugin's cfg as a settings source, like `ctx.Menu`. |
| `Section(title)` | Starts a section. Rows added before any section go in an untitled one (drawn without a heading). |
| `Setting(entry, …)` | The automatic control ([table above](#which-control-a-row-gets)). |
| `Toggle(ConfigEntry<bool>, …)` | A switch. |
| `Number(entry, min, max, step, units, …)` / `Slider(entry, min, max, step, units, …)` | A stepper field / a slider with an exact box. The bounds apply where the entry declares none; a slider without both bounds is a number field. |
| `Dropdown(entry, choices, labels, …)` / `Radio(entry, choices, labels, …)` | A selector / every option visible. Over an enum, an `AcceptableValueList` or `choices` (with `labels` drawn in their place; one label per choice or `ArgumentException`). |
| `OptionDescriptions(params string[] descriptions)` | OPTIONHOVER1: a short explanation of each choice of the `Dropdown`, `Radio` or automatic choice row added last, in the order of its choices (the `choices` given, else the enum's names or the `AcceptableValueList`'s values); `""` for none. A radio list draws each under its option (one line); a dropdown's list does not draw them. Either way the [description column](#page-design-stage-2) shows the hovered option's. Throws `InvalidOperationException` after a row that cannot be a choice (a toggle, number, text, button, multi-select) and `ArgumentException` when the row lists `choices` and the count differs. The same field for a registered presentation is `SettingPresentation.ChoiceDescriptions`. |
| `MultiSelect(entry, choices, labels, …)` | A `[Flags]` enum's single-bit values, or a string entry's `choices`, stored comma-separated. A string entry without choices is a text field. |
| `Text(ConfigEntry<string>, multiline, lines, …)` / `Color(ConfigEntry<string>, alpha, …)` | A text field (2–12 lines when `multiline`) / a color field. |
| `MaxLength(characters)` | TEXTAREA1: the most characters the `Text` row added last holds (default 256, or 4,096 for a text area; see [text limits](#text-settings-limits-and-plain-text)). On a server-wide row it is also the limit the server enforces, capped at 2,048 characters and 2,048 UTF-8 bytes ([server-wide limits](#server-wide-text-limits-textlimit1)). Throws after a non-text row or below 1. |
| `DateTime(ConfigEntry<string>, seconds, zone, …)` | JOINFLOW1 (source-checked and game-free tested in `DateTimePickerTests`, not live-verified): a date and time stored as ISO 8601 UTC (`2026-10-10T18:00:00Z`, empty for none), drawn as the date-time field ([control gallery](#control-gallery)). `zone` (`Func<TimeZoneInfo>`) is read when the row is drawn and when the picker opens; without it a personal row uses the player's zone and a server-wide row UTC. `seconds` (`Func<bool>`) is read when the picker opens; true adds a seconds stepper. Done commits through the same write as every row (a personal row) or sends the value to the server ([admin rows](#administrator-date-and-choice-rows)); Cancel and Escape write nothing. The zone line says `Your time: …` on a personal row and `Server time: …` on a server-wide row with a `zone`. The row's reset arrow sits in the field's own gutter cell. The two functions are dropped with the page on unload. |
| `Keybind(id, label, help, showIf, enableIf, needsServerPart, fallback)` | The capture control for a bind registered with `ctx.Keybinds` (it leaves the automatic Keys section). An id nothing registered is skipped and logged once. |
| `Button(caption, action, confirm, help, showIf, enableIf, needsServerPart, fallback, label)` | A named row: `label` (default: the caption) on the left and a normal-size button captioned `caption` in the control column; a registered action's row is its name with a **Run** button. Two buttons (or info lines) with one caption get distinct ids (`…/button/Run`, `…/button/Run#2`); `confirm` asks that question first ([confirm dialog](#common-options)). `Done.` or `Failed: <message>` goes on its help line; a throwing action is logged. |
| `Info(label, Func<string> value, refreshSeconds = 1, help, showIf)` | A read-only line re-read every `refreshSeconds` (clamped to 0.25–60; 0 reads once) while the page is shown. |
| `Divider()` | A thin rule in the current section. |
| `InScope(SettingScope)` | JOINFLOW1 (source-checked, not live-verified): after a `Button` or `Info`, `InScope(SettingScope.ServerWide)` draws it under **Server settings** with the server-wide rows it belongs to instead of under Your settings. It is never `Admin only` (only setting rows are), but it takes the server-wide availability: grayed while not connected, while waiting for the server, or when the server lacks this mod's server part (Progenitor's own page counts as present once the handshake said ok). Any other scope leaves it personal. Throws `InvalidOperationException` after a setting row or a divider (a setting row's scope comes from its entry). |
| `HideWhenAlone()` | Settings scopes step 4: the row added last is hidden, not grayed, while the half it needs is absent ([details](#settings-scopes-on-the-page-step-4)). |

**Common options** on every setting method: `label` (replaces the derived display name), `help` (the help line; the entry's description otherwise), `showIf`, `enableIf`, `needsRestart`, `needsServerPart`, `fallback`, `scope`.

The entries stay the source of truth: the page reads them each time it is drawn and writes a change through `SettingsCatalog.Submit` exactly as a stage 1 row does. The builder's presentation (label, help, bounds, step, units, choices, hint) is laid over a registered `SettingBinding`'s: a field the builder sets wins.

**Hot reload.** The page holds the plugin's delegates (predicates, button actions, info functions) in Core only. Nothing is handed to Bootstrap or IL2CPP beyond the kit's existing Core-owned slots. An info line's timer is posted (`MainThread.PostDelayed`) with the **page panel's** owner (the Mods workspace, Core), not the plugin's; it stops because the handle is disposed with the page, and it reads nothing once disposed. Disposing the registration (every `PluginUnloadReason` does, through `ctx.Track`; the owner sweep `ModsMenu.RemoveAll` is the second line) drops the registration's delegates (predicates, button actions, info functions), disposes the admin-edit registrations the page made, closes the mod's page if it is on show, and with it the Reset-all and confirm dialogs; closing or rebuilding the page drops the page's own copies of the actions and info functions (`ModSettingsView.Release`).

### Common options

| Option | Behavior |
|---|---|
| `help` | The help line under the name, and the description column's body. There is no hover tooltip widget: the column (or, on a narrow pane, the line under the name) is where the help shows (and in the readback as `help`). A sentence of the help that starts with `Applies` is the setting's timing note: it stays in the column while one of the row's [options](#option-descriptions) is hovered. |
| `showIf` / `enableIf` (`Func<bool>`) | Evaluated when the page is built and **after every committed change on the page** (a commit, a reset, a button). show-if hides the row in place and enable-if grays it in place (`IUiHandle.Interactable`), as search hides rows — nothing is rebuilt. A grayed row refuses a commit that still reaches it (logged) and hides its reset link. A predicate that throws is logged once (`Ui: settings show-if of <id> threw: …`) and the row is treated as shown / enabled. Not re-evaluated on a timer: a predicate that reads something outside the page changes at the next change or page build. |
| `ShowWhen(SettingCondition)` / `EnableWhen(SettingCondition)`, `ShowIf(entry, value)` / `EnableIf(entry, value)` (builder methods on the row added last) | The same hiding and graying from a declarative rule: another setting of the mod compared with a constant, a server fact (`ServerHasMod`), and All/Any of those. Locally it is combined with the row's delegate (both must hold) and evaluated at the same moments. Unlike a delegate it is data: a published setting carries it to clients without the mod's DLL ([presentation hints](settings-scopes.md#presentation-hints)). A `showIf` / `enableIf` delegate is never sent; on such a client it does nothing. |
| `needsRestart` | The value at registration (the mod's load) is remembered. While the stored value differs from it, the help line says `Pending restart.` and the status bar says `Pending restart · settings changed`; putting the value back clears both. A page opened while a change is pending starts that way. |
| `needsServerPart` + `fallback` | Grayed with a reason and the fallback until the mod's server part is **known** to run: the server's handshake request named the mod and the mod is not paused (`ModSettingsLayout.ServerPartForSettings`). **Unknown grays**, because only the handshake request is positive evidence — so the row is grayed offline, before the handshake, and on a server that did not name the mod. Since settings scopes step 4 the words are the framework's [availability reasons](#settings-scopes-on-the-page-step-4): `Not connected to a server.`, `Waiting for the server…`, `Server doesn't run Progenitor.`, else `Needs the server part of this mod.` (a pause or a server that did not name the mod). Unlike the status bar's server-part cell, the sides are not required: the request is direct evidence. Read from the inventory on every evaluation. The server part names itself in that request only when it declares itself: `[NetworkCompatibility]` on the plugin type, or `ctx.Handshake.DeclareSelf()` / `RequireSelf()` in its server `OnLoad`; without either the rows stay grayed on every server, and registering such a page logs a warning (RESEND1). A server part hot-loaded or unloaded while players are connected re-sends the request about a second later to every connected client whose handshake is ok, and to a client still joining once its answer is accepted (RESEND1), so the rows turn on or off in place, without a rejoin (source-checked and game-free tested, not live-verified). |
| `scope` (`SettingScope.Local` / `SettingScope.Server`, also named `Personal` / `ServerWide`: the same values) | A declared marker on the help line (`Local to this client.` / `Server-set.`) and the readback's `scopeDeclared`. `Server` on an entry bound `synced: true` opts it into administrator edits (`ServerSettings.Register`, unless `ctx.Menu.AllowServerEdits` already did), so the row takes the admin request path while joined; the registration is released with the page. **Bind the entry synced before `ctx.Settings.Page`**: the server has no page to draw, so it opts the entry in only at that call. An entry not synced then is logged, stays local, and the client page retries each time it is drawn (so a client that binds it later still gets the request path). |
| `confirm` (buttons) | The kit dialog (`<question>`, **<caption>** / **No**; Escape is No). Only Yes runs the action. The dialog belongs to its button: it closes when the page is rebuilt, another mod is selected, the workspace closes or the mod unloads, and a Yes after the button is gone runs nothing. |

### Option descriptions

OPTIONHOVER1 (requested 2026-10-06, "When hovering over the different settings / difficulties the tool tip should update"). An option of a choice row can carry a short explanation of what it does:

```csharp
ctx.Settings.Page(page => page
    .Section("Combat")
        .Dropdown(difficulty, help: "How hard the fights are. Applies at the next restart.")
            .OptionDescriptions("Enemies hit softer.", "The game's balance.", "Enemies hit harder and have more health.")
        .Radio(repeat, choices: new[] { "None", "Days" })
            .OptionDescriptions("One time.", "Every few days."));
```

- **Where it shows.** While the pointer is over the option (a line of the dropdown's open list, a radio line), the description column reads: the setting's name, `Easy: Enemies hit softer.`, then the setting's timing note (`Applies at the next restart.`) and its other notes. A radio list also draws each explanation under its option, one line, cut with an ellipsis; keep them short. The dropdown's list draws only the labels.
- **Order.** One entry per choice, in the order the row lists them: the `choices` given, else the enum's names, else the `AcceptableValueList`'s values. `""` is an option with none. Extra entries are ignored and missing ones are empty, except that a row with explicit `choices` must give exactly one per choice.
- **Other ways in.** `SettingPresentation.ChoiceDescriptions` (a registered presentation; the builder's wins when both set it); `IUiPanel.Dropdown(text, values, value, labels, descriptions)` and `Radio(text, values, value, labels, descriptions)` on a plain panel.
- **Status.** Source-checked, built and game-free tested (`OptionHoverDetailsTests`), not live-verified.

## Readback (`mods_ui widget_read`)

Every key is on the row that draws it; values marked *live* are read when described, so a reader sees a commit without a rebuild.

| Key | Row | Value |
|---|---|---|
| `page-framework` *(live)* | status bar | `{switch {visible, on, interactive, reason, mode (live\|restart\|unavailable\|refused\|hidden), pending (turns-off\|turns-on\|removed\|"")}, status (… failed\|problems), word, tone, restart (load\|uninstall\|switch-off\|switch-on\|remove\|settings\|""), version, versionText, sides, sidesWord, serverPart, serverPartText, conflicts, conflictChip, problems [{severity, kind, mod, sentence, whatToDo, otherMod, key, listLine}], limited, action, text, band, owner: "framework"}` |
| `page-problem` | a problem line | one problem's readback (as in `page-framework.problems`) plus `line`, the sentence drawn |
| `page-problems` | the last problem line (which carries Retry and Show all) | `{count, shown, lines, showAll, retry, conflicts}` |
| `page-status` *(live)* | status bar | `{state: installed\|pendload\|penduninst, status: enabled\|disabled\|paused\|pending-restart\|not-on-client, word, tone, cell}` |
| `page-action`, `page-badges` | status bar | the action caption and tone; `[sidesWord]` |
| `page-header` | title row (default) or the status bar (custom) | `{kind: default\|custom, error}`; custom adds `rows` |
| `page-title`, `page-meta`, `page-oneliner` | default mod area | as MODSPAGE1; `page-oneliner` is the by-line, `page-meta` is on the button row. There is no `like` key (removed). |
| `page-store-link` | the header's button row | `{kind: button, caption: Store page, tooltip, url, cell: store, shown}`; the button is drawn only when `url` is known |
| `page-settings` | settings header | `{kind: progenitor\|none}` |
| `page-settings-search` *(live)* | settings header | `{query, total, shown, threshold}` |
| `page-settings-reset-all` *(live)* | settings header | `{resettable, hidden: [ids show-if hides], pendingRestart}` |
| `page-section` | section heading (its title cell takes the whole row, so a title is never cut short at normal lengths) | `{title, kind: page\|group\|general\|keys\|actions, rows, items}` |
| `page-setting` *(live)* | a setting's row | `{id, kind: setting, section, key, label (the cfg caption), displayName, control, value, default, modified, resettable, editPath, scope, scopeDeclared, bounds, help, helpLine, visible, enabled, why, needsRestart, pending, needsServerPart, greyed, reason, fallback}` (`reason` is the grayed reason alone and `fallback` the fallback alone; the help line and `why` join them) plus, on a keybind row, `alsoUsedBy` (the other owners' names, or empty), `active` (false when the key lost its chord, KEYS1) and `conflict` (the winner rule's `{with, winner, winnerAction, rule, reason, enforced}`, or null; [mods-menu.md](mods-menu.md#keybinds-tab)); plus `choices` for a dropdown, segmented picker or radio; `choices` and `selected` for a multi-select; `multiline` and `lines` for a text area; `min`, `max`, `step`, `units` for a number or slider |
| `page-setting` *(live)* | a builder button / info line | `{id, kind: button\|info, key (the id's tail: button/Run, info/Local time#2), section, control: button\|info, label, displayName, help, helpLine, visible, enabled, why, needsServerPart, greyed, reason, fallback}` plus `confirm` (button) or `value`, `refreshSeconds` (info) |
| `page-setting-reset` *(live)* | the reset arrow | `{id, visible, state: changed\|default\|none, tooltip}` (the arrow is drawn at the default too, dim) |
| `page-settings-description` *(live)* | settings header | the description column: `{row, source: option\|hover\|selected\|first\|none, heading, body, notes, tone: warn\|muted\|"", option, optionLabel, optionDescription, text, settingsArt {parts {<part> {source, donor, sprite, copy}}, borrowed, donorSprites}}`; `option*` are empty unless `source` is `option`; `text` is the heading, body and notes a line each. An embedded view (the Server tab, Preferences) has no settings header: read the same object as `mods_ui widget_read`'s top-level `details`. |
| `page-divider` | a divider | `{section}` |
| `page-settings-drawn` *(live)* | settings header | What the runtime drew, read from the live objects: `{buttonArt {donor: key-cap\|menu-button\|popup-button\|kit, sprite}, pendingSkins, rows [{kind, name, grid, lit, band {…}, images [{path, sprite, color [r,g,b,a], type, enabled, ppu, texture: alive\|released\|"", rim, transition, hover, selectedSprite}], text {color, size, autoSize}}]}`. A header cell row lists its buttons and fields with their `text`. Null from a runtime that draws nothing. |

**Date-time and admin readback (JOINFLOW1, source-checked and game-free tested, not live-verified).** A `page-setting` row whose builder asked `.DateTime(...)` adds `iso` (the stored ISO UTC value), `display` (what the value box shows), `zone` (the zone line, `Your time: UTC+02:00 (…)`), `seconds` (the picker edits seconds) and `pickerOpen`; drawn as the picker it reads `control: datetime` (and, on a personal row with a reset, `reset: changed|default`), read-only it keeps `control: readonly`. An administrator's date or choice row adds `editor` and `requestPending`, with `choices` on a choice row ([admin rows](#administrator-date-and-choice-rows)). The open picker is read with `mods_ui datetime_read` ([bridge-ops-ui.md](bridge-ops-ui.md#mods_ui-date-time-picker-actions-joinflow1)).

A row show-if hides is not on screen, so it has no row to read; its id is in `page-settings-reset-all.hidden`. The rows' own `kind` names the controls: `dropdown`, `number`, `slider-field`, `color`, `keybind`, and stage 2 `radio`, `multi-select`, `textarea`, `info`. The key `page-setting-note` of stage 1 is no longer drawn: the note is the row's help line.

## Text settings: limits and plain text

TEXTAREA1. **Status: source-checked and game-free tested (`CaretLayoutAndTextInputTests`, `ModSettingsStage2Tests`), not live-verified.** The cfg round trip below was run against this machine's `BepInEx.Core.dll` outside the game; nothing here was observed in the game.

| Path | Limit | Over the limit |
|---|---|---|
| Settings text field (`Text`) | `MaxLength(n)`, else **256** characters (`ModSettingsLayout.DefaultTextMaxLength`), raised to the stored value's length | Typing and paste stop at the limit (TMP `characterLimit`, read by the typed-key pump). |
| Number box, slider value box, a number shown in a text field (an admin's server-request row) | Typing filter (TEXTFILTER1): digits and one leading `-` or `+`, plus one `.` when the number has decimals | A rejected character is not inserted, on every typing path and in paste; the commit still checks the bounds. |
| Color hex box | Typing filter: one leading `#` and `0-9 A-F a-f`; 7 characters (`#RRGGBB`), 9 with alpha | As above. |
| Settings text area (`Text(multiline: true)`) | `MaxLength(n)`, else **4,096** characters (`DefaultTextAreaMaxLength`), raised to the stored value's length | As above. |
| Settings model (`SettingBinding`, `SettingsRow.TryWrite`) | the author's `MaxLength` only | The write is refused: `value is longer than N characters`. |
| BepInEx cfg | none | One line per value, rewritten whole on every save. |
| Admin edit wire (`'PrSe'`, `ServerSettingsWire`) | 2,048 UTF-8 bytes per text field the client encodes, 12 KiB per packet | The client refuses before sending, in the server's words (below). A request whose expected or requested value is longer (a client that skipped its check) is still decoded up to the packet's 12 KiB, so the server refuses it, answers and audits it (game-free tested with a hand-built packet; not exercised live). |
| Server-wide value (`ServerSettings.Decide`) | the [server-wide text limits](#server-wide-text-limits-textlimit1); control characters other than CR, LF and Tab are refused | `The value is longer than 1,000 characters (1,023); shorten it to 1,000.`, `The value is too long to send: …`, `The value has a backslash before a letter …`, `The value contains control characters.` |
| ConfigSync broadcast | 4,096 bytes per field after BepInEx's cfg escape, 15 KiB per packet | A value that passed the server-wide check always fits (below). Too large at registration throws `ArgumentException`; a value set some other way (a hand-edited cfg) that grows past it is skipped with a server log warning and clients keep the old value. |
| Display | a text area shows `lines` lines; a field shows one line | A text area scrolls to keep the caret's line in view (TMP's own scroll, driven by the caret the pump pushes, with the framework moving the text when the caret line is still outside: TEXTAREA2). Text past the viewport is clipped by its mask (`field.viewportMasked`, `drawsOutsideFrame` in the test readback). A single-line field does not scroll sideways: text past its right edge is clipped. |

A paste is cut to the room left in the field. The caret layout lays out at most the visible lines plus one and probes long words in doubling prefixes, so a long paste does not cost a full text layout per keystroke (game-free bound: under 2,000 measure calls for a 1,000,000-character paste).

**Plain text.** A setting value is data, never markup:

| Where a value or another mod's text is drawn | Status |
|---|---|
| The field itself (built or cloned from a vanilla donor) | `richText` off on the field and its text component. |
| Info rows, including `Locked · <value>` and `Admin only · <value>` | The value label has `richText` off. |
| Description column (other mods' cfg descriptions, `The default is X.`, server errors) | `richText` off. |
| Cells and scroll lines (Install listing names and descriptions) | Rich text on for the framework's own colors; untrusted text is escaped with one `noparse` run per `<` (`UiRichText.Literal`), so text containing `</noparse>` can no longer close the run. |
| Row names and the help line | Rich text on (TMP's default). A mod's own help or a cfg key containing `<…>` is drawn as markup; mod authors are semi-trusted. |
| BepInEx log lines that echo a value | Escaped and cut to 120 characters (`SettingsAuditLog.Clean`): backslash, CR, LF, Tab and `"` escaped, other control characters as `?`. |
| `progenitor-settings.log` (server audit) | The same cleaner. |

**BepInEx cfg round trip (run outside the game, 2026-10-04, `BepInEx.Core.dll` from this machine's client).** Line breaks, `[Section]` lines, `=`, `#`, quotes, Tab, NUL and `<size=…>` all came back unchanged, and no entry or section was injected. Two values did not round-trip: a typed backslash-n (two characters) came back as a line break, and leading and trailing spaces were trimmed. This is BepInEx's escaping; Progenitor writes no cfg text of its own.

**Leading and trailing spaces are trimmed at commit (TEXTUNDO1 polish; source-checked and game-free tested in `TextUndoTests`, `ServerTextLimitTests` and `TextExpandTests`, not live-verified).** BepInEx 6 (`ConfigFile.Reload`, read with the IL viewer from this machine's `BepInEx.Core.dll`) trims each cfg line, and the value after `=`, with `string.Trim()` when it **reads** the file. `Save` writes the value unchanged. The string escape writes Tab, LF, CR, VT, FF, NUL, BEL and BS as backslash escapes, so those survive. A space, a no-break space or another Unicode space at either end does not. Without trimming, a committed value kept its trailing space in memory until the next reload, then lost it. Live, Done committed 1,000 characters ending in a space, and the server stored 999. Now every text commit applies the cfg rule first (`UiCommitText.Trim`): the whitespace at either end is removed, except Tab and line breaks. The trim runs before the length check:

- a Personal text field or text area in `ModSettingsView.Commit`, after the unchanged-value check, so focusing a field and leaving it without an edit never rewrites a stored value (a selector's choice is written as it is);
- an admin row's Apply and the editor's Done, in the client's pre-check (`ServerSettings.Request`);
- the server's own check (`ServerSettings.Decide`), so a client that does not trim cannot store a value the next read would shorten.

The field then shows the trimmed value, so the text after a commit is the stored text. Personal text is trimmed too: its cfg reload would trim it anyway. Programmatic writes (`SettingsRow.TryWrite`, a plugin's own `ConfigEntry`) are not trimmed.

**A Personal setting is saved at once while joined (SAVEHOLD1; source-checked and game-free tested in `SettingPersistenceTests`, not live-verified).** While connected, ConfigSync turns `SaveOnConfigSet` off for a mod's whole cfg file when it locks a synced setting in it to the server's value. Before this fix, a Personal setting in the same file, such as the Sample's Notes and Demo count next to its synced Server notice, changed only in memory until the disconnect saved the file. Live, a commit in a rejoined session was missing from the cfg until then. A crash or quit while joined would have lost it, and any reload of the file while joined put the old disk value back. Main had the same defect. Every page write (`SettingsRow.TryWrite`) now saves such a file at once (`ConfigSync.TrySaveHeldFile`), as KEYS4 already did for key writes (`Keybinds.Persist`). The locked synced keys are parked with the player's pre-join values, so the save never writes a server value. If one could not be parked, nothing is saved and the readback says why. A file whose mod turned `SaveOnConfigSet` off itself is left alone. A write that does not go through the page, such as a plugin setting a non-synced `ConfigEntry<T>.Value` itself, is saved by ConfigSync after a 1.5 s debounce (10 s at most) through the same parked save, and flushed on disconnect, plugin unload, Core unload and a normal quit (LOCALSAVE1, [config-sync.md](config-sync.md#local-settings-in-a-held-cfg)). The readback `page-setting.persist` reports how the last write was saved and whether the key's value on disk matches ([dev loop](bridge-ops-ui.md#settings-reach-the-cfg-while-joined-savehold1)).

**Undo and redo** in every settings field ([ui.md](ui.md#undo-and-redo-textundo1); source-checked and game-free tested, not live-verified): **Ctrl+Z** undoes, and **Ctrl+Y** or **Ctrl+Shift+Z** redoes. Typing undoes a word at a time. A paste, a cut, a deleted selection and a run of Backspaces or Deletes each undo in one step. The caret and the selection come back with the text. The history is per field and ends when the field loses focus, so a committed value cannot be undone after leaving the field. It also ends when the value changes from outside (the commit's trim, a server echo, a reset). The expanded editor has its own history while it is open: Cancel discards it with the text, and Done commits. A read-only field or editor ignores Ctrl+Z.

No setting value reaches a chat command, a file path, a process start or a shell, and no format string is built from one (source-checked).

### Server-wide text limits (TEXTLIMIT1)

**Status: source-checked and game-free tested (`ServerTextLimitTests`, `TextExpandTests`), not live-verified.**

The server checks every administrator edit of a server-wide text setting against that setting's own limit, from **its own** declaration: the `.Text(…)` row of the mod's `ctx.Settings.Page` (which runs on the server too) or a `SettingPresentation.MaxLength` binding, recorded by `ServerSettings.DeclareText` (when a page and a binding both declare a row, the most recent live declaration applies). No limit travels in the request, so a client that believes in a larger limit, or skips its own check, is refused the same way. The client applies the same check (its own declaration) before sending, so the refusal normally appears at once, in the server's words. Every refusal is answered to the client (the row's help line) and audited to `progenitor-settings.log`.

**Choice: cap, do not raise or chunk.** The admin edit packet carries a text field of 2,048 UTF-8 bytes (the request also carries the current value), and the game's network buffers are 16 KiB, so a 4,096-character value of three-byte characters could not travel in one packet either way. Capping the server-wide limit is the simplest correct option: no protocol change, and an older Core on either side still reads every packet. ConfigSync needs no cap of its own: BepInEx's cfg escape turns a one-byte character into at most two bytes and leaves the rest alone, so a value within 2,048 UTF-8 bytes is within ConfigSync's 4,096 escaped bytes (the server still measures it, and refuses rather than desync).

| Text setting | Character limit | Byte limits | Refusal |
|---|---|---|---|
| Personal text field | `MaxLength(n)`, else **256** | none (not sent) | the field stops typing; the model refuses `value is longer than N characters` |
| Personal text area | `MaxLength(n)`, else **4,096** | none (not sent) | as above |
| Server-wide text field | `MaxLength(n)`, else 256, **at most 2,048** | **2,048** UTF-8 bytes (packet); **4,096** escaped bytes (ConfigSync, implied) | `The value is longer than N characters (L); shorten it to N.` / `The value is too long to send: B of 2,048 bytes (an accented or non-Latin character takes 2 to 4).` / the backslash refusal below |
| Server-wide text area | `MaxLength(n)`, else 4,096, **at most 2,048** | as above | as above |
| Server-wide row nobody declared (`AllowServerEdits` with no page row) | 256 (a single-line field) | as above | as above |

Characters are UTF-16 code units (`string.Length`), what the field counts: an accented letter is one, an emoji two. In bytes an ASCII character is one, an accented letter two, most other scripts three, an emoji four. So the Sample's Server notice (`MaxLength(1000)`) takes 1,000 ASCII or accented characters, but about 680 characters of a three-byte script. A server-wide value already longer than its limit (a hand-edited cfg) is shown whole; the expanded editor's counter reads `1,023 / 1,000 (23 over)`, and a request to keep it is refused (`shorten it to 1,000`) until it is shortened.

**Backslashes.** BepInEx writes a backslash in a string value as itself but reads `\n`, `\t`, `\r`, `\0`, `\a`, `\b`, `\v`, `\f`, `\'`, `\"` and `\\` as escapes, so `C:\new\tools` would come back from the cfg, and reach clients through ConfigSync, as `C:`, a line break, `ew`, a Tab and `ools` — after which the server's value no longer matches what the admin's page shows, and every later edit is refused as changed on the server. The server (and the client before sending) therefore refuses a value that the cfg escape would change: `The value has a backslash before a letter the config file reads as an escape (such as \n or \t), so it would not be saved as typed. Use / instead, or put a space after the backslash.` Escaping backslashes ourselves before BepInEx writes was rejected: BepInEx's own converter is what reads every existing cfg, and changing what Progenitor writes would make values already saved by BepInEx read back differently. Refusing is the option that cannot alter an existing cfg.

**After a refusal** the admin's typed text stays in the field whatever the reason (its length, not an admin, a conflict, outside the accepted values, a timeout, a failed send), and the help line adds `Your text is kept: Apply sends it again, or leave the page to discard it.` The next Apply is checked against the server's current value. An accepted value is replaced by what the server stored.

### Editing a server-wide text area

A server-wide string the builder declares multi-line (`.Text(entry, multiline: true, scope: SettingScope.Server)`) is, for an administrator, the same text area as a Personal one, with **Apply** under it: Enter starts a line, and the line breaks travel unchanged through the request, the server's checks, the cfg save (BepInEx escapes them), ConfigSync and the client's synced copy (game-free round trip in `ServerTextLimitTests`). For everyone else the row reads `Admin only · <first line>` and its expand button opens the whole value read-only. The readback `page-setting` of such a row adds `multiline`, `lines`, `maxLength` (the server-wide limit) and `expandable`. The separate settings pane (`mods_ui select page=settings`) keeps a single-line field for every admin row.

The Sample's **Server notice** (`Menu/ServerNotice`, synced, `scope: Server`, 4 lines, `MaxLength(1000)`) is the example. Client and server must run the same Sample build: the handshake refuses a join when the versions differ, and the row only exists in this build.

### The expanded editor (TEXTEXPAND1)

**Status: source-checked and game-free tested (`TextExpandTests`), not live-verified.** Nothing here has been seen in the game.

Every text area on a mod's settings page has a small expand button (`⤢`, or `…` when the game font lacks the glyph) in its bottom-right corner; the text wraps before it. A read-only multi-line value (`Admin only`, `Locked`) has it in the row's gutter. It opens the large editor:

- **The window** is the game's popup frame the Install tab's README reader uses (640 wide, about 23 lines; refined from a pane-sized overlay so it matches the game's own popups and stays inside 80 % of a 1,080 canvas). The title is the setting's name.
- **The editor** is the settings value box with the same text pipeline as every field: the owned caret, layout-aware typing, filters, the limit, paste with line breaks. A vertical scroll bar in the game's `ScrollBar_Bar` / `ScrollBar_Handle` art (the kit's colors when they were not borrowed) shows and drags the view; it hides while the text fits. The bar is a bottom-to-top `Scrollbar`: its `value` is 1 with the start of the text in view and 0 with the end in view (`value = 1 − fraction`, where the readback's `view.fraction` is 0 at the top and 1 at the end).
- **A live counter** under it: `1,234 / 4,096` (the row's limit: the field's cap for a Personal row, the server-wide limit for an admin row), or `1,234 characters` read-only.
- **Done** commits with the inline field's rules: a Personal row through the same write as its end of edit; an admin row puts the text in its field and sends it as **Apply** does, after the server-wide check (an error keeps the editor open with the reason under it). **Cancel** and **Escape** discard. Enter starts a line inside the editor. A read-only value has **Close** only (Enter closes it too); typing, paste, cut, Backspace, Delete and Ctrl+Z change nothing (the framework refuses the edit, and the field is TMP-read-only like every armed field). It draws no caret (TEXTUNDO1 polish). The arrow keys still move the view, and Ctrl+A and Ctrl+C still copy.
- **The wheel** scrolls a text area's text, and the editor's, three lines a notch while it can move — only a field of the panel under the pointer, inside that panel's visible list, and with a dialog open only the dialog's; at the end of the text the notch scrolls the page. A press on the editor's scroll bar or a text area's expand button does not focus the field or move its view. With a dialog open, a click reaches no field behind it. A view the player scrolled stays where it is (the caret hides while its line is out of view) until the caret or the text moves.
- **Cost:** no writes while idle. The bar is touched only on the frames after an edit, a caret move, the wheel or a drag; an open, idle editor costs one liveness check of its field and bar and one counter check a frame, and a page with no editor open costs nothing (`mods_ui perf`: `scrollWrites`, `caretWrites` stay 0).

The editor belongs to the page: it closes, discarding, when the page is rebuilt, another mod is selected or the workspace closes. Readback: `page-setting-expand {id, visible, tooltip, readOnly, open}` per button, and `mods_ui status` → `dialog.editor {readOnly, maxLength, length, counter, done, cancel}`. Bridge tests: [bridge-ops-ui.md](bridge-ops-ui.md#mods_ui-text-field-tests-texttest1).

## Published controls

Stage 1 published `Dropdown`, `Number`, `SliderField`, `ColorField` and `KeyField` on `IUiPanel`. Stage 2 adds `TextArea`, `Radio`, `MultiSelect`, `Info`, `Divider`, `ConfirmButton` and `IUiHandle.Interactable`. JOINFLOW1 adds `DateTimeField` (`IUiDateTimeField`; source-checked and game-free tested, not live-verified). Contracts: [api.md](api.md#ui); drawing and commit rules: [ui.md](ui.md#stage-2-controls-settings1-source-checked-and-game-free-tested-not-live-verified).

## Stage 3: the enable switch

**Status: source-checked and game-free tested (`ModStage3Tests`, `PluginFileActionsTests`), not live-verified.** The switch on the status bar works. What it does depends on who owns the mod's files (`ModSwitch.Decide`):

| Mod | Off | On | Mode |
|---|---|---|---|
| Hot-loadable Progenitor mod (`progenitor-reload/`, no `ctx.UnsafeHarmony`) | Unloaded now with `PluginUnloadReason.Suspend`, through the Bootstrap suspend table [per-server mod pause](mods-menu.md#per-server-mod-pause) uses. Its DLL siblings and the reload plugins that depend on it go with it. | Loaded again now (`ResumePlugins`). A mod whose previous plugin context has not been collected reads `Resuming…` until it loads. | live |
| Restart-only Progenitor mod (`BepInEx/plugins`, or `ctx.UnsafeHarmony`) | Recorded; the status reads `Turns off at next start`. At the next start `ProgenitorPlugin.Load` skips its `OnLoad`, and the `OnLoad` of every plugin that hard-depends on it (`[BepInDependency]`), so nothing of it runs: `Disabled`. | Recorded; `Turns on at next start`, then it loads normally. Pressing the switch back before the restart puts the choice back and nothing changes. | restart |
| Ordinary BepInEx plugin whose files are known ([below](#ordinary-bepinex-plugins)) | A next-start request: the preloader renames the mod's files to `*.old` before the chainloader starts. `Turns off at next start`, then `Disabled`. | A next-start request that renames them back. | restart |
| Ordinary plugin whose files cannot be told apart | Not offered. The switch is not clickable and its reason says why, beginning `Managed elsewhere:` (for example `Its folder also holds Foo, so its files cannot be told apart.`). | — | unavailable |
| A plugin skipped because a hard dependency is off | Not switchable by itself: the switch is off and not clickable, the status reads `Off because <dependency> is off`, and the reason says to turn the dependency on. | — | unavailable |
| A `[ProgenitorSide]` plugin that does not run on this side (dormant) | Nothing to switch here: `It does not run on this side of the game, so there is nothing to switch here.` | — | unavailable |
| Progenitor itself (Core and Bootstrap share `progenitor`), the dev bridge, the preloader, BepInEx | Refused: `Progenitor needs this to run, so it cannot be turned off.` / `BepInEx loads every mod, so it cannot be turned off here.` The Sample and the other examples are not required parts and can be switched. | — | refused |

**Remembered per mod.** `BepInEx/config/progenitor-mod-state.json` (`{schema: 1, disabled: [{id, name, changedUtc, heldWith[]}]}`, `ModEnableState`), beside `progenitor-server-profiles.json` and written the same way (a temp file and `File.Replace`). Only mods that are off are listed; turning one on removes its entry. A file that exists but cannot be read turns nothing off (every mod loads, logged once) and the switch refuses to change anything until it can be read; it is never overwritten. Required parts in a hand-edited file are ignored.

**Disabled and paused are separate facts.** `Disabled` is the player's switch; `Paused for this server` is the per-server pause. A plugin the player turned off (and, while both are still in Bootstrap's pause table, the plugins a live switch took with it, `heldWith`) is *held*: `ServerModPause` never resumes it on a leave or a server request, never reports it as paused, and the Mods list and page read it `Disabled`, not paused. Should anything load it anyway (a Core swap, a Bootstrap resume that pulls it along), its `Load` skips `OnLoad`. Turning a held mod back on while the joined server's last pause took it keeps it paused until the player leaves (`Turned on. It stays paused for this server until you leave.`). **While the joined server has any mod paused, no switch action resumes anything:** Bootstrap's resume also brings back the paused plugins a released one depends on and those that depend on it, so turning a mod on only releases the player's hold (`Turned on. It loads when you leave this server.`) and **Retry** is refused (`Leave this server first, then press Retry.`). The `heldWith` list belongs to the process that recorded it (the entry stores that process's id and start time): in any other process it counts for nothing, and Core prunes it at load, before any plugin loads or any server pause asks. Within the process it counts only while the turned-off mod and the sibling are both still in Bootstrap's table. "Paused for this server" is read from the current join (the server's request this join, or its lack of Progenitor), never from the host's list of an earlier visit.

**Turning off a mod others need asks first.** When the switch would take other plugins with it — a live switch's Bootstrap closure (DLL siblings and dependants), or for a restart-only mod the running plugins that hard-depend on it, ordinary BepInEx plugins included — the framework dialog asks `Turn off <mod>?`, naming them (`Addon needs it and turns off now too.`), with **Turn off** / **No**. The dialog belongs to the page: a Yes after the page moved on changes nothing. Turning a mod on never asks. After **Turn off**, an ordinary plugin that hard-depends on a restart-only mod is turned off at the next start too when the preloader can name its files; any that cannot keep loading and show `It depends on <mod>, which is turned off.` (a warning on their own row) until the mod is on again or they are turned off. The switch-off records which ordinary plugins it turned off (`plainOff` in `progenitor-mod-state.json`); turning the mod on again brings exactly those back: a request still waiting is canceled, one already carried out gets a turn-on request. A plugin the player turned off separately is not recorded and stays off. A live switch-off of the mod on show steps the page off it first and back to it afterwards, so the unload does not close the workspace.

**What is not done.** Nothing here patches the chainloader or moves a Progenitor mod's file. A restart-only Progenitor mod stays on disk and loads as an inert plugin (no context, no registrations) while it is off.

**After a Core swap** the chainloader's Progenitor plugins (restart-only mods in `BepInEx/plugins`) do not run `Load` again, so the new Core generation records them from what the swap left in memory (`ModRegistry.NoteRehydrated`: running when the old instance still has a context, skipped when the player has it off, dormant for a side it does not run on, otherwise failed). Their load-time problems (a failed `OnLoad`, a file that did not load) are not known again until the next start. A plugin whose load *failed* is not running: nothing of it acts, so it is neither `Turns on at next start` nor `Turns off at next start`.

### Ordinary BepInEx plugins

**Status: source-checked and game-free tested, not live-verified; needs the preloader, which needs one game restart to install.** Decided 2026-09-29: offer turning off and uninstalling a mod Progenitor did not install only when Progenitor is sure which files are the mod's.

- **When it is offered** (`PluginFileActions.Identify`), in one of two layouts:
  - **A folder of its own** directly under `BepInEx/plugins` (`plugins/Author-Mod/…`, the layout r2modman and Thunderstore Mod Manager use): no other loaded plugin lives anywhere in that folder, no DLL in it declares another plugin, nothing in it is a link, and it holds at most 256 files.
  - **A loose single DLL** directly in `BepInEx/plugins` (the common hand-installed layout): its metadata declares exactly one `[BepInPlugin]`, and it is this mod's; no `<name>.dll.old` is already beside it.
  - Either way: it is not Progenitor's or BepInEx's, no other loaded plugin hard-depends on it, and its DLLs can be read. Otherwise the page keeps `Managed elsewhere: <why>`.
  - **Leftovers never block a loose DLL.** A file beside it that does not end in `.dll` or `.dll.old` (`<name>.dll.105.live.bak`, `<name>.dll.next.105`, `<name>.dll.prev`) is not loaded by the chainloader and is not the mod's file: it neither refuses the identification nor moves with the mod. Only `<name>.dll.old` beside it refuses, because a rename could not resolve it.
- **What the page and the readback say** (live finding 2026-10-07; the fix is built and game-free tested, not live-verified). Another tool's local DLL that the switch treats as an ordinary plugin never shows the generic `Installed by another tool — remove it with that tool.` Its provenance line is one of:
  - `Installed by another tool. Progenitor can turn it off or uninstall it at the next start.` when its files are identified (the page offers the switch and **Uninstall**);
  - `Managed elsewhere: <why>` with the identification's own reason otherwise, for example `Managed elsewhere: Mod.dll holds more than one plugin, so turning it off would turn the others off too.` or, when no identification could be made at all, `Managed elsewhere: Progenitor cannot see which file this mod was loaded from.` (the log then names the error);
  - the turned-off note below while the preloader holds it off.

  The Mods readback (`mods_ui status`, `entries[]`) reports the same decision: `removable` is true exactly when its files are identified, and `provenanceLine` is the page's line (`ModsMenu.ReadbackProvenanceOf`). Before this fix the readback said `removable: false` and the generic line for every such row. Tests: `ModStage3Tests` (`A_loose_dll_with_leftovers_reads_removable_with_the_pages_own_line`, `A_local_dll_without_ordinary_actions_says_managed_elsewhere_with_the_reason`) and `PluginFileActionsTests.Loose_dll_with_bak_and_next_leftovers_is_certain_and_touches_only_the_dll`.
- **While the preloader holds it off** the plugin is not loaded, so nothing reports its version: the status bar reads it from the renamed DLL's own `[BepInPlugin]` metadata (`ModSwitch.DisabledVersionOf`), and the provenance line says `Installed by another tool. Turned off by Progenitor; turn it on to load it at the next start.` instead of `Managed elsewhere`.
- **How it is done.** The switch and **Uninstall** write a request to `BepInEx/config/progenitor-plugin-actions.json`; the `Progenitor.Preloader` patcher in `BepInEx/patchers` carries it out at the next start, before the chainloader opens any plugin file, and records the result in the same file. Turn off renames every file in the folder to `<name>.old` (the r2modman convention; the chainloader skips `*.dll.old`); turn on renames them back; uninstall moves the folder to `BepInEx/progenitor-removed/<yyyyMMdd-HHmmss>/<folder>/`. **A loose DLL:** turn off renames only that DLL to `<name>.dll.old` and turn on renames it back; uninstall moves only that DLL, plus files named exactly `<stem>.README.md` or `<stem>.md` beside it, into `BepInEx/progenitor-removed/<stamp>/<stem>/`. Nothing else is touched: not `*.bak`, `*.next`, `*.prev` or `*.disabled` leftovers (another tool's `*.dll.disabled` is left alone and not listed), not a `.cfg`, and never a DLL that declares no plugin (such as `System.*.dll`, which other mods may share). Each DLL's sha256 is checked against the request first, and a request is all or nothing.
- **What is never touched:** anything outside `BepInEx/plugins`, any path with a `Progenitor*` or `BepInEx*` segment, a link — the folder is walked by hand, a link anywhere inside it refuses the identification, and every folder between the mod's folder and each file is checked again for a link when the request is carried out (a folder swapped for a junction after the request is refused) — or a file whose bytes changed since the request.
- **Ownership:** every DLL in the folder (loaded or not) is read for a `[BepInPlugin]` attribute; another plugin's GUID refuses the folder. A DLL that cannot be read refuses it too, as does any path written with 8.3 short names that resolves to a protected name.
- **Safe to repeat:** a file already at the requested spelling with the same bytes counts as done. Before the first rename the preloader writes `BepInEx/config/progenitor-plugin-actions.journal`; a start that stopped half way is completed at the next start when every remaining file still matches, and undone otherwise. If undoing fails, the result says the folder is left partly renamed and names the files. Every read-modify-write of the state file holds a named lock (`Local\Progenitor-PluginActions-<hash of the root>`, 10 s timeout; a timeout is reported, never ignored), and temp files have unique names.
- **Never under a running game:** while another `VRising.exe` (in the game folder) or `VRisingServer.exe` (anywhere under it, the private-game `VRising_Server` child included) runs — and a game process whose path cannot be read counts as running — requests stay waiting and the preloader logs one line. A journal left by a start that stopped half way is then left as it is (nothing further is renamed or moved; it is finished or undone at the next start with no other game running), and a partly done entry is recorded as a plain failure naming the files left renamed.
- **An unreadable journal** is never discarded: it is renamed to `progenitor-plugin-actions.journal.corrupt-<stamp>`, and each plugin it still names gets a failure saying where its files may be (`BepInEx/progenitor-removed`, or renamed to `*.old`).
- **The switch never freezes the game:** the menu waits at most 250 ms for the lock; when another process holds it the press says `Busy: another game process is changing the Mods menu's requests. Try again in a moment.`
- **Files the mod already ships off** (`*.old`) are left exactly as they are: not renamed on, not recorded. A folder holding both `Foo.dll` and `Foo.dll.old` is refused up front (`Its folder holds both Foo.dll and Foo.dll.old, so it cannot be turned off by renaming.`).
- **Before the restart** the status reads `Turns off at next start` / `Turns on at next start` / `Removed at next start`; pressing the switch back, or **Cancel uninstall** for a removal, drops the request.
- **After the restart** the menu reconciles the result: a mod turned off is listed `Disabled` (it is no longer loaded, so the result file is how it is listed at all); an action that could not be carried out becomes a warning on the mod (`It could not be turned off at the last start: Mod.dll changed after the request, so nothing was done.`) instead of pretending.
- **To restore an uninstalled mod by hand,** move its folder from `BepInEx/progenitor-removed/<stamp>/` back into `BepInEx/plugins/` while the game is closed.
- **Deploying the preloader:** [dev-loop.md](dev-loop.md#deploying-the-preloader) (`tools/deploy.ps1 -Projects Progenitor.Preloader`, only while the target process is stopped).

## Mod problems

**Status: source-checked and game-free tested (`ModStage3Tests`), not live-verified.** Requested 2026-09-29: the status bar shows when a mod is broken or clashes with another mod, not only whether it is on.

**One problem record** (`ModProblem`): severity (critical / warning / info), kind, the mod, one plain sentence of what happened, one of what to do, details for the mod author, and the other mod involved. Two kinds of source feed `ModProblems`:

| Source | Severity | Sentence (example) | Clears |
|---|---|---|---|
| `OnLoad` threw (`ProgenitorPlugin.Load`) | critical, `Failed to load` | `It failed to load, so none of its features are running. Press Retry to load it again, or tell its author.` (a restart-only mod: `Restart the game to try again, or tell its author.`) | the next load of that mod (Retry, a redeploy), or its unload |
| Localization files did not load | warning, `Loaded with problems` | `Its translation files did not load, so some text may be missing.` | the next load, or the unload |
| JSON content files did not load | warning | `Its content files did not load, so some of its items or recipes may be missing.` | the next load, or the unload |
| Keybind conflict (KEYS1: the winner rule over the game's keys, every Progenitor bind and ordinary mods' key settings, read live) | warning, a conflict, **on the side that lost only** | `Not active: F9 is used by Hunts, installed first. Pick another key in its Keys section or in Mods > Keybinds.` (an ordinary mod's key: `F9 is also used by Hunts, installed first. Both fire; Progenitor can't stop this mod. Change it in Mods > Keybinds.`) | at once when either bind is rebound or unloaded |
| Chat command conflict (read live from the command registry) | warning, a conflict | `The command .hunt is also registered by Rifts, so no mod gets the short form. Use .p a hunt or .p b hunt instead.` (the registry's rule: a clashing short form is disabled for every owner; there is no winner) | at once when either registration goes |
| A next-start request the preloader could not carry out | warning | `It could not be turned off at the last start: …` | the next request for that mod |

A later source (the [M4.7 health check](roadmap.md)) adds its own kinds through `ModProblems.AddSource(IModProblemSource)`, with no second UI.

- **Status word, worst first:** `Failed to load` beats `Loaded with problems` beats everything else. A conflict never changes the word; the status bar gets a chip, `2 conflicts`.
- **The problems line**, directly under the status bar: one left-aligned line per problem (what happened, then what to do) at the help-text size (15), in the warn tone for a warning and the danger tone for a critical problem, worst first, at most three. **Retry** (a hot-loadable mod that failed to load: unloaded with `Suspend` and loaded again) and **Show all** are small buttons at the right end of the last line; no other row is added, so the gap to the mod's title is the usual one. **Show all** is drawn only when there is more to see: a problem past the three, or details for the mod author (an exception); a lone conflict, fully shown, has none. Changed after the live check of 2026-09-29 (the line was centered at body size and Show all was a row of its own); the fix is not re-checked live. **Show all** opens a framework notice, `Problems with <mod>`, with every problem and **Copy report**, which puts the report for the mod author (the mod, its version, Progenitor's version, each problem with its details) on the system clipboard. Where the runtime has no clipboard the notice shows the report text instead and says `Copying to the clipboard is not available here; the report is shown below.`
- **The Keys section:** a key in conflict ends its help line with the winner rule's sentence: the loser's `Not active: F9 is used by <mod>, installed first. Pick another key.`, the winner's `Also bound by <mod> (not active there).` (KEYS1). The section heading carries **Open in Keybinds**, which opens the [Keybinds tab](mods-menu.md#keybinds-tab) at this mod's section. A losing Progenitor bind does not fire.
- **The Mods list:** the entry's diamond is amber or red and its second line says the problem in words (`Failed to load`, `Key F9 also used by ClanResourceHUD`).
- **Report, never act:** a problem never disables, pauses or unloads a mod. The switch and **Retry** are the only actions.
- **Non-Progenitor mods:** only what the framework can see (a failed next-start action); nothing is guessed from their logs until M4.7.

## Counterparts

**Status: source-checked and game-free tested (`ModStage3Tests`), not live-verified.** User notes 2026-09-29. A mod declares how much of it works without its counterpart — the other side's part of the same mod — with one attribute beside `[NetworkCompatibility]`:

```csharp
[NetworkCompatibility(NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor)]
[Counterpart(CounterpartNeed.Limited, Features = new[] { "Server echo of the F8 press" })]
public sealed class Plugin : ProgenitorPlugin<CompanionContext> { … }
```

This is `samples/Progenitor.SampleCompanion/Plugin.cs` (compile-checked with its Release build).

| `CounterpartNeed` | Means | Effect |
|---|---|---|
| `Required` | Nothing works without the counterpart. | Today's behavior: a `ServerMustHave` row refuses the join or, for a hot-loaded client mod, pauses it for that server. |
| `Limited` | It runs with some features missing. | **On the client** its `ServerMustHave` row is declared `NoNeedForSync` (the version rule is kept; the load log says so), so it is never refused or paused for a server that lacks the counterpart. **On the server** nothing changes: the server still requires clients to have the mod. On such a server the status bar says `Server part not on this server · limited` and its `needsServerPart` rows are grayed with `Needs the server part of this mod.` and their fallback. |
| `Optional` | It works fully; the counterpart only adds extras. | On the client, the same row rule; nothing is grayed. On the server, `Optional` (or a non-empty `ClientExtras`) is the one explicit way a server mod lowers its requirement that clients have it. |

- **Which features need the counterpart:** `Features` names them in plain words; mark the matching settings rows `needsServerPart: true` with a `fallback` on `ctx.Settings.Page`.
- **Known absent** means the joined server sent a handshake request that carried rows and did not name the mod, and the request did not fill all 16 wire slots (a full request may have left mods out; the flag is recorded before the metadata row is stripped, so a full request that keeps 15 rows still counts as full). A full request or no request is unknown and grays nothing new (`CounterpartPolicy.RequestProvesAbsence`, `Handshake.ServerRequestTruncated`).
- A declared counterpart makes the mod two-sided on the status bar even when its handshake row alone would not.
- **Directional:** the attribute speaks about the other side's part. A client declaration never lowers the server's requirement; a server lowers it only through `Optional` or `ClientExtras`.
- **Pause rules kept:** a lowered row that fails on a version mismatch is decided exactly as its `ServerMustHave` row was, and a counterpart plugin does not make the DLL it shares unpausable — only its own pause for a missing server part is prevented.
- **Rule:** declare `Limited` or `Optional` only when the mod really runs without the other part — the handshake then no longer protects players from a missing counterpart.

### Reference examples: counterparts and scopes

**Status: source-checked and game-free tested (`CounterpartSamplesTests`), not live-verified.** Two samples cover both directions. Each README has the join matrix (join allowed or refused, paused or not, grayed rows and status bar per cell), the settings with their scopes, and an acceptance procedure.

| Sample | Shape | Declaration | Settings it shows |
|---|---|---|---|
| [Server-led sample](../samples/Progenitor.SampleServerLed/README.md) (`Progenitor.SampleServerLed`) | A server mod (periodic announcements, `.serverled status`) with an optional client part | `ServerMustHave` + `[Counterpart(Optional, ClientExtras = …)]`: `NoNeedForSync` on both sides | Server options for the server (`scope: Server`, synced, admin-editable); client options for the server (sent to the server over the mod's own custom event, honored per player); client options for the client (`scope: Local`) |
| [Client-led sample](../samples/Progenitor.SampleClientLed/README.md) (`Progenitor.SampleClientLed`) | A client mod (a clock) with a tiny optional server part | `NoNeedForSync` + `[Counterpart(Limited, Features = …)]` | Local clock settings; `needsServerPart` rows with fallbacks; a server-provided default (`scope: Server`) the client overrides |

- **Is my counterpart there?** A client plugin reads `ctx.Handshake.ServerPart` (`CounterpartPresence`): `Running` when the joined server's handshake request named the mod, `Absent` when a request that carried rows and did not fill all 16 wire slots did not, `Unknown` otherwise (not joined, no or an empty request, or a full one). It is the evidence the status bar and the `needsServerPart` rows use, and it is `Running` on the server. Send the server part a packet only while it is `Running` and `Handshake.LastClientResult == "ok"`; a vanilla or unrelated server never receives the mod's packets.
- **Does this player have the client part?** The server has no per-player handshake fact for plugins. In the server-led sample the client part's own preference packet is that evidence, and the server sends its popup packet only to players whose client part reported.
- **A client-led mod with an optional server part** declares `NoNeedForSync`, not `ServerMustHave` + `Limited`: one attribute speaks for both sides, and on the server `Limited` keeps `ServerMustHave`, so the server part would refuse every client without the client part.

## Stage 3 readback

`page-framework` carries `switch.mode`, `switch.pending`, `conflicts`, `conflictChip`, `problems` and `limited` (table [above](#readback-mods_ui-widget_read)); `page-problem` / `page-problems` are the problems line; `page-setting` on a keybind row carries `alsoUsedBy`. The Mods list's own readback is in [mods-menu.md](mods-menu.md#mods-list-modlist1).

## Acceptance procedure (stage 3, not run)

Client only unless a step says otherwise; the isolated test server only (`<testserver>`), never the live one. Requires a Core with this change (a Core swap; Bootstrap is unchanged), the Sample and `Progenitor.SampleCompanion` built from the same tree, and for steps 8 and 9 the preloader deployed while the game was stopped (`tools/deploy.ps1 -Projects Progenitor.Preloader`).

1. Open the Mods workspace, select the Sample. `mods_ui widget_read pane=content`: `page-framework.switch.interactive: true`, `switch.mode: live` (it is hot-loaded). Press the switch (`widget_click pane=content label=page-framework/switch`). Expected: log `Mods: switch progenitor.sample: off -> ok`, `Reload: … unload reason=Suspend`, the page still open with `status: disabled`, `switch.on: false`, the list entry's diamond `disabled` and second line `Disabled`, and `progenitor-mod-state.json` naming the Sample. Press it again: loaded, `status: enabled`.
2. Turn the Sample off, restart the client. Expected: log `Progenitor Sample … not loaded: turned off in the Mods menu.`, the page `Disabled`. Turn it on: it loads now (unloaded and loaded again), `Enabled`.
3. With the Sample and the companion both loaded (KEYS1 wording): the page of the one installed later (or later by plugin id) shows `1 conflict`, the problems line `Not active: F8 is used by …`, its Keys row the same sentence, and the list's second line `Key F8 not active: used by …`; the winner's Keys row says `Also bound by … (not active there).` and its page has no problem. Before KEYS1 the list said `Key F8 also used by …`. Rebind the companion's key in its Keys section: the conflict is gone from both pages without a rebuild.
4. Select the Progenitor row in `Required by Progenitor`: `switch.interactive: false`, `switch.mode: refused`, the reason `Progenitor needs this to run, so it cannot be turned off.`
5. Break a hot-loaded test mod's `OnLoad` (throw), redeploy it. Expected: `status: failed`, the problems line with **Retry** and **Show all**, the list's red diamond with `Failed to load`. **Show all** → **Copy report**: the clipboard holds the report with the exception. Fix the mod, redeploy or press **Retry**: the problem clears.
6. Join the isolated test server as it runs without the companion. Expected: no pause of the companion, its page `Server part not on this server · limited`, its **Server echo** row grayed with the fallback. With the companion on the server too: `Server part running`, the row enabled.
7. Join the test server without the Sample (per-server pause takes it), then turn the Sample off and leave. Expected: it stays off (not resumed by the leave), `Disabled`.
8. With an ordinary plugin in its own folder under `BepInEx/plugins`: its switch reads `restart`; turn it off: `Turns off at next start`, and `progenitor-plugin-actions.json` holds a `disable` request. Restart: its files end in `.old`, the log has `Progenitor preloader: … turned off.`, the page `Disabled`. Turn it on and restart: renamed back, loaded. Put two plugins in one folder: `Managed elsewhere: Its folder also holds …`.
9. **Uninstall** that plugin, restart: its folder is under `BepInEx/progenitor-removed/<stamp>/`, it is gone from the list. Change a DLL between the request and the restart on another try: the page says it could not be removed, and nothing moved.

Leave the client at the title screen afterwards and restore the test server's plugin set.

## Stage 3 known limits

- **Nothing live-checked.** Guessed sizes: the list entry 48 px, a group heading 30 px, the summary 26 px and the chip row 34 px, chips at 14 px; the diamond cell 22 px wide with an 11 px square (about 15 px across) and a 2 px outline; hover and selected tints at 6 % and 14 % (20 % hovered) alpha; problem lines at 16 px. All are constants in `ModsListLayout`, `CellLayout` and `ModPagePane`.
- A restart-only Progenitor mod that is off is still loaded as an inert plugin (its DLL is open; nothing of it runs). Its `[BepInDependency]` dependants are skipped by the same rule; a plugin that only soft-depends on it loads normally.
- The live switch works on the whole DLL: a DLL with two plugins turns both off, and the dialog names the other one.
- Ordinary plugins need the preloader installed (one game restart). Only a folder of its own or a qualifying loose single DLL is offered; anything else stays `Managed elsewhere`.
- A command conflict names every owner and the long forms; the registry has no winner, so none is named.
- **Copy report** writes the system clipboard (`GUIUtility.systemCopyBuffer`) and reads it back to confirm; it is not live-checked.
- Status words `Failed to load` (danger) and `Loaded with problems` (warn) use tones the palette marks as emphasis, not reading, colors; the word is always there in text and in the readback.

## Acceptance procedure (stages 1 and 2, not run)

Client only; the isolated test server only for steps 7 and 8. Requires a client running a Core with this change (a Core swap; Bootstrap is unchanged) and the Sample built from the same tree.

1. Open the Mods workspace and select the Progenitor Sample. `mods_ui widget_read pane=content`: expect `page-framework` first with `status: enabled`, `band: true`, `versionText: v…`, `switch.on: true`, `switch.interactive: false`; its `version` cell of kind `text`; `page-header.kind: default`; `page-meta` with an `Example` button cell and no Like; `page-settings-head` with a `reset-all` cell; `page-section` rows `Personal`, `Server`, `Demo`; `page-setting` for `Menu/ShowHints` with `displayName: Show hints`, `control: toggle`; `Menu/HintStyle` `control: radio`; `Menu/HintTopics` `control: multi-select`; `Menu/Notes` `control: textarea`; `Menu/ExampleLimit` `control: readonly`, `scopeDeclared: server` offline.
2. Capture the page at 1600×1024 and 3840×1080 (focus-free capture). Check: the status bar is one band with the switch drawn as a track and knob and the version not letter-spaced; every setting row's control starts at the same x; no help line runs under a control or the reset link at any length; toggles sit at the right of the control column; section headings are small and muted; the settings header has no full-width button.
3. Toggle **Show hints** off with the mouse (foreground test, user-authorized only). Expect the cfg to change, `modified: true`, the help line ending `The default is true.`, the reset link visible in that row's gutter, and the **Hint style** and **Hint topics** rows hidden (`page-settings-reset-all.hidden` lists them) without a page rebuild. Press the reset link: the toggle returns on and both rows show again.
4. Pick a hint style, click two topics (the cfg holds `Combat, Travel`), type two lines into **Notes** and click elsewhere: the cfg holds both lines.
5. Toggle **Experimental layout**: `page-framework.status: pending-restart`, `restart: settings`, the status word `Pending restart · settings changed`; toggle back: `enabled`.
6. Press **Log a line**: the dialog asks `Write a line to the Sample log?`; **No** logs nothing, **Yes** logs `Sample settings: button confirmed and run.` Open the dialog again and select another mod: the dialog closes. Read `page-setting` for `…/info/Local time` twice a few seconds apart: its `value` advances.
7. Join the isolated test server as an administrator: `Menu/ExampleLimit` reads `control: server-request`; apply a value and confirm the server's answer on its help line.
8. With a two-sided mod whose builder marks a row `needsServerPart`, confirm the row is grayed offline with its fallback and enabled once joined to a server that runs the mod.
9. On a mod with more than ten settings, type into the search field: rows filter as you type and `page-settings-search.shown` drops.
10. Press **Reset all**, answer **Yes, reset**, confirm every visible, enabled row reads `modified: false`; with a modified row hidden by show-if or grayed, confirm it keeps its value and is not in the dialog's count.

Leave the client at the title screen afterwards.

## Known limits and risks

- **Live layout is unverified.** MENUKIT2's game-look grid (the pitch, columns, controls, description column and art) is built from the M1 readback but has not been seen live. Sizes chosen without seeing them: the status bar height (48), the status bar switch (44×22), the provenance line (14 px, its height estimated from the one-liner's line count), a list-layout option (30 px per option), a text area (22 px per line), the divider row (14). All are constants in `ModSettingsGrid`, `ModFrameworkLine`, `ModSettingsView`, `ModPagePane` and `UnityUiRuntime`.
- **The expanded editor is unverified live** (TEXTEXPAND1). Unseen: the corner button's look and whether the game font has `⤢`; the editor's 520-unit height; the scroll bar's drag (Unity's `Scrollbar`, followed by the pump while the button is held); whether TMP's own caret scroll leaves a wheel-scrolled view alone (the framework holds it, TMP adjusts only when its caret moves); the 22-unit line pitch the wheel steps by. Constants: `UiTextExpandLayout`, `UiTextScroll`.
- **Fixed in stage 2, from the live stage 1 page (not yet re-checked live):** a long note ran under the `«` reset link ("changed · def« 1") because the note row was its own label and the link attached to it — the link is now in the row's gutter and the help wraps inside the caption column; the status line's version was a mono cell drawn letter-spaced (`· 0 . 1 . 0`) — it is plain text now.
- A grayed row is dimmed with a `CanvasGroup` on the row object (alpha 0.45, selectables off). Whether a cloned vanilla donor's own color transitions fight that is unverified.
- Multi-line text: Enter inserts a line through the framework's typed-key pump (`UiTextEdit.Newline`). The owned caret follows the caret's own line, including after Enter and across word wraps (TEXTAREA1, [caret geometry](ui.md#caret-geometry-textarea1-textarea2)). The caret is placed from TMP's own laid-out lines when they describe the current text (TEXTAREA2), and the text area scrolls to keep its line in view. Arrow keys move along the string, not by line.
- Segmented pickers and list options caption their options on one line with an ellipsis; a long label is cut. A list-layout multi-select grows its row by 30 px per option; a very long list makes a very tall row.
- There is no hover tooltip; the help line carries it.
- Live round 1 (2026-09-29, 3840×1080) fixed in source, not yet re-checked: header buttons sized to their caption and the `»` replaced by **Store page**; the help/row rhythm (2 px under the name, 16 px between settings); toggles drawn as switches, right-aligned; compact values right-aligned; list options as check boxes; the segmented accent; one control height with centered captions (text fields keep TMP's top alignment inside the 34 px box — a guess that the 4 px inset reads as centered); named button rows at the control font size; the read-only reason said once; acronyms in display names; the Sample's stale help texts. Guessed sizes: name band 26, help gap 2, row pad 8, control height 34, switch 44×22, check box 18, button caption slack 1.3.
- Help longer than the row cap allows (roughly 20 lines at the grid's caption width) is cut with an ellipsis on screen; the full text is in the readback.
- show-if / enable-if are re-evaluated on changes on the page, not on a timer. Needs-server-part rows and the status bar's server-part cell also follow the joined server's handshake evidence in place: the request, the accepted result and the end of the join each re-read it on the open page (`ModsMenu.NoteServerPartChanged`, logged `Ui: mod page <id> server part -> …`; COUNTERPART-SAMPLES, game-free tested, not re-checked live).
- The admin (server-request) row draws its **Apply** button as a separate grid row under the field.
- The color swatch repaints on the next page build, not on a typed commit. A key capture starts on a mouse click; since KEYS8 a capture takes a left or right button only after seeing it up, so that click never becomes the key ([modules.md](modules.md#keybinds-fui5)). Not live-checked.
- Stage 3's limits are listed [with it](#stage-3-known-limits).
