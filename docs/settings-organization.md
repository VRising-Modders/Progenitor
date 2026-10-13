# Settings organization: three homes (spec)

**Earlier status (before three homes): stages 2 to 8 and the review fixes of 2026-10-05 built and game-free tested; none of it live-verified beyond the rows the 2026-10-05 live pass names in [Review fixes](#review-fixes-2026-10-05); stage 9 (live acceptance) not run.** Per-stage status is under [Build stages](#build-stages). Decisions of 2026-10-05, written up the same day. A row that says "today" describes the code before this work; everything else is as built by its stage. The inventory was source-checked against `origin/main` at `b19b4f80` before the build. The vanilla server settings rest on read-only research (internal research notes), and that research marks several tag sources as *inferred*.

This page decides **where** each Progenitor setting is drawn. [settings-scopes.md](settings-scopes.md) decides **who** a setting affects and who may edit it. [mod-settings.md](mod-settings.md) decides **how** a row is drawn.

## Three homes (2026-10-06)

**Status: built and game-free tested 2026-10-06; not live-verified (see [Three homes build](#three-homes-build)).** This section supersedes the placement decisions of 2026-10-05: [Placement rules](#placement-rules), [The two pages](#the-two-pages-superseded), [Link rows](#link-rows-superseded) and the subheading names in the [inventory](#inventory). The overlap rule (one stored value), the Server rules, the lock list, the audit and every channel and server-side rule of PR #178 stay as they are.

### Why it changed

The 2026-10-05 organization (PR #178) was reported as confusing:

- "Mods menu, Sources, local server — clicking these do nothing" (the Configuration tab's left list did not scroll).
- "The organization feels a bit halfassed."
- "It still isn't clear to me what I should expect in the Configuration tab vs the Progenitor mod settings."

### The decision (user, 2026-10-06)

The tab bar becomes **Mods · Install Mods · Keybinds · Preferences · Server**. Each setting has exactly one obvious home:

| Home | Question it answers | What is there |
|---|---|---|
| **Preferences** (renamed from Configuration) | How does this Mods window behave on this PC (and Progenitor's filter in the game's Join Game browser)? Nothing here changes a server. | Mods tab; Install Mods tab; Sources and downloads; Join Game (the Progenitor servers filter of the game's Join Game browser, [BROWSERFILTER1](server-list-description.md#progenitor-servers-in-join-game)); Local server ([trim of 2026-10-06](#trim-2026-10-06): Look and navigation became Mods tab). |
| **Mods** → a mod's page | What does this mod do? | That mod's own settings. Progenitor's page: **Your settings** (Keys, Photo mode, Build menu, Equipment, Chat, Private games with Uninstall, and Get help with Collect logs since 2026-10-10; the UI group went in the [trim](#trim-2026-10-06)) and **On this server** (only Progenitor's own two server toggles, **Allow photo mode on this server** and **Ally-safe hits**), plus the link `This server's setup is in the Server tab.` **Server »**. Mod authors' pages are unchanged (their Your / Server scopes stay). |
| **Server** (new, the last tab: "server should be the last tab since it is generally only applicable to those who do server stuff", requested 2026-10-06) | How is the server I'm on set up? (admin tools) | When players join; Mods players need; Server list; Server rules · applied at the next restart; Chat on this server (listed as Chat; [CHAT4, 2026-10-07](#chat-on-this-server-2026-10-07)); Players without Progenitor; Operator. |

Preferences opens with one purpose line: **How this Mods window works on this PC. What a mod does is on its page in Mods; how the server you're on is set up is in Server.**, with links to both. The Install tab's **Server settings »** link opens the Server tab. A Progenitor-installed mod's page has a **Preferences » Updates** text link under its **Updates** row (key `link-preferences-updates`), which opens Preferences at its Updates group in one step (back returns to the mod page; live 2026-10-09).

**Layout rules (user):**

- **Drawers.** Only a group with **more than four** settings is a collapsible drawer. A drawer's header shows the group name and its row count. Its open or closed state is remembered per drawer in `[Ui] ClosedDrawers` of Core's `progenitor.ui.cfg` (plain text, cfg-only, survives a restart and a Core reload). First-time default: open. A group of four or fewer is a plain gold small-caps subheading. Mod authors' pages draw no drawers.
- **Left list.** Every tab with groups has a left list of them: Preferences, Server, and Progenitor's page (its groups are listed under Progenitor in the Mods list). Clicking an item scrolls the content to that group, opens it when it is a closed drawer, and highlights the item. The highlight also follows the scroll: the group at the top of the view.
- **Navigation.** Back and forward include the Server tab and the section jumps, as the history does for other places.
- **Look.** The game's row kit; each tab's title as a section header with the red rule; gold small-caps subheadings.
- **Rights.** Every admin may edit everything; RBAC is future work. The `[ServerSettings] LockedInGame` lock list applies to every server row wherever it is drawn.

### Mockups

`▾` stands for the drawer marker left of the title (the game's dropdown arrow in gold, pointing down open and right closed; never a glyph the game font lacks); the count is muted in parentheses. `»` is the Latin-1 link glyph the kit already draws (the game font is not known to have `›`).

**Preferences** (any player, connected or not; as trimmed on 2026-10-06, so no group is a drawer):

```
 Mods · Install Mods · Keybinds · [Preferences] · Server
┌──────────────────────┬──────────────────────────────────────────────────────────────┐
│ Mods tab             │ Preferences                                                  │
│ Install Mods tab     │ ════════════════ (red rule)                                  │
│ Sources and downloads│ How this Mods window works on this PC. What a mod does is on │
│ Local server         │ its page in Mods; how the server you're on is set up is in   │
│                      │ Server.                         [ Mods » ]  [ Server » ]     │
│                      │ MODS TAB                                                     │
│                      │   Edit other mods' settings                      [ off ]   « │
│                      │ INSTALL MODS TAB                                             │
│                      │   Install view                        [ Simple ▾ ]         « │
│                      │   Remember Install filters and sort              [ off ]   « │
│                      │ SOURCES AND DOWNLOADS                                        │
│                      │   Use Thunderstore                                [ on ]   « │
│                      │   Show mods with NSFW content                    [ off ]   « │
│                      │   Download limit                                 [ 128 ] MB  │
│                      │   Advanced sources                            [ Expand ]     │
│                      │ LOCAL SERVER                                                 │
│                      │   (folder + Apply / Clear, found folders, Install Progenitor │
│                      │    on this server, Stop / Start server, Admin list: Add me)  │
│ [ Close Mods ]       │                                                              │
└──────────────────────┴──────────────────────────────────────────────────────────────┘
```

**Server, as an admin of the joined server:**

```
 Mods · Install Mods · Keybinds · Preferences · [Server]
┌───────────────────────────┬──────────────────────────────────────────────────────────┐
│ When players join         │ Night Garden                                             │
│ Mods players need         │ ════════════════ (red rule)                              │
│ Server list               │ How this server is set up · admins edit, others read only│
│                           │ Progenitor's own server settings »  (link)               │
│ Server rules              │ ▾ WHEN PLAYERS JOIN (8)                                  │
│ Players without Progenitor│   Server message [Show] · This server's mods [View]      │
│ Operator                  │   Message [Edit] [Preview] · Countdown now · Next reset  │
│                           │   (or the next wipe, read-only) · Show the message ·     │
│                           │   Show the mods page                                     │
│                           │ MODS PLAYERS NEED                                        │
│                           │   Players joining this server                  [ Open ]  │
│                           │   Mods break rules while playing    ( Warn and pause ) ( Disc. ) │
│                           │   If mods can't be checked  ( Let them join ) ( Refuse ) │
│                           │ ▾ SERVER LIST (6)                                        │
│                           │   Server name · Server list description · Room left ·    │
│                           │   Progenitor signature · Listed as · Status              │
│                           │ ▾ SERVER RULES · APPLIED AT THE NEXT RESTART (15)        │
│                           │   Pending changes  2 changes apply at the next restart:  │
│                           │     Clan size 4 → 3, Game mode PvP → PvE   (gold status) │
│                           │   Game mode [ PvP ▾ ] (Restart) · Clan size · Difficulty │
│                           │   Password  Set [ ****** ] [Apply] · Max players · …     │
│                           │   Listed as Merciless · Territory · Days running · …     │
│                           │ PLAYERS WITHOUT PROGENITOR                               │
│                           │   Message without Progenitor ┌──────────────────────┐    │
│                           │     (muted: the default text) │ This server uses …   │    │
│                           │                               └──────────────────────┘    │
│                           │                                              [ Apply ]   │
│                           │   Placeholders  {mods} — the mods they're missing ·      │
│                           │                 {seconds} — seconds until disconnected   │
│                           │   Players see   This server uses mods. … in 20 seconds.  │
│                           │   Length        Using the default message.   (muted)     │
│                           │   Seconds before disconnect                  [ 20 ]      │
│                           │ ▾ OPERATOR (5)                                           │
│                           │   Remote mod installs · Admins who may install mods ·    │
│                           │   First notice after · Repeat notice every ·             │
│                           │   Creation time limit                                    │
│ [ Close Mods ]            │                                                          │
└───────────────────────────┴──────────────────────────────────────────────────────────┘
```

**Server, as a player who is not an admin there:** the same header and groups. Server-wide rows read `Admin only · <value>` with no field or **Apply**; the password reads only `Set` or `Not set`. Rows on the admin channel (Players joining, the mid-session choice, if mods can't be checked, the seconds, every Operator row) are never sent to a non-admin (decision 5 stands), so a group made only of them shows one line instead: `Only an admin of this server sees these settings.`

```
│ When players join         │ Night Garden                                             │
│ Mods players need         │ How this server is set up · admins edit, others read only│
│ Server list               │ ▾ WHEN PLAYERS JOIN (10)                                 │
│ …                         │   Message                     Admin only · Welcome! …    │
│                           │ MODS PLAYERS NEED                                        │
│                           │   Only an admin of this server sees these settings.      │
│                           │ ▾ SERVER RULES · APPLIED AT THE NEXT RESTART (14)        │
│                           │   Clan size                          Admin only · 4      │
│                           │   Password                           Admin only · Set    │
```

**Server, not connected** (SERVEREMPTY1, user design of 2026-10-06, "feels like it is half deployed" before it; source-checked, built and game-free tested, not live-verified): the groups dimmed on the left, an explanation, one muted line per group, and the last server joined.

```
│ When players join   (dim) │ Server                                                   │
│ Mods players need   (dim) │ ════════════════ (red rule)                              │
│ Server list         (dim) │ You're not on a server. When you join one that runs      │
│ Server rules        (dim) │ Progenitor, its setup shows here: admins edit it,        │
│ Players without Progenitor│ everyone else can read it.                               │
│ Operator            (dim) │ When players join  · message, resets, join screens       │
│                           │ Mods players need  · required and approved mods, …       │
│                           │ Server list        · name, description, listing          │
│                           │ Server rules       · mode, clan size, password           │
│                           │ Players without Progenitor · message, seconds before …   │
│                           │ Operator           · remote installs, who may install    │
│                           │ Last server: Progenitor Forms Test · 127.0.0.1:9976 [Join]│
```

- The dimmed left-list items take no click and are never highlighted.
- **Last server** is the last server this client joined ([join-flow.md](join-flow.md#the-last-server-serverempty1)); it is hidden without one or while this client hosts a private game, and **Join** is grayed while a join is in progress. **Join** connects as Join now does; a server joined with a password opens the game's own password prompt. Readback and bridge: [mods-menu.md](mods-menu.md#the-server-tab).

**Server, joined a server without Progenitor:** the same layout without the Last server row and with `This server doesn't run Progenitor, so its setup can't be shown or changed here. On a server that runs it, admins edit it and everyone else can read it.` While the handshake has not answered yet: `Waiting for the server to answer… Its setup shows here once it does.`

**Mods → Progenitor:**

```
│ Progenitor          │ Progenitor 2026.10.x                       Installed · Both  │
│     Keys            │ Your settings                                                │
│     Photo mode      │ ▾ KEYS (19)                           [ Open in Keybinds ]   │
│     Build menu      │   Open or close the Mods menu · Navigate back / forward · …  │
│     Chat            │ PHOTO MODE                                                   │
│     Private games   │   Photo mode · hide the HUD · hide nameplates · look speed   │
│     On this server  │ BUILD MENU                                                   │
│                     │   Build menu search · clear on close · breadcrumbs           │
│                     │ CHAT                                                         │
│                     │   Seconds on screen · keep on screen · hover · click-safe ·  │
│                     │   /r reply · Tab name completion                            │
│                     │ PRIVATE GAMES                                                │
│                     │   Load Progenitor in the private games I host · Uninstall    │
│                     │ On this server                                               │
│                     │ Applies to everyone on Night Garden · admins edit, others…   │
│                     │   Allow photo mode on this server                 [ on ]     │
│                     │   Ally-safe hits                                  [ on ]     │
│                     │   This server's setup is in the Server tab.   [ Server » ]   │
```

A non-admin, or a player who is not connected, sees **On this server** with only the link row: both toggles are admin-channel rows.

### Updates (2026-10-09)

**Status: built and game-free tested.** AUTOUPDATE1 adds a Preferences group **Updates** between Sources and downloads and Join Game, with two rows: **Update mods** (shared `[AutoUpdate] Mods`, Automatic / Notify / Off) and **Offer updates before joining a server** (shared `[AutoUpdate] PromptBeforeJoin`). Both change what the Mods window and the join do on this PC, so placement rule 2 puts them on Preferences. The dev Thunderstore keys (`[Install] DevThunderstoreUrl`, `AllowDevThunderstore`) are cfg only (rule 1: a test hook). Behavior: [auto-update.md](auto-update.md#client-settings-preferences). AUTOUPDATE2 (2026-10-09, built and game-free tested; verified live in part, [auto-update.md](auto-update.md#phase-2-as-built)) adds a third row, **Major versions** (shared `[AutoUpdate] MajorVersions`, Automatic / Notify, default Notify), on the same rule. The per-mod choice (**Updates**: Global / Automatic / Notify / Off / Keep) is not a Progenitor setting row: it is drawn on each Progenitor-installed mod's own page, under its kept versions, with **Roll back** and the failed versions, because it is about that mod (the Mods home), and it is stored in `BepInEx/config/progenitor-auto-update.json`, not in a cfg.

AUTOUPDATE3 (2026-10-09, built and game-free tested; verified live in part, [auto-update.md](auto-update.md#phase-3-as-built)) adds a Server tab group **Updates** before Operator (a drawer; lock word `updates`): **Update server mods**, **Major versions**, **Restart when empty**, **Empty for**, **Back up the world before updating**, **Keep backups made before an update**, **Skip when a backup is newer than**, the **Status** / **Waiting** / **Recent** lines and **Check now**, **Restart now**, **Cancel restart**. They change what the server does for everyone on it, so rule 3 puts them under Server settings (the phase 3 brief named the group), edited by admins over the admin channel and audited in `progenitor-settings.log`; the server cfg section is `[AutoUpdate]`. `CheckHours`, `WarnMinutes`, `Exclude`, `Pins` and `HostRestarts` are cfg only (operator configuration; `HostRestarts` names what starts the server again and gates **Restart when empty**). A joined server whose Progenitor predates this group shows one note row instead. The lock word covers the three actions too (**Check now**, **Restart now**, **Cancel restart**). **Update server mods** = Automatic acts as Notify while `[RemoteInstall] Enabled` is false; the row's help and the status line say so.

### Homes (`SettingPlaces`)

Place words: `preferences`; `server-tab/<group>` (`when-players-join`, `mods-players-need`, `server-list`, `server-rules`, `chat`, `players-without-progenitor`, `operator`); `progenitor-page/<group>` (`keys`, `photo-mode`, `build-menu`, `equipment`, `chat`, `private-games`, `on-this-server`; `ui` went in the [trim](#trim-2026-10-06)); the mirrors `install-toolbar`, `keybinds-tab` and `players-joining-panel`; and `cfg`. The lock list's group words are unchanged (`join`, `mods-players-need`, `server-list`, `no-progenitor`, `gameplay`, `operator`, `server-rules`), so an operator's existing list keeps working; `gameplay` names the two **On this server** toggles. CHAT4 (2026-10-07) adds `chat` for the Server tab's Chat group.

### Chat on this server (2026-10-07)

**Status: built and game-free tested; not live-verified.** Decided 2026-10-07: every chat feature's server settings sit in a **Chat** group of the Server tab, server-wide and admin-edited; the player's own chat settings stay under **Your settings → Chat**. The group follows **Server rules** (both are what the server decides for every player) and comes before the two admin-only groups. Its heading is **Chat on this server** and the left list names it **Chat**, because the two homes of Progenitor's registration are split by group title (`ProgenitorPage.SectionsFor`) and **Chat** is Your settings' group. Rows, defaults and behavior: [chat.md](chat.md#server-settings-as-built).

| Setting | Store · key | Home | Who sees it | Applies |
|---|---|---|---|---|
| Allow clickable links, Allow mod links, Allow @all from admins, Allow long messages, Longest long message | serverchat `[ServerChat] AllowLinks`, `AllowModLinks`, `AllowAtAll`, `AllowLongMessages`, `MaxLongMessageChars` (5; ConfigSync rows `progenitor/ServerChat/<key>`) | Server · Chat | every player (`Admin only · <value>` for a non-admin) | live (ConfigSync; clients fill `ChatPolicy`) |
| Word filter, Filtered words, When a word matches | shared `[ChatFilter] Enabled`, `Words`, `Action` (admin channel `chat-filter`, `chat-filter-words`, `chat-filter-action`) | Server · Chat | admins only | live (each chat line) |
| Discord relay, Discord webhook address, Relay Global chat / Local chat / clan chat / whispers / System lines | shared `[ChatRelay] Enabled`, `WebhookUrl`, `Global`, `Local`, `Clan`, `Whispers`, `System` (admin channel `chat-relay`, `chat-relay-url`, `chat-relay-global`, `-local`, `-clan`, `-whispers`, `-system`) | Server · Chat | admins only; the address is write-only and reads `Set` / `Not set` | live (each chat line) |

`serverchat` is `progenitor.serverchat.cfg` (its own file, like the server message, because ConfigSync holds a file's saving off while it carries server values). The admin channel gained three option kinds for this group: text (the word list, checked and normalized by the server), a choice (the action) and a secret (the address: the state, the audit and every log carry only `set` or empty, and every accepted write is stored). The shared cfg entries are cfg-only rows in `SettingPlaces`, so `settings_places` never lists the address.

### Trim (2026-10-06)

**Status: built and game-free tested 2026-10-06; not live-verified.** Decided: hide niche settings from the menus ("No user will care to change those"). Each one stays in its cfg file with the same key, default and behavior; only the menus stop drawing it.

| Was drawn at | Setting | File · key |
|---|---|---|
| Preferences · Look and navigation | Background intensity | shared `[Workspace] BackgroundIntensity` |
| Preferences · Look and navigation | Background contrast | shared `[Workspace] BackgroundContrast` |
| Preferences · Look and navigation | Game menu tabs in back/forward | ui `[Ui] MenuHistoryInnerTabs` |
| Preferences · Look and navigation | Authenticate-as-admin button | shared `[Ui] AdminAuthButton` |
| Preferences · Sources and downloads | Current game update tag | shared `[Install] CurrentUpdateTag` |
| Progenitor's page · UI | Mouse capture for Progenitor panels | ui `[Ui] CaptureMode` |
| Progenitor's page · Keys | The game's keys win clashes | ui `[Keybinds] EnforceGameWins` |

The read-only **Nexus Mods** `Not supported yet` line under Sources and downloads is gone too (it was a note, not a setting; `[Mods] UseNexus` and `NexusApiKey` were already cfg-only). The **Operator** notice timings stay on the Server tab (kept by decision).

How it is built, and the choices the decision left open:

- **Mechanism.** `SettingPlaces` homes each of the seven `cfg` (cfg-only) with a `Hidden` mark, the date and the reason. The Preferences rows and the two `SettingRef` rows are no longer added, and a cfg-only home keeps a row off Progenitor's page's sweep of its own files (`DrawnOnProgenitorPage`), so no row of them is drawn anywhere.
- **`settings_places`.** Cfg-only settings are still left out (secrets among them), except these seven: each is listed with `home cfg`, `places []`, `cfgOnly true` and `hidden` (the date and the reason), with its value read from the file as before.
- **Search.** A search on a page or tab filters the rows it draws, so none of the seven is found.
- **Reset all leaves hidden values alone.** It acts on the rows a page draws. A hidden value is the file's: an operator who set it there keeps it, and nobody can see a reset happen to a row that is not drawn.
- **Look and navigation becomes Mods tab.** Its one remaining row, **Edit other mods' settings**, is about the mod pages of the Mods tab, as the next group's rows are about the Install Mods tab, so the group is renamed rather than merged. The left list reads Mods tab, Install Mods tab, Sources and downloads, Local server. A stored `preferences/look-and-navigation` in `[Ui] ClosedDrawers` is ignored.
- **Drawer threshold, recomputed.** Mods tab has one setting, Install Mods tab two and Sources and downloads three, so Preferences has no drawers now. The **Advanced sources** fold's two rows are not counted toward Sources, so opening the fold does not turn the group into a drawer under the pointer. On Progenitor's page the UI group and its left-list item are gone; Keys keeps its drawer (19 keybinds). The Server tab's drawers are unchanged.

**Drawer header fix (same day).** Live 2026-10-06, after Background intensity was changed, the drawer headers drew `ION (5)` and `NLOADS (5)`: no fold marker, and the gold title cut from the left. A value change re-evaluates the page in place, which rewrites each drawer's count cell. The runtime re-placed that row's cells at the panel's content row width, which outside a layout pass is the full content width, not the settings grid's (the width the layout pass gives a grid row). The cells were packed from a left edge outside the grid row, so the marker and the start of the title fell outside it. An in-place re-placement now reuses the width the last layout pass placed the row at (`MenuPanelLayout.InPlaceCellRowWidth`); every layout pass (a rebuild, the scroll restore, a resolution change, an admin or connection change) places it again at its own width. A drawer header's `page-section` readback adds `titleShown` and `chevron.shown`, measured from the drawn rects, and `heading {chevronShown, titleShown, chevronLeft, titleLeft, titleWidth, titleNeeds}`. Built and game-free tested (`ThreeHomesMenuTests`: the column model at grid widths 640, 820 and 1100, and the readback after a value change); not live-verified.

### The tab strip's ornament

Requested 2026-10-06: "If you are adding a new tab, make sure to center the flair on the silver line." A first build read that as the line's own center (one whole copy of the `Divider_Big_04` trim centered on the window); seen live, the request was clarified: "silver flair should be centered in the dead space to the right." So the ornament is centered in the dead space of the silver line, from the last tab's right edge to the line's right end (round 13's split layout, `WorkspaceHostLayout.OrnamentCentered = false`), at every size. `mods_ui tabs` reports `trimNotch {flairCenterX, lineCenterX, lineLeft, lineRight, centered, source}` in screen pixels read from the drawn trim objects (live 2026-10-06 the first version reported the layout's frame-center units, 0 and 0; those are now `layoutFlairX` / `layoutLineCenterX`), and `mods_ui status` the screen-pixel `hosts.windowRects.notchRect.centerX` and `hosts.windowRects.lineRect.centerX`. Built and game-free tested (`WorkspaceHostsParityTests`, at several window sizes); not live-verified.

## Why (2026-10-05)

Today Progenitor's settings live in three places, and the split follows code history rather than the player's question:

- The **Configuration tab** mixes mod-manager preferences (sources, the local server folder) with a **Progenitor settings** button. That button opens a raw "Settings and keybinds" list for `progenitor` (`ConfigurationPane.OpenFrameworkSettings` → `SettingsPanel.OpenForModInMenu`). The list holds every catalog row of every Progenitor cfg file, but none of the page's buttons.
- **Progenitor's mod page** (Mods → Required → Progenitor) holds the page-builder rows: **Your settings**, and the **Server settings** that the join flow and SERVERDESC1 added (`JoinServerSettings.BuildPage`).
- **Many settings are cfg-only.** The download limit, the current update tag, the Mods-menu background and the seconds before a player without Progenitor is disconnected are examples.

Some docs therefore say the Players joining row is "on Progenitor's page in the Configuration tab" ([join-flow.md](join-flow.md#players-joining-admins-stage-3), [mods-menu.md](mods-menu.md#players-joining-joinadmin1)), while the code puts it on the mod page. The vanilla settings behind the **Join Game** browser filters should also be editable without a wipe. Those settings need a home.

## Decisions (2026-10-05)

1. **Where the line is.**
   - The **Configuration tab** holds only **mod-manager preferences for this PC**: how the Mods menu itself behaves. That means the install view, sources and the download limit, listing filters, editing other mods' settings, the menu's look and the local server folder. It never holds server settings.
   - **Mods → Progenitor** (Progenitor's own mod page) holds everything about Progenitor itself, in the two existing scope sections:
     - **Your settings** (`This PC only`), with subheadings such as Keys, Gameplay and UI.
     - **Server settings** (`Applies to everyone on <server> · admins edit, others read only`), with subheadings.
2. **Server settings subheadings** (from the approved mockup): **When players join**, **Mods players need**, **Server list**, **Players without Progenitor**, plus a new subheading for the vanilla server settings (names proposed [below](#vanilla-server-settings-server-rules)).
3. **Links both ways** between the two places, and the existing back/forward navigation returns from either jump.
4. **Overlap is allowed, with one source of truth.** A setting may appear in more than one place, but it is always the same stored value. Editing it in either place updates both, and the readbacks agree.

## Placement rules

*Read with [Three homes](#three-homes-2026-10-06): "Configuration tab" below is now **Preferences**, and "Progenitor's page, Server settings" is now the **Server** tab, except Progenitor's own two server toggles (**On this server** on its page).*

### For mod authors

Unchanged. A mod's settings go on **its own page**, in **Your settings** or **Server settings** by the scope it declares ([settings-scopes.md](settings-scopes.md#mapping-existing-mods)). A mod never adds rows to the Configuration tab or to Progenitor's page. Author sections become subheadings inside the scope section, in declared order.

### For Progenitor itself: where does a new setting go?

Ask the questions in order. The first "yes" decides.

1. **Is it a secret, a path to a server-side file, a diagnostic switch or a test hook?** (A token, a URL override, a timeout for Thunderstore fetches on the server, the Bootstrap `[Diagnostics]` flags.) → **cfg only.** It is not drawn. It is documented where its feature is documented. A server cfg value that is not published is never sent to clients ([settings-scopes decision 3](settings-scopes.md#design-decisions-2026-10-03)).
2. **Does it change how the Mods menu works on this PC?** That covers what the Install tab lists or downloads, where installs go, how the menu looks or navigates, and whether other mods' settings are editable. → **Configuration tab.**
3. **Does it affect everyone on a server?** → **Progenitor's page, Server settings**, under the subheading for its topic. When no subheading fits, ask the maintainers before adding one.
4. **Otherwise** (a Progenitor feature on this PC: keys, photo mode, build search, dye all) → **Progenitor's page, Your settings**, under its topic's group (Keys, Photo mode, Build menu, Equipment, Chat, Private games since three homes; Equipment since 2026-10-08).

Then decide whether it also appears somewhere else ([overlap](#overlap-one-stored-value-many-places)). A setting has exactly one **home** (where the inventory lists it). Other places are **mirrors**.

## The two pages (superseded)

*Superseded on 2026-10-06 by [Three homes](#three-homes-2026-10-06); kept as the record of the 2026-10-05 design.*

### Configuration tab (target)

```
Configuration
┌─────────────────────────────────────────────────────────────────────────┐
│ Progenitor's own settings and this server's settings are on            │
│ Progenitor's page.                                            [ Open ] │
├─ Mods menu ─────────────────────────────────────────────────────────────┤
│ Install view                                  [ Simple | Advanced ]    │
│ Edit other mods' settings                                    [ off ]   │
│ Remember Install filters and sort                            [ off ]   │  (open question 3)
│ Background intensity                              ──●──────  0.90      │
│ Background contrast                               ─●───────  0.18      │
│ Game menu tabs in back/forward                                [ on ]   │
│ Authenticate-as-admin button                                  [ on ]   │
├─ Sources ───────────────────────────────────────────────────────────────┤
│ Use Thunderstore                                              [ on ]   │
│ Show mods with NSFW content                                  [ off ]   │
│ Download limit (MB)                                          [ 128 ]   │
│ Current game update tag                                [ automatic ]   │
│ Nexus Mods: not supported yet                               (dimmed)   │
│ ▸ Advanced: package index path, keep the in-game frame                 │
├─ Local dedicated server ────────────────────────────────────────────────┤
│ (unchanged: note, folder + Apply/Clear, found folders, Install         │
│  Progenitor on this server, Stop / Start server, Admin list: Add me)   │
└─────────────────────────────────────────────────────────────────────────┘
```

The **Progenitor** group (the **Progenitor settings** button and **Uninstall Progenitor from this client**) leaves this tab. The button becomes the link row at the top. Uninstall moves to Progenitor's page ([open question 1](#open-questions)).

### Progenitor's page (target), as an admin of the joined server

```
Progenitor 2026.10.x                                   Installed · Both
───────────────────────────────────────────────────────────────────────────
YOUR SETTINGS · This PC only
│ Mod manager settings (install view, sources, filters)        [ Open ] │
├ Keys ───────────────────────────────────────────── [Open in Keybinds] ┤
│ Open or close the Mods menu                                    [ F10 ] │
│ Navigate back / forward                         [Mouse Back] [Mouse Fwd]│
│ Release input · Photo mode · flight keys …                             │
│ The game's keys win clashes                                    [ on ]  │
├ Gameplay ──────────────────────────────────────────────────────────────┤
│ Photo mode: enabled · hide HUD · hide nameplates · look speed          │
│ Build menu search: enabled · clear on close · breadcrumbs              │
├ UI ────────────────────────────────────────────────────────────────────┤
│ Mouse capture for Progenitor panels                    [ When hovered ]│
├ Private games ─────────────────────────────────────────────────────────┤
│ Load Progenitor in the private games I host                    [ on ]  │
│ Remove Progenitor from this PC…                          [ Uninstall ] │  (open question 1)
═══════════════════════════════════════════════════════════════════════════
SERVER SETTINGS · Applies to everyone on Night Garden · admins edit, others read only
├ When players join ─────────────────────────────────────────────────────┤
│ Server message                                         [ Show ]        │
│ This server's mods                                     [ View ]        │
│ Message                                     [ Edit ]  [ Preview ]      │
│ Countdown now                               3d 4h 17m · in 3 days      │
│ Next reset (or the next wipe, read-only)                               │
│ Show the message                       [ When it changes or after … ]  │
│ Show the mods page                                    [ When needed ]  │
├ Mods players need ─────────────────────────────────────────────────────┤
│ Players joining this server                                 [ Open ]   │
│   Recommended, required and approved client mods · install window ·    │
│   refused joins                                                        │
│ Mods break rules while playing                                                 │
│                        ( Pause the mod and warn them ) ( Disconnect them ) │
├ Server list ───────────────────────────────────────────────────────────┤
│ Server name                               [ Night Garden ] (restart)   │
│ Server list description              [ Edit ]   72 of 82 characters left│
│ Progenitor signature                                           [ on ]  │
│ Listed as / Status                                                     │
├ Players without Progenitor ────────────────────────────────────────────┤
│ Message without Progenitor   [ text area; empty: the default, muted ] │
│                                                            [ Apply ]   │
│ Placeholders   {mods} — the mods they're missing · {seconds} — …       │
│ Players see    This server uses mods. … disconnected in 20 seconds.    │
│ Length         212 / 300  (or: Using the default message.)             │
│ Default message                                  [ Use the default ]   │
│ Seconds before disconnect                                        [ 20 ]│
│ If mods can't be checked                ( Let them join ) ( Refuse )   │
├ Server rules · applied at the next restart ────────────────────────────┤
│ Pending changes   2 changes apply at the next restart: Clan size 4 → 3,│
│                   Game mode PvP → PvE.            (gold; muted: None.) │
│ Game mode                                      [ PvP ▾ ]               │
│ Clan size                                      [ 4 ]                   │
│ Difficulty                                     [ Normal ▾ ]            │
│ Blood-bound equipment                          [ on ]                  │
│ Castle heart damage                [ Only when decaying ▾ ] (Restart)  │
│   Listed as Merciless: No → Yes after the restart                      │
│ Password                                       Set · [ Change ] [Clear]│
│ Max players                                    [ 40 ]                  │
│ Scheduled wipes                 [ Off ▾ ]  ⚠ can wipe this world       │
│ Territory (read only)                          Abundant                │
│ Days running (read only)                       33                      │
│ Standard settings (read only)                  No                      │
├ Gameplay ──────────────────────────────────────────────────────────────┤
│ Allow photo mode                                               [ on ]  │
│ Ally-safe hits                                                 [ on ]  │
├ Operator ──────────────────────────────────────────────────────────────┤
│ Remote mod installs                                            [ on ]  │
│ Admins who may install mods        [ 7656119…, 7656119… ]   [ Apply ]  │
│ First notice after · Repeat notice every · Creation time limit         │
│ (seconds, each a field with Apply at its right)                        │
└────────────────────────────────────────────────────────────────────────┘
```

**A non-admin** sees the same sections. Server-wide ConfigSync rows read `Admin only · <value>` ([settings-scopes](settings-scopes.md#what-the-mods-page-shows)). Rows backed by the admin channel (Players joining, the mid-session choice, the seconds, the can't-be-checked choice, and every Gameplay and Operator row) are hidden from non-admins, as the Players joining row is today ([open question 5](#open-questions)). The Server rules show their values read-only, and the password shows only `Set` or `Not set`. **Not connected:** the Server settings section shows Progenitor's own rows grayed with `Not connected to a server.`, and the Server rules subheading is omitted.

## Link rows (superseded)

*Superseded on 2026-10-06: the Preferences tab's purpose line links to Mods and Server, and Progenitor's page links to the Server tab ([Three homes](#three-homes-2026-10-06)).*

| Where | Row | Action |
|---|---|---|
| Progenitor's page, first row of **Your settings** | `Mod manager settings (install view, sources, filters)` · **Open** | Jumps to the Configuration tab, top. |
| Configuration tab, first row | `Progenitor's own settings and this server's settings are on Progenitor's page.` · **Open** | Selects Mods → Progenitor. As an admin of the joined server (the scope section's `canEdit`, the same fact the Server settings rows use), it lands on the **Server settings** heading. Otherwise it lands on the page's top. |

- **Mechanism:** both rows use `ModsMenu.JumpWorkspace`, the path the Keybinds links use ([mods-menu.md](mods-menu.md#links-with-the-mods-tab)). It selects the tab through `SelectWorkspaceTab` (tab memory kept), clears a search or filter that would hide the target, and scrolls with `UiReveal` on the next frame.
- **History:** each jump is one place in the back/forward history, because the one observer records the settled view ([Back and forward](mods-menu.md#back-and-forward)). **Navigate back** returns to the row that was clicked.
- The history gains one place kind, **Progenitor's page at the Server settings heading**. The Configuration tab's "settings page opened inside it" place goes away, together with the raw **Progenitor settings** pane.
- The jump never opens the raw `page=settings` pane. That pane stays reachable through `mods_ui select page=settings` for debugging.

## Overlap: one stored value, many places

**Rule.** Every drawn setting has one **setting id** and one home. A mirror is another row bound to the **same** store object:

- the same `ConfigEntry` for a cfg row;
- the same server state for a join-admin row;
- the same server answer for a server rule.

A mirror never holds a copy. Both rows read the store when they are drawn and when the store says it changed, and both write through the store's one write path.

| Store | Setting id | Write path (shared) | Change signal (shared) |
|---|---|---|---|
| Local cfg (`ConfigEntry`) | `<pluginId>/<Section>/<Key>`, as `SettingsRow` uses today | `SettingsCatalog.Submit`: parse, bounds, write `ConfigEntry` | the entry's `SettingChanged`, through `ConfigHelper.AddSettingChanged` on files Core does not own ([pin rule](settings-scopes.md#hot-reload-pin-rules-that-apply)) |
| Server-wide ConfigSync row | the same id | `ServerSettings.Request` (`'PrSe'`; later `'PrSt'`) | the ConfigSync delta |
| Join-admin state | `progenitor/JoinAdmin/<op>` (the audit ids) | the existing `'PrJa'` operation | the answer that carries the whole state, then `JoinAdminClient.Changed` |
| Server rules (new) | `progenitor/ServerRules/<Key>` | `'PrSv'` `write` ([below](#wire-and-authorization)) | the `'PrSv'` answer and its `changed` push |

**Implementation.**

1. **`SettingPlaces`** (Core, game-free): a table of setting id → home and mirrors (`configuration`, `progenitor-page/<subheading>`, `install-toolbar`, `keybinds-tab`, `players-joining-panel`). Progenitor's page builder and the Configuration tab both ask it which rows to draw. A row whose home is the Configuration tab is no longer drawn among Progenitor's raw rows.
2. **The Configuration tab draws its settings with the mod page's row kit**: `ModSettingsLayout` rows built from `SettingsRow`, the same controls, reset arrow and description column. Today `ConfigurationPane` builds its own toggles. The local-server section's custom rows (path field, Install Progenitor on this server, stop and start) stay hand-built: they are actions, not settings.
3. **Today's overlaps already follow the rule.** Core's keybinds are rows on Progenitor's page and in the Keybinds tab over one `ConfigEntry`. `[Mods] InstallView` is read by the Install toolbar switch and bound once in `ModsConfigBindings`. Those stay as they are.
4. **`EditOtherModsSettings` becomes live.** Today it is read once at Core install (`UiKeybindHost.BindForeignEditPolicy`), so a change takes a Core reload. It moves to a `SettingChanged` read, as `CaptureMode` already has.

**Readback.** Every settings row in `mods_ui widget_read`, on any tab, carries `settingId`, `home` and `alsoAt: [place…]`. Its `value` is read from the store at readback time, not from the drawn text. A new `mods_ui settings_places` op returns `{settingId: {home, places[], value, display}}` for every placed setting from one store read. An acceptance check edits in one place and compares the two places' readbacks against it.

## Inventory

Legend:

- **Today:**
  - `Config` = the Configuration tab;
  - `Page·You` / `Page·Server` = Progenitor's mod page sections;
  - `raw` = only in the raw **Progenitor settings** list (the chainloader sweep registers Bootstrap's `progenitor.cfg` as a foreign, read-only source: `UiKeybindHost.Sweep`; source-checked, not live-checked);
  - `cfg` = file only.
- **Change:** `stay`, `move`, `overlap` (one home plus mirrors) or `cfg-only` (stays undrawn).
- **Applies:**
  - `live` = watched, or read each time it is used;
  - `reload` = read at Core install, so a Core reload or restart;
  - `restart` = read at process start (Bootstrap);
  - `join` = the next join or ask.

Files:

- `shared` = `BepInEx/config/progenitor.cfg` (Bootstrap's);
- `ui` = `progenitor.ui.cfg`;
- `msg` = `progenitor.servermessage.cfg`;
- `list` = `progenitor.serverlist.cfg`;
- `hosted` = `progenitor.hosted.cfg`;
- `joinadmin` = `progenitor-join-admin.json`.

### Mod-manager preferences (home: Configuration tab)

| Setting | File · key | Scope | Today | Target | Change | Applies |
|---|---|---|---|---|---|---|
| Use Thunderstore | shared `[Mods] UseThunderstore` | this PC | Config · Sources | Config · Sources | stay | live |
| Nexus Mods (dimmed note) | shared `[Mods] UseNexus` | this PC | Config (note) | none | **note removed 2026-10-06** ([trim](#trim-2026-10-06)); the key stays cfg-only | — |
| Show mods with NSFW content | shared `[Mods] ShowNsfwContent` | this PC | Config · Sources | Config · Sources | stay | live |
| Local server folder | shared `[Mods] DedicatedServerPath` | this PC | Config · Local dedicated server | same | stay | live (Apply) |
| Install view | shared `[Mods] InstallView` | this PC | Install toolbar switch | home Config · Mods menu; mirror Install toolbar | overlap | live |
| Edit other mods' settings | ui `[Settings] EditOtherModsSettings` | this PC | raw | Config · Mods menu | move | reload → **live** |
| Background intensity | shared `[Workspace] BackgroundIntensity` | this PC | raw | cfg | **cfg-only 2026-10-06** (user: "No user will care to change those"; [trim](#trim-2026-10-06)) | live |
| Background contrast | shared `[Workspace] BackgroundContrast` | this PC | raw | cfg | **cfg-only 2026-10-06** (same reason) | live |
| Game menu tabs in back/forward | ui `[Ui] MenuHistoryInnerTabs` | this PC | raw | cfg | **cfg-only 2026-10-06** (same reason) | live |
| Authenticate-as-admin button | shared `[Ui] AdminAuthButton` | this PC | raw | cfg | **cfg-only 2026-10-06** (same reason) | reload* |
| Download limit (MB) | shared `[Install] MaxDownloadMB` | this PC | raw | Config · Sources | move | live |
| Current game update tag | shared `[Install] CurrentUpdateTag` | this PC | raw | cfg | **cfg-only 2026-10-06** (same reason) | live |
| Package index path | shared `[Mods] IndexPath` | this PC | raw | Config · Sources · Advanced | move | live* |
| Keep the in-game frame canonical | shared `[Workspace] CanonicalInGameFrame` | this PC | raw | Config · Sources · Advanced | move | live |
| Nexus API key | shared `[Mods] NexusApiKey` | credential | cfg | cfg | cfg-only | — |
| Local index folder (test) | shared `[Install] LocalIndexPath` | test | raw | cfg | cfg-only | live* |
| Default install target (unused) | shared `[Install] DefaultTarget` | legacy | cfg | cfg (retire) | cfg-only | — |
| Treat the local server as remote (test) | shared `[Install] TreatLocalServerAsRemote` | test | raw | cfg | cfg-only | live |
| Install filters, sort, Simple's Show chip | none (session memory per catalog) | this PC | Install toolbar | unchanged; optional "remember" switch | — | — |

### Progenitor on this PC (home: Progenitor's page, Your settings)

| Setting | File · key | Today | Target | Change | Applies |
|---|---|---|---|---|---|
| 19 Core keybinds: `OpenSettingsPanel` (F10), `NavigateBack`, `NavigateForward`, `ReleaseInput`, `PhotoMode`, `PhotoModeExit`, `PhotoModeLockOn`, `PhotoModeHideHud`, `PhotoModePause`, `PhotoModeZoomIn`, `PhotoModeZoomOut`, 8 `PhotoMode<Forward…Slow>` flight keys; since 2026-10-06 also `TeleportToMapPoint` (unbound by default, map scope: the [debug buff](modules.md#debug-tools-debug)'s map teleport; authenticated admins use the game's Ctrl+Shift+click); `DyeAllTestColor` was registered on 2026-10-08 and removed the same day, so 20 | ui `[Keybinds] <id>` | Page·You Keys + Keybinds tab | the same (home Page·You · Keys; mirror Keybinds tab) | overlap (existing) | live |
| The game's keys win clashes | ui `[Keybinds] EnforceGameWins` | Page·You | cfg | **cfg-only 2026-10-06** (user: "No user will care to change those"; [trim](#trim-2026-10-06)) | live |
| Mouse capture for panels | ui `[Ui] CaptureMode` | Page·You | cfg | **cfg-only 2026-10-06** (same reason; the UI group is gone) | live |
| Photo mode: Enabled, HideHudOnEnter, HideNameplates, LookSensitivity | ui `[PhotoMode] …` (4) | Page·You | Page·You · Gameplay | stay | live |
| Build menu search: Enabled, ClearOnClose, Breadcrumbs | ui `[BuildSearch] …` (3) | Page·You | Page·You · Gameplay | stay | live |
| Dye all: Enabled | ui `[DyeAll] Enabled` | — (the former mod's own page) | Page·You · Equipment | new (Dye all in Core, 2026-10-08). IncludeCovered and HoldSeconds were drawn, then removed the same day: covered pieces are always dyed and the hold is always 1 s | not live |
| Dye all: MatchPalettes, LegacySettingsImported | ui `[DyeAll] …` (2) | — | cfg | new (Dye all in Core, 2026-10-08). TestColorIndex was cfg-only for the developer key, then removed with that key | not live |
| Chat box: VisibleSeconds, KeepVisible, StayWhileHovered, ClickSafe | ui `[Chat] …` (4) | — | Page·You · Chat | new (CHAT1) | live |
| Chat commands: ReplyShortcut, NameCompletion | ui `[Chat] …` (2) | — | Page·You · Chat | new (CHAT2) | live |
| Chat view: View, ReplacedBy, Timestamps, CollapseRepeats, KeepHistory, HistoryLines, Mentions, MentionSound, MentionWords, FontSize, BackgroundOpacity, LockPosition, Recall, MutedPlayers, LongMessages | ui `[Chat] …` (15) | — | Page·You · Chat | new (CHAT5-9) | partly live |
| Chat box layout: Tabs, ActiveTab, OffsetX, OffsetY, Width, Height | ui `[Chat] …` (6) | — | cfg (set from the chat box: tab line, dragging, quick menu) | new (CHAT6) | partly live |
| Load Progenitor in private games | hosted `[Hosted] Enabled` | cfg | Page·You · Private games | move | at the next private-game start |
| Hosted dry run; plugins folder mode | hosted `[Hosted] DryRun`, `ServerPluginsMode` | cfg | cfg | cfg-only | next private-game start |

### Server-wide (home: Progenitor's page, Server settings)

| Setting | Store · key | Today | Target subheading | Change | Applies |
|---|---|---|---|---|---|
| Message, Next reset, Show the message, Show the mods page (RESETWIPE1 2026-10-06: Repeat, Repeat every, Countdown precision and After the reset are cfg-only now) | msg `[ServerMessage] Message`, `NextReset`, `ShowMessage`, `ShowModsPage` (4; `ResetRepeat`, `ResetEvery`, `ResetPrecision`, `ResetPassedText` cfg-only) | Page·Server · Server message | When players join | stay | live (ConfigSync; catalog re-signed) |
| Players joining: recommended, required client mods, policy mode, approved list | joinadmin `recommended`, `requiredClient`, `clientPolicy.mode`, `clientPolicy.approved` (4) | the Players joining panel | Mods players need (panel) | stay | live (re-signed; applies to the next join) |
| Install window | shared `[Networking] InstallGraceSeconds` | the panel | Mods players need (panel) | stay | live (each refused join) |
| If a player's mods break the rules while playing | joinadmin `clientPolicy.midSession` | the panel | home: the panel; mirror: a row under Mods players need | overlap | live |
| Version matching and the per-mod rules (mod compatibility C3) | joinadmin `versionMatching`, `serverModRules`; shared `[ClientPolicy] VersionMatching` (cfg-only) forces the mode | the panel | home: the panel; mirror: a radio row and a read-only `Rules for one mod` line under Mods players need | overlap | the next join (catalog re-signed) |
| Players whose mods can't be checked | joinadmin `clientPolicy.unverified` | the panel | home: the panel; mirror: a row under Players without Progenitor | overlap | join |
| Server list description, Progenitor signature, Status | list `[ServerList] Description`, `Signature`, `Published` (3) | Page·Server · Server list | Server list | stay | live |
| Message without Progenitor | msg `[ServerMessage] NoProgenitorMessage` | Page·Server · Server message | Players without Progenitor | move | live |
| Seconds before they're disconnected | shared `[Networking] HandshakeAnswerTimeoutSeconds` (20; 0 turns the deadline off) | cfg | Players without Progenitor (admin channel `answer-timeout`, 0 to 300, like the install window) | move | join |
| Allow photo mode | shared `[PhotoMode] AllowOnThisServer` | cfg | Gameplay (decision 4; admin channel `photo-mode`) | move | live |
| Ally-safe hits | shared `[Ai] AllySafeHits` | cfg | Gameplay (decision 4; admin channel `ally-safe-hits`) | move | live |
| Remote mod installs | shared `[RemoteInstall] Enabled` | cfg | Operator (decision 8; admin channel `remote-install`; turning it off asks first) | move | live (the next request) |
| Admins who may install mods | shared `[RemoteInstall] AllowedSteamIds` | cfg | Operator (decision 8; admin channel `allowed-steam-ids`; removing yourself asks first) | move | live (the next request) |
| Notice timing: NoAnswerNoticeSeconds (0 to 300), NoAnswerNoticeRepeatSeconds (2 to 300), NoAnswerCreationLimitSeconds (0 to 3600) | shared `[Networking] …` (3) | cfg | Operator (decision 8; admin channel `notice-seconds`, `notice-repeat-seconds`, `notice-creation-limit-seconds`) | move | live (the next countdown step) |
| The lock list | shared `[ServerSettings] LockedInGame` (empty) | — | cfg ([locking](#locking-settings-in-the-servers-config-file)) | cfg-only | live (each use) |
| The deprecated list lock | shared `[RemoteInstall] AllowedSteamIdsInGame` (true) | — | cfg (an alias in the lock list) | cfg-only | live (each use) |

**Every row of this table, and every Server rules row, is lockable** ([below](#locking-settings-in-the-servers-config-file)).

### Operator, diagnostic and developer settings (cfg only)

| Settings | File · section | Side | Applies |
|---|---|---|---|
| `NoProgenitorGraceSeconds` | shared `[Networking]` | client | live* |
| `ReceiveBurstPackets`, `ReceivePacketsPerSecond`, `MaxSyncedPrefabsPerJoin` | shared `[Networking]` | server, server, client | reload |
| `DenyTimeModDescriptor` | shared `[Networking]` | server | live* |
| `DescriptionInChat` (default false: the server's description is not posted to a joining player's chat; [server-list-description.md](server-list-description.md#the-description-in-chat), added 2026-10-06) | list `[ServerList]` | server | live (each join) |
| `ThunderstoreBaseUrl`, `ConnectTimeoutSeconds`, `StallTimeoutSeconds`, `TotalTimeoutSeconds` (`Enabled` and `AllowedSteamIds` moved to Operator, decision 8) | shared `[RemoteInstall]` | server | live |
| `FallbackRoot`, `Assign`, `Rename`, `Disable` | shared `[Commands]` | both | live |
| `Directory` | shared `[Persistence]` | server | world load |
| `SkipCoreHarmony`, `SkipCoreHarmonyAllowLiveWorld`, `SkipCoreModules`, `SkipCoreModulesAllowLiveWorld`, `HolderScanWide`; `CollectBeforeWorldLoad` | shared `[Diagnostics]` | both | restart (Bootstrap); live* (the last) |
| `WatchCore` | shared `[Reload]` | both | restart |
| `DropUnknownPrefabs`, `FailLoadInstead`, `MaxDropPercent`, `MaxDropEntities`, `MinPrefabLookupCount` | shared `[SaveSafety]` | server | restart |
| `RewriteMode` | shared `[Compat]` | both | reload |
| Archive mode and `InteropArchives` | shared `[Resilience]` | both | reload |
| `SearchRadius`, `SearchStep`, `WalkableCheck` (the [debug buff](modules.md#debug-tools-debug)'s map teleport search; 2026-10-06, moved from the Sample's cfg) | shared `[DebugMap]` | server | live |
| `HoldSeconds` (the debug buff's map teleport hold; 2026-10-06) | ui `[DebugMap]` | client | live |

The dev bridge's own cfg (`[Bridge]` `Enabled`, `Port`, `TokenFile`; `[ExtraInstances]` `AllowExtraInstances`, `LoopbackPort`; `[Acquisition] AllowCatalogLoad`) belongs to `Progenitor.Dev`. Players never have it, and it is out of scope.

`*` means the "Applies" value was inferred from where the value is read, without tracing every read.

### Counts

These count Progenitor's 106 drawn or cfg settings (the 19 Core keybinds count one each), plus actions and the new vanilla rows. The dev bridge's 6 are not counted.

| Change | Count | Which |
|---|---|---|
| **Stay** where they are | 29 | 4 on the Configuration tab, 9 in Your settings, 16 in Server settings (8 message rows, 3 server-list rows, 5 Players joining values) |
| **Move** | 13 | 9 cfg or raw rows onto the Configuration tab; `[Hosted] Enabled` to Your settings; `NoProgenitorMessage` to its new subheading; `HandshakeAnswerTimeoutSeconds` and `AllowOnThisServer` onto Server settings |
| **Overlap** (one home, mirrors) | 22 | 3 new (Install view; the mid-session choice; can't be checked) and 19 existing (Core keybinds on the page and in the Keybinds tab) |
| **cfg-only** (stay undrawn) | 42 | 36 operator and diagnostic settings, 4 mod-manager credentials and test hooks, 2 hosted internals |
| **New** (vanilla server settings) | 9 editable rows over 10 game keys (scheduled wipes is two), 4 read-only rows | [Server rules](#vanilla-server-settings-server-rules) |
| **Decisions 4 and 8** | +6 moved, +1 cfg-only | `[Ai] AllySafeHits`, `[RemoteInstall] Enabled` and `AllowedSteamIds`, and the three notice-timing values move from cfg-only onto Server settings; `[ServerSettings] LockedInGame` is new and cfg-only (`[RemoteInstall] AllowedSteamIdsInGame`, added and deprecated the same day, stays as its alias) |
| **Debug tools (2026-10-06)** | +4 cfg-only, +1 keybind | The debug buff moved from the Sample into Core: `[DebugMap] SearchRadius`, `SearchStep`, `WalkableCheck` (shared, server) and `HoldSeconds` (ui, client) are cfg-only operator/developer settings; `TeleportToMapPoint` joins the Core keybinds (overlap, like the other 19). There is no `[Debug] Enabled` switch: every debug command is admin-only. |
| **Trim (2026-10-06)** | 7 moved to cfg-only, 1 note removed | Background intensity, Background contrast, Game menu tabs in back/forward, Authenticate-as-admin button, Current game update tag, Mouse capture for panels, The game's keys win clashes; the Nexus Mods note ([trim](#trim-2026-10-06)) |
| **Actions that move** | 2 | **Progenitor settings** (becomes the link row); **Uninstall Progenitor from this client** (to Your settings, open question 1) |

## Vanilla server settings (Server rules)

The settings behind the **Join Game** browser's filters and columns, editable from the client by a server admin when a change needs no wipe. Source: the internal research notes, including a native-code follow-up (2026-10-05) that confirmed or corrected every row below. The recommended values were approved on 2026-10-05.

**Scheduled wipes, by date and confirmed (WIPECONFIRM1, decided 2026-10-06; source-checked and game-free tested in `WipePlanTests`, not live-verified).**
- **The game's rule** (research §5.7): at server start only, if the server's local date is on or after `StartDate.json` + **Scheduled wipes every (days)** (moved forward to the next **Wipe on** weekday; Any: no move), the game moves the save to `<save>_<ticks>` and starts a new world, which gets a new start date. The browser shows the Scheduled Wipes tag and the days until the wipe. The two rows' help says this.
- **What a wipe takes with it.** Only the world folder moves: mod data kept through `ctx.Save` (its `progenitor\` folder) goes into the archive and the new world starts without it. Mod config files and Progenitor's server-level store (`<persistentDataPath>\progenitor-server`, `ctx.ServerSave`: season stats, bans, carried-over rewards) are untouched, as is any file a mod writes outside the world folder. For mod authors: [Where should my data go?](modules.md#where-should-my-data-go)
- **Next wipe** (after Wipe on): the date-time picker in the server's time zone. A picked day sets **Scheduled wipes every (days)** to the whole days from the world's start to that day (at least 1, at most 365; a past day, a day not after the start, or an unknown start is refused with the reason), and **Wipe on** stays when the day falls on it, else becomes Any. The line under it reads `= every 34 days on Any from the world's start (Thu, Sep 10, 2026)`. **Scheduled wipes every (days)** at 0 is no scheduled wipe. Not done: an "only on <weekday>" choice inside the picker; pick the weekday with **Wipe on**.
- **The confirmation**, before any change to the interval, the weekday or the date (`WipePlan.For`): before → after (`Scheduled wipe: every 30 days on Any → every 34 days on Wednesday`), the wipe date (`Wed, Oct 14, 2026, at the first server start after 12:00 AM (server time)`, plus the player's time when it differs), the time until then, the world's start date and age, and what happens (`archives this world to <save>_<ticks> and starts a new world`). A change that makes a wipe due adds `This world is already N days old: the NEXT restart wipes it.` and the dialog is destructive. **Typing:** a change that makes a wipe due or earlier, or turns wipes on, needs `WIPE` typed; one that keeps the date needs `CONFIRM`; turning wipes off or moving the date later needs only a click. The word is case-insensitive and the main button stays disabled until it matches. Cancel changes nothing. The server still refuses a wipe change without the confirmation flag.
- **Bridge:** `mods_ui dialog_type {text}` types into the open prompt; `mods_ui dialog_choose {choice: yes|no}` answers; the dialog readback has `confirmWord`, `typed` and `mainEnabled`. `mods_ui field_apply {label, text}` puts `text` in the row's field and presses its Apply (FIELDAPPLY1: `text` used to be ignored, so the unchanged value was sent and nothing happened); its readback has `confirmation` and `dialog`. A date row's readback has `dateRow` and `openCell`, so `click_at {row: "<dateRow>/open"}` presses its Set/Change button; `datetime_day` / `datetime_done` drive the picker.
- **Recipe (writes nothing):** 1. `mods_ui {"action":"select_tab","tab":"server"}`. 2. `mods_ui {"action":"field_apply","label":"Scheduled wipes every (days)","text":"3"}`: `confirmation true`, `dialog.confirmWord "WIPE"`, `dialog.mainEnabled false`. 3. `mods_ui {"action":"dialog_type","text":"WIPE"}`: `mainEnabled true`. 4. `mods_ui {"action":"dialog_choose","choice":"no"}`. 5. `server_rules` (or `widget_read`): ResetDaysInterval next is still `0`. 6. The date path: `widget_read` the Next wipe row for `dateRow`, `click_at {"row":"<dateRow>/open"}`, `datetime_day {"day":N}`, `datetime_done`: the same dialog; `dialog_choose no`; still `0`.
- The message's own date picker is labeled **Next reset (you wipe by hand)** and shows only while scheduled wipes are off ([join-flow.md](join-flow.md#server-message-settings-admins)).

**Status.** Stage 6 (read-only) is built, game-free tested (`ServerRulesTests`, `ServerSettingsPageTests`) and not live-verified. Stage 7 (writes) is built, game-free tested (`ServerRulesTests`, `ServerSettingsPageTests`) and not live-verified. The 2026-10-05 live pass read the rules and wrote Clan size, the clamps, the password and a declined wipe; the [review fixes](#review-fixes-2026-10-05) after it are game-free tested only.

**The subheading** is **Server rules · applied at the next restart** (decision 2). It sits after **Players without Progenitor** and before **Gameplay**. **Server name** is under **Server list**, because it is a listing field, like the description. Every player of a server that has the channel sees these rows; a player who is not an admin there sees them read-only, drawn as every other server row a non-admin sees: `Admin only · <value>` (no **Server** chip on the Server tab since 2026-10-06; an empty value reads a muted `Not set`; the password only `Set` / `Not set`), the help line `Only an admin of this server can change it.`, and no field or **Apply** (live finding 2026-10-06; readback `control: readonly`, `viewOnly: true`). A server without the channel (an older Progenitor), or no server, draws no Server rules subheading.

| Row | Game key (file) | Control | Applies | Notes |
|---|---|---|---|---|
| Server name | `Name` (Host) | Text, at most 64 characters, one line (line breaks and tabs removed) | restart | Written to the file only. The running server lists the name from its discovery data (`_ServerDiscoveryData.Data.ServerName`, a native struct inside a struct), which Progenitor does not rewrite; the game reads the file's name, and runs its own `ValidateServerName`, at the next start. A rename that changes only capitals is written. Under **Server list**. |
| Game mode | `GameModeType` (Game) | PvP / PvE | restart | |
| Clan size | `ClanSize` (Game) | Number 1–10 (the browser parses only `cs1`..`cs10`; the game accepts up to 50) | restart | Help: existing clans larger than a lowered limit keep their members; only new invites are blocked (research §5.5). |
| Difficulty | `GameDifficulty` (Game) | Relaxed / Normal / Hard (listed as Easy / Normal / Hard) | restart | A `GameDifficultyPreset` whose file sets it makes the row read-only: `Set by the difficulty preset <name> in ServerHostSettings.` The row sets the enum alone (the listing tag and the game's `GameDifficulty` AI condition, source 98 in the game's boss AI conditions), not the preset's modifiers; each option's description names the [game's preset of that difficulty](#game-difficulty-presets). |
| Blood-bound equipment | `BloodBoundEquipment` (Game) | Switch | restart | Feeds Merciless. |
| Castle heart damage | `CastleHeartDamageMode` (Game) | Only when decaying / Players can destroy it / Players can seize or destroy it | restart | Feeds Merciless. |
| Listed as Merciless | derived: `!BloodBoundEquipment` or `CastleHeartDamageMode != CanBeDestroyedOnlyWhenDecaying` (research §5.1) | Read-only line | — | `No`, `Yes`, or `No → Yes after the restart` when a pending change flips it. |
| Password | `Password` (Host) | Write-only: `Set` / `Not set`, a field and **Apply** | **live** | Never shown or read back, not even to admins; the audit line writes `<hidden>`. At most 32 characters, the game's own runtime setter's limit: a longer one is refused with a message (over 64 the server quits at its next start). Empty or only spaces removes it, after **Remove the password?**. Applied live: written to the file, then set with the game's own `ServerBootstrapSystem.SetServerPassword` (research §5.4). Every **Apply** writes, the same password too, so an answer never confirms a guess. The field is masked while typing. |
| Max players | `MaxConnectedUsers` (Host) | Number 1..(128 − `MaxConnectedAdmins`) | restart | The admin reserve is shown in the help (`MaxConnectedAdmins`, clamped 0..128). Never written above the clamp: the listing would advertise slots that do not exist (research §5.2). The page refuses a number above the state's bound before sending. A file value above the clamp (shown clamped) is compared as written, so it can be written down to the clamp. |
| Scheduled wipes every (days) | `ResetDaysInterval` (Host; 0 = off) | Number 0–365 | restart | **Destructive:** see warnings. |
| Wipe on | `DayOfReset` (Host) | Any / Monday … Sunday | restart | The reset date (start date + interval) moves forward to the next such weekday, 0 to 6 days; `Any` does not move it (research §5.7). |
| Next scheduled wipe | computed: `StartDate.json` + interval, moved forward to the wipe day (research §5.7) | Read-only line | — | `Off` when the interval is 0. The server checks once, when it starts. |
| Backups (admins only) | wipe archives beside the save and Progenitor's own backups | Summary line, **Restore an earlier world · Show**, Restore pending / Cancel restore, Last restore, **Keep Progenitor backups** (0–100) | next start | WORLDRESTORE1: restores a whole earlier world at the next start, after a verified backup of the current one; typed `RESTORE` confirmation. See [world-restore.md](world-restore.md). |
| Territory | computed `ServerActivityLevel` (tags `sal_hi` / `sal_mi`) | Read-only: LO = Abundant, MI = Moderate, HI = Limited (confirmed, research §5.6) | — | Not a setting. Recomputed every 60 s. |
| Days running | `<save>/StartDate.json` | Read-only | — | World state. Never edited. |
| Standard settings | the `std` tag | Read-only (Yes / No) | — | The game sets it when the effective game settings hash equals one of its bundled presets (research §5.1). It can only be predicted: while any game row other than Clan size is pending, the line adds `may change after the restart`. |

**Option descriptions** (OPTIONHOVER1, 2026-10-06). Each choice row's options carry an explanation (`ServerRuleSpec.ChoiceDescriptions`) that the description column shows while the pointer is over the option in the open list ([option descriptions](mod-settings.md#option-descriptions)): Game mode (what PvP and PvE allow), Difficulty (the game's preset of that difficulty, below), Castle heart damage (who can destroy or seize a heart; Merciless or not), Wipe on (when a due wipe happens). Source-checked and game-free tested (`OptionHoverDetailsTests`), not live-verified.

#### Game difficulty presets

The game's bundled difficulty presets, read from the server install's `VRisingServer_Data\StreamingAssets\GameDifficultyPresets\` (the client install's `VRising_Data\StreamingAssets\GameDifficultyPresets\` holds the same three files; checked 2026-10-06). A `GameDifficultyPreset` names one of them and lays it over the game settings (research §5.3). `ServerRules.DifficultyPresets` records them; a test compares the record with the installed game's files (the bundled server's and the client's) when they are present.

| File | `GameDifficulty` | Enemy power (`UnitStatModifiers_Global.PowerModifier`) | Enemy health | V Blood health / power / level increase | Sun damage | Blood drain | Loot (`DropTableModifier_General`) | Durability drain |
|---|---|---|---|---|---|---|---|---|
| `Difficulty_Easy.json` | 0 (Relaxed) | 0.7 | 1 | 0.8 / 0.6 / 0 | 0.75 | 0.75 | 1 | 1 |
| `Difficulty_Normal.json` | 1 (Normal) | 1 | 1 | 1 / 1 / 0 | 1 | 1 | 1 | 1 |
| `Difficulty_Brutal.json` | 2 (Hard) | 1.4 | 1 | 1.25 / 1.7 / 3 | 1.0 | 1.0 | 1.25 | 0.5 |

### Rules

- **The next start's values, in the game's order.** Host keys: `StreamingAssets` < `<persistentDataPath>\Settings` < the save folder's `ServerHostSettings(.json)` < environment < command line (SERVERDESC1's resolver, extended with the rows' override names). Game keys (research §5.3; `GameSettingsResolution`):
  1. code defaults (`GameModeType` PvP, `ClanSize` 4, `GameDifficulty` Normal, `BloodBoundEquipment` on, `CastleHeartDamageMode` CanBeDestroyedByPlayers);
  2. `StreamingAssets\Settings\ServerGameSettings.json`;
  3. `<persistentDataPath>\Settings\ServerGameSettings.json`;
  4. the save folder's `ServerGameSettings` or `ServerGameSettings.json` (a dedicated server honors it);
  5. a `GameSettingsPreset` **replaces** every file layer: code defaults plus `StreamingAssets\GameSettingPresets\<name>(.json)`;
  6. a `GameDifficultyPreset` is **laid over** the result from `GameDifficultyPresets\<name>.json`;
  7. the clamps (`ClanSize` 1..50).

  Partial files layer key by key; an enum may be its word or its number; a key repeated in one file counts once, the last one (read and written). "No preset" is empty, `default` or `none`, in any case. `-preset` / `-p` / `VR_PRESET` and `-difficultyPreset` / `-dp` / `VR_DIFFICULTY_PRESET` win over the host files; game keys have no per-key command-line or environment override. `-p` is the preset and `-P` the password: those two are matched with their case.
- **The loaded values** are read, never written, from `ServerGameSettingsSystem.Settings` for game keys (what EOS reads and `std` hashes) and from `SettingsManager.ServerHostSettings` for host keys. Never from `SettingsManager.ServerGameSettings`: it misses the save folder and both presets. Max players' loaded value is the enforced one (clamped by the admin reserve).
- **Pending restart.** A restart row is pending while the next start's value differs from the loaded one. The row's help line says `Changed: applies at the next restart.`, its readback has `pending: true`, and the subheading's first line, the **Pending changes** status row, lists them (PENDING2, 2026-10-06: a summary `2 changes apply at the next restart`, then one muted amber line per change with option labels, `Clan size: 4 → 6`, `Castle heart damage: Players can destroy it → Only when decaying`, at most 8 then `+N more`, and a warning line when the wipe settings make a wipe due; wrapped; was one truncated line; `None. Changes here apply when the server restarts.`, muted; [mods-menu.md](mods-menu.md#the-server-tab)). The state is recomputed from the files and the running game on every request, so a Core reload or a rejoin shows it again. Restarting the server is the operator's job; for a server in this PC's local folder the Configuration tab's **Stop server** / **Start server** still apply.
- **Never edit a restart-only value in memory.** Editing the runtime game settings makes only EOS advertise the new value, while gameplay (the ECS singleton) and the cached Steam tags keep the old one. Restart rows change only the file. Only the Password is applied live as well (Server name waits for the restart).
- **Never create the game's `ReloadSettings` file.** It re-reads only the host settings (research §5.3, corrected): the password and the listing values would change live while slots and game settings stay on the old values.
- **Write the highest file layer that exists**, for host and game keys alike: the save folder's file when it exists, else `<persistentDataPath>\Settings\<file>.json` (created with only that key when missing); never `StreamingAssets`. The writer generalizes `ServerListDescription.Persist` (`JsonFileEdit`): only the value's bytes change; keys, order, comments and the byte-order mark are kept; temp file, then replace; an unchanged value is not written.
- **Overrides win.** An overridden row stays editable, because the file still changes. Its help line says `The start command sets -maxUsers: it replaces this value at every start. Remove it to keep the file in charge.` (or names the environment variable).
- **Presets.** While a `GameSettingsPreset` is named, every game row is read-only with `This server uses the <name> preset, which replaces these settings at start.` (decision 6: no **Use custom settings**). A named preset applies only when the install's `StreamingAssets\GameSettingPresets\` folder exists (the game loads none otherwise, and the file layers stand). When the named preset's file does not exist in that folder, the server silently runs the game's built-in defaults: the rows are read-only and a **Preset** line warns about it. When the install is unknown, the preset is taken to apply (the rows stay read-only) but is never called missing. A difficulty preset makes read-only only the rows its file sets, and only when its file exists.
- **Scheduled wipes warning.**
  - The game checks for a reset once, when the server starts and is about to load a save (`CheckIfResetServer`). The reset date is the start date (`StartDate.json`) plus the interval, moved forward to the wipe day (0 to 6 days; `Any`: no move); the server resets when the local time has reached it.
  - A reset **moves** the save folder to `<save>_<ticks>` and starts a fresh world (the old world is archived, not deleted).
  - **A confirmation, enforced by the server.** A change of **Scheduled wipes every** or **Wipe on** needs one when it turns wipes on (0 → more), brings the next reset date to today or earlier, or the world's start date is not known (`ServerRules.WipeNeedsConfirm`). The server computes this from its own clock and `StartDate.json` and refuses such a write without `confirm: true` in the request (`Confirm first: this change may reset this world at the next start (the request needs confirm: true).`, audited); nothing is written.
  - The page asks in the same cases: **Turn on scheduled wipes?** (turning on) or **Change the scheduled wipes?** (lowering the interval or changing the day), with the world's start date, its days running and the reset date. The body starts `The next start may reset this world: the current save is moved to a backup folder (<save>_<ticks>) and a fresh world starts.`, or, when the reset date has already passed, `The next start resets this world: …` and says the date has passed. **Turn on scheduled wipes** / **Change them** sends the change with `confirm: true`; **No** sends nothing. A change that keeps the date ahead, and turning wipes off, ask nothing.
  - The bridge's `server_rules write` passes `"confirm": true` for such a change, or the server refuses it.
  - This is unrelated to the server message's reset, which is a notification countdown ([join-flow.md](join-flow.md#server-message-settings-admins)).
- **No branch on deployment type.** The rows depend only on which file layers the resolver finds (AGENTS rule).
- **The lock list** ([locking](#locking-settings-in-the-servers-config-file)) covers every rule: the group `server-rules`, or a rule's own id `progenitor/ServerRules/<Key>`. Server name is also in `server-list`.

### Wire and authorization

- **Channel.** One new Core-reserved id, **`'PrSv'` (`0x50725376`)**, shaped like `'PrJa'`: a version byte, a kind byte (request, answer, changed), a nonce, then UTF-8 JSON of at most 15 KiB.
  - Operations: `get` and `write {key, expected, value, confirm}` (`confirm` only for a wipe change that needs it).
  - The answer carries the whole state: per row `{key, loaded, next, pending, override, editable, reason, lockedBy, source}`, the password as `set` / `not set` only, `restartPending`, Merciless now and next, the territory, the start date and days running, Standard settings, the next reset date, a preset warning, the admin reserve and the largest Max players.
  - When a write lands, the server sends `changed` to the other players who asked in this session (they have the handler), so an open page refreshes.
- **Who has the channel.** As built, the Players joining hello says so (`rules: true`) instead of a new handshake capability bit: the hello already goes to every capable client after the handshake, admin or not, and a client never sends `'PrSv'` to a server that did not say it has it.
- **Authorization, rate limit, audit.** The rules of every server-wide setting apply ([settings-scopes.md](settings-scopes.md#authorization-and-validation-server-in-order)):
  - Identity comes from the connection. The admin list or `adminauth` authorizes a write. `[RemoteInstall] AllowedSteamIds` does not apply.
  - 10 writes per SteamID per minute; 30 reads per minute on their own budget.
  - Every write and every refusal is audited in `progenitor-settings.log` as `progenitor/ServerRules/<Key>`. The password's value is never written (`<hidden>`).
  - Any player who passed the handshake may `get`; nothing in the answer is a password.
- **Server-side checks:** the lock list, the row's own read-only reasons (presets, no known file), an expected-value conflict, type and bounds. A file that cannot be read or written refuses with its reason and is never overwritten blind.

## Locking settings in the server's config file

Requested 2026-10-05. **Status: built, game-free tested (`ServerOptionsTests`, `ServerSettingsPageTests`, `ServerRulesTests`), live-verified once on 2026-10-05 for an admin-channel row,** on every edit path: `'PrJa'`, `'PrSe'` and `'PrSv'`.

- **The key.** `[ServerSettings] LockedInGame` in the server's `progenitor.cfg`, default empty (everything editable by admins). It is cfg-only: never drawn, never editable in the game, never sent as a ConfigSync row. It is read on every use, so a cfg reload applies it at once. A server binds it on its own as soon as the shared cfg is available (`ServerSettingsLock.Install`, independent of the admin channel); until it has, every edit on `'PrJa'`, `'PrSe'` and `'PrSv'` is refused with `The server has not loaded its settings lock list yet; try again in a moment.` (fail closed). Reads stay open.
- **Its value** is a list separated by commas, semicolons or spaces. Each entry is one of:
  - a **setting id**, as `settings_places` and the rows' `settingId` show it. Examples: `progenitor/ServerMessage/Message`, `progenitor/JoinAdmin/allowed-steam-ids`, `progenitor/ServerRules/ClanSize`, or a mod's server-wide row `<pluginId>/<Section>/<Key>`. The Players joining popup's lists are `progenitor/JoinAdmin/recommended`, `…/required-client` and `…/approved`; its choices are `…/policy-mode`, `…/unverified`, `…/mid-session`, `…/version-matching` (the mode and the per-mod rules) and `…/window`.
  - a **group**, one per Server settings subheading: `join` (When players join), `mods-players-need` (the Players joining popup and its mirror rows), `server-list`, `no-progenitor` (Players without Progenitor), `gameplay`, `operator`, `server-rules`, `chat` (the Server tab's Chat group, 2026-10-07). A setting belongs to the groups of its home and of every mirror: **Players whose mods can't be checked** is in both `mods-players-need` and `no-progenitor`.
  - `*`: every server setting, mods' server-wide rows included.
  - Anything else is ignored, with one warning in the server log. A well-formed id that names no known setting is kept, because a mod may register it later, and is also warned once.
- **Matching.** An exact id first, then a group, then `*`. The matching entry is the row's `lockedBy`.
- **The server refuses** an edit of a locked setting with `Locked in the server's config file (ServerSettings LockedInGame: <entry>).`, naming the matching entry, and writes the refusal, with the value before, to `progenitor-settings.log`. This holds on every channel, whatever a client sends. Reading is never locked.
- **The page** draws a locked row read-only, for admins too: an admin-channel row is grayed with its value, and a ConfigSync row reads `Locked · <value>`. The help line says `Locked in the server's config file (ServerSettings LockedInGame: <entry>).`, an admin-channel or Server rules row carries a small **Locked** marker after its name, and the readback has `locked: true` and `lockedBy`. The admin channel's state carries the list (`lock`). The page rebuilds when the list changes, and a server-wide row whose lock is lifted becomes editable again.
- **The Players joining popup** shows the note under a locked section's heading. Its buttons are grayed and do nothing; a request that reaches `Send` anyway is not sent. Its readback has `locked: {key: entry|null}` and `lock`.
- **Deprecated alias.** `[RemoteInstall] AllowedSteamIdsInGame = false` adds `progenitor/JoinAdmin/allowed-steam-ids` and logs a deprecation warning once. The refusal reads `Locked in the server's config file ([RemoteInstall] AllowedSteamIdsInGame = false).`; the page's `lockedBy` is the id.

## Mod authors' settings

Unchanged, and fully covered by [settings-scopes.md](settings-scopes.md) and [mod-settings.md](mod-settings.md):

- A mod's page shows **Your settings** and **Server settings** from its own declarations.
- Author sections become subheadings.
- A mod gets no rows on the Configuration tab or on Progenitor's page.
- `ctx.Settings.Page` gains nothing for this spec.

The only change a mod author sees is `settingId`, `home` and `alsoAt` on its rows' readback (`home` is always its own page).

## Bridge and readback additions

| Addition | Where | Content |
|---|---|---|
| `settingId`, `home`, `alsoAt` | every settings row in `mods_ui widget_read` (mod pages, the Configuration tab, the Install toolbar switch, the Keybinds tab) | [Overlap](#overlap-one-stored-value-many-places) |
| `page-link` rows | `widget_read` on both pages | `{target: configuration \| progenitor-page, landing: top \| server-settings, label}`; clicked with `widget_click` by key (`link-configuration`, `link-progenitor`) |
| `page-subheading` rows | Progenitor's page | `{scope, title, rows, pending}` (`pending` only under Server rules) |
| `settings_places` op | `mods_ui` | `{settingId: {home, places[], value, display}}` from one store read |
| `serverRule` on a row | `widget_read` | `{key, loaded, next, pending, override, editable, reason}`; the password reads `{set}` |
| `server_rules` op | dev bridge, both sides | client: `read`, `write {key, value, confirm}` (as the page sends it; `confirm: true` for a wipe change that needs it). Server: `read` (files, layers, loaded values, overrides), with no direct `set`, so live tests take the admin path. Readback never includes the password. |
| `nav_state` place kind | `mods_ui nav_state` | `progenitor-page@server-settings` |

## Acceptance checks (not run)

On the isolated test server and the Steam client only, inside an authorized session. Leave the client at the title screen afterward.

1. **Configuration tab:**
   - The link row is first.
   - Sources, Mods menu and Local dedicated server are drawn.
   - No Progenitor group and no server setting appear.
   - Every drawn setting's readback has `home: configuration`.
2. **Link to Progenitor's page, as admin:** **Open** lands on the Server settings heading (`nav_state` current `progenitor-page@server-settings`). **Navigate back** returns to the Configuration tab with its scroll.
3. **Link as a non-admin, and not connected:** it lands on the page's top.
4. **Link back:** Progenitor's page **Mod manager settings · Open** shows the Configuration tab. Back returns to the page.
5. **Overlap: Install view.** Switch to Advanced on the Install toolbar. The Configuration row reads Advanced. Set Simple on the Configuration tab. The toolbar reads Simple. `settings_places` agrees with both, and with `[Mods] InstallView` in the cfg.
6. **Overlap: mid-session choice.** As admin, set **Disconnect them** on the page row. The Players joining panel shows it. `join_admin read` shows `midSession: disconnect`. There is one audit line.
7. **Overlap: keybinds.** Rebind Navigate back on Progenitor's page. The Keybinds tab and `[Keybinds] NavigateBack` agree.
8. **Edit other mods' settings is live:** turning it on makes a foreign mod's row editable without a Core reload.
9. **Players without Progenitor:**
   - Set the seconds to 30. The server's `[Networking] HandshakeAnswerTimeoutSeconds` reads 30.
   - An unmodded join counts down from 30 after the character spawns.
   - Restore 20.
10. **Server rules, restart row:**
    - Change Clan size.
    - The row shows **Restart**, and **Pending changes** reads `1 change applies at the next restart` with the line `Clan size: 4 → 3` under it
    - The file layer changed, and only that value's bytes.
    - The running listing is unchanged.
    - After a server restart, the chip is gone and the listing shows the new size.
    - Restore the size and restart.
11. **Server rules, live row:** change Server name. The Join Game browser shows it without a restart, and the file persists it. Restore.
12. **Overrides and preset:** start with `-maxUsers` and the row warns. With a `GameSettingsPreset`, the Game rows are read-only with the preset reason.
13. **Scheduled wipes:** turning them on asks the second confirmation. Answer **No** and nothing is written. Do not answer yes on a world anyone needs.
14. **Non-admin:** Server rules values are read-only, the password reads `Set` / `Not set`, and a raw `write` is refused with an audit line.

## Build stages

Each stage is a small PR with game-free tests. The order keeps every intermediate state coherent.

1. **This spec** and the roadmap row.
2. **`SettingPlaces` and readback:** the table, `settingId` / `home` / `alsoAt`, and `settings_places`. No visible change. **Built, game-free tested (`SettingPlacesTests`), not live-verified.** As built:
   - `Ui/SettingPlaces.cs` holds one row per setting of Progenitor's own: setting id, home, mirrors, store (`cfg` or `join-admin`) and source (file and key). Place words: `configuration`, `install-toolbar`, `keybinds-tab`, `players-joining-panel`, `cfg` (not drawn), and `progenitor-page/<subheading>` with the subheadings `keys`, `gameplay`, `ui`, `private-games`, `when-players-join`, `mods-players-need`, `server-list`, `players-without-progenitor`, `server-gameplay` and `operator`.
   - A setting the table does not name resolves by rule: a Core keybind is at home under `progenitor-page/keys` with the mirror `keybinds-tab`; a mod's keybind is at home on `mod-page/<pluginId>` with the same mirror; any other mod setting is at home on `mod-page/<pluginId>`; an unknown Progenitor row is at home under `progenitor-page/general`.
   - Admin-channel settings use `progenitor/JoinAdmin/<op>`, the audit row id, also where the value lives in a server cfg.
   - The readback keys are on every `page-setting` row, every Keybinds-tab `keys-row` of a mod key, and the Install tab's `view` readback. `mods_ui settings_places` returns `{settingId: {home, places[], value, display, known, store, source}}`. It leaves the `cfg` rows out. `known: false` means the store has no value here: an admin-channel value before the server sent its state, or an entry no module has bound in this process.
3. **Configuration tab:** the link row, the Mods menu and Sources rows on the mod page's row kit, the moved cfg rows, and `EditOtherModsSettings` made live. Remove the Progenitor group and the raw **Progenitor settings** pane entry, and take raw rows homed elsewhere off Progenitor's raw list. Plus decision 3: the **Remember Install filters and sort** switch. **Built, game-free tested (`ConfigurationOrganizationTests`), not live-verified.** As built ([mods-menu.md](mods-menu.md#where-progenitors-settings-are-drawn)):
   - `ConfigurationPane` draws Mods menu and Sources through `ModSettingsView` in its embedded mode. That mode has no search or Reset all, no scope heading, and each section title is the game's header with its red rule. The rows are `SettingsRow`s over the entries the owning modules read, bound with the same type, default and words. Ui.cfg's two entries come through `CoreUiSettings`.
   - The Sources fold is **Expand** / **Collapse**, not Show / Hide, which the other-servers list already uses.
   - The remembered filters are `[Mods] InstallFiltersRemembered` (cfg only), versioned text.
   - The link row is a cell row, `link-progenitor` / `open`.
   - The history place is `progenitor-page@server-settings`, and the old "settings-all inside Configuration" place is no longer valid.
   - **Uninstall Progenitor from this client** stayed at the bottom of the tab, without a heading, until stage 4 moved it.
4. **Progenitor's page, Your settings:** the link row, the Keys / Gameplay / UI / Private games subheadings, `[Hosted] Enabled`, and the uninstall row (after open question 1). **Built, game-free tested (`ProgenitorPageTests`), not live-verified.** As built ([mods-menu.md](mods-menu.md#where-progenitors-settings-are-drawn)):
   - `Ui/ProgenitorPage.cs` builds Your settings, and `JoinServerSettings.BuildPage` calls it first.
   - The rows are placed by section and key, found when the page is drawn. These are the Core-only builder calls `SettingRef`, `AllKeybinds`, `Link`, `Destructive` and `Readback`. A row whose module has not bound its entry yet is left out until it is. `[Hosted] Enabled` lives in Bootstrap's shared file, which the chainloader sweep lists as another plugin's source; it is drawn editable here (`owned`).
   - The uninstall row is a builder button with the kit's confirmation dialog, in the danger tone, over the same request as before (`ConfigurationPane.WriteClientUninstall`). While a request is on disk it gives way to **Removal requested** and **Keep Progenitor**.
   - The `[Hosted]` keys are in the shared `progenitor.cfg`, not a `hosted` file: `progenitor.hosted.cfg` has not been read since the shared-file handover.
5. **Progenitor's page, Server settings:**
   - the four subheadings;
   - `NoProgenitorMessage` moved;
   - the mid-session and can't-be-checked mirror rows on the existing `'PrJa'` operations;
   - a new `'PrJa'` operation `answer-timeout` for the seconds (clamped, audited, like `window`);
   - the docs fixed where they place Progenitor's page "in the Configuration tab".
   - Plus decision 4 (**Gameplay**) and decision 8 (**Operator**), and the [lock list](#locking-settings-in-the-servers-config-file).

   **Built, game-free tested (`ServerOptionsTests`, `ServerSettingsPageTests`), not live-verified.** As built ([mods-menu.md](mods-menu.md#where-progenitors-settings-are-drawn)):
   - `ProgenitorPage.BuildServerSettings` draws six subheadings in this order: **When players join**, **Mods players need**, **Server list**, **Players without Progenitor**, **Gameplay**, **Operator**. `JoinServerSettings.BuildPage` passes it the rows its modules bind (the server message, the Players joining row, the server-list rows, the no-Progenitor message).
   - The admin-channel rows are a Core-only row kind (`AdminSettingSpec`, builder `AdminSetting`): a switch, a selector, or a field with **Apply** for a number or a list. Each reads the value in the server's last `'PrJa'` state and sends one request per change, with the server-row statuses (waiting, `Saved by the server.`, the server's clamp note or refusal). A request with no answer in 8 seconds puts the control back. Before the server's state arrives, a row shows `…` and asks for it with a `get`, at most every 5 seconds.
   - Every admin-channel row is hidden from a player who is not an admin there (decision 5: the Players joining rule). A server older than this stage sends no options, and those rows are not drawn.
   - Eight new `'PrJa'` operations (`Networking/JoinAdminOptions.cs`): `answer-timeout` (0 to 300), `notice-seconds` (0 to 300), `notice-repeat-seconds` (2 to 300), `notice-creation-limit-seconds` (0 to 3600), `photo-mode`, `ally-safe-hits`, `remote-install` and `allowed-steam-ids`. Seconds are clamped with a note, like `window`. A switch takes `true` / `false` (or on / off). The list keeps SteamID64s in the order typed, each once, at most 64; it refuses any other token. They go through the channel's rate limit, handshake and authorization. Each edit and each refusal is one audit line in `progenitor-settings.log`, with the before and after values (the whole list for `allowed-steam-ids`). The server writes the entry in its `progenitor.cfg` and saves it. `[RemoteInstall]` is then read again, so the next remote-install request uses the new value. The answer's state carries `options` (operation → value) and `locked`.
   - **The lock list** `[ServerSettings] LockedInGame` ([locking](#locking-settings-in-the-servers-config-file)) is built for every edit path: the `'PrJa'` operations (the page's rows and the popup's), administrator ConfigSync edits (`'PrSe'`) and the Server rules writes (`'PrSv'`, stage 7). `[RemoteInstall] AllowedSteamIdsInGame = false`, built first for the install list alone, is kept as a deprecated alias for `progenitor/JoinAdmin/allowed-steam-ids`.
   - Confirmations use the kit dialog. **Turn off remote installs?** comes before `remote-install` goes off. **Remove yourself from the list?** comes before an `allowed-steam-ids` change takes the right to install away from the admin making it: an empty list allows any admin, so naming only others counts. **No** sends nothing.
   - The mid-session and can't-be-checked rows are mirrors of the Players joining popup's choices. Both places read the same server state and write through the same operations (`mid-session`, `unverified`). `settings_places` now reads admin-channel values from that state, including `policy-mode` and `window`.
6. **Server rules, read-only:** the resolver for Game keys, loaded-versus-next, overrides, preset and pending. The `'PrSv'` `get`, the capability bit, the read-only rows and `server_rules read`. Before this stage, run the owed research: the `wanted-sbfilters.txt` export, the `MaxConnectedUsers` bounds and the Game-file layering. **Built, game-free tested (`ServerRulesTests`, `ServerSettingsPageTests`), not live-verified.** The owed research was done (research note §5). As built ([above](#vanilla-server-settings-server-rules)):
   - `Networking/ServerRules.cs`: the rules, the game-settings resolver (`GameSettingsResolution`, seven steps), the state builder and the JSON writer; `ServerRulesWire.cs`; `ServerRulesServer.cs` (`get`, rate limits, audit of refusals); `ServerRulesGame.cs` (the live reads); `ServerRulesClient.cs`; `ServerRulesRows.cs` (the page's rows over the row kind of stage 5).
   - The hello flag `rules` replaces the capability bit (see [Wire](#wire-and-authorization)).
   - The subheading's first line is an info row, **Pending changes** (`1 change applies at the next restart` and `Clan size: 4 → 3` on its own line, a status row since PENDING1), rather than a `page-subheading` readback row; a row's `serverRule` readback carries `{key, loaded, next, pending, override, editable, reason, lockedBy, source}` (the password `{set, nextSet}`).
   - Every row was read-only in this stage alone (`Read only in this version of Progenitor.` for an admin); since stage 7 an admin edits them, and everyone else reads `Only an admin of this server can change it.`.
   - `settings_places` lists every rule (store `server-rules`; the password reads `set` / `not set`).
   - `server_rules` bridge op: client `read`, `get` (ask now, wait for the answer); server `read` (files, layers, write targets, preset, overrides, tags and the state).
7. **Server rules, writes:** `write` for each row with the byte-preserving writer, the audit, and the scheduled-wipes confirmation (Server name was planned live; the review fixes made it a restart rule). **Built, game-free tested (`ServerRulesTests`, `ServerSettingsPageTests`), not live-verified.** As built:
   - `ServerRulesDecisions` checks a write in order: a known rule, the lock list, the row's read-only reasons (a preset, a difficulty preset's row, no known file), the expected value (the value the page showed; a conflict is refused), then type and bounds: Server name one line of 1–64 characters (line breaks and tabs removed; compared with its case, so a rename in capitals is written); a choice among the game's words (any case); Clan size 1–10; Max players 1..(128 − admins); Scheduled wipes 0–365; a switch on or off; the password at most 32 characters, blank or spaces meaning none. A value the files already hold writes nothing (`Unchanged.`, and no audit line), except the password, which is always written. The comparison is with the files' value before any override or clamp (live 2026-10-06: comparing with a `-serverName` override's value answered `Unchanged.` while the file held another name); the audit's before value is that same file value. A wipe change that may reset the world needs `confirm` (see the warning above).
   - The value is written into the highest existing file layer with `JsonFileEdit` (only its bytes change). The password is then set on the running server with the game's `ServerBootstrapSystem.SetServerPassword`; every other rule, Server name included, waits for the restart and is never edited in memory.
   - The answer carries the new state and a note: `Saved: applies at the next restart.`, `Applied now, to the next player who joins.`, `Removed: the next player joins without a password.`, or for wipes `Saved: scheduled wipes are on from the next start, which may reset this world.`; an overridden rule's note adds the override sentence. The other players who asked this session get the state pushed (`changed`).
   - The page: an admin's rule rows send each change (switches and choices at once, numbers and text on **Apply**), with the expected value; the confirmations **Turn on scheduled wipes?** / **Change the scheduled wipes?** and **Remove the password?** come first. The password field is never filled from the server and its text is never read back (`fieldLength` only).
   - `server_rules {"action":"write","key":…,"value":…}` sends a write as the page does and waits for the answer;`"confirm": true` answers the wipe confirmation.
8. **Optional:** a **Server settings ›** link on the Install tab's Simple toolbar while it acts on the server, landing like the Configuration link ([open question 7](#open-questions)). **Built, game-free tested (`InstallSimpleViewTests`), not live-verified.** As built ([mods-menu.md](mods-menu.md#install-view-simple-and-advanced-installsimple1)): a key cap `server-settings` at the right of Simple's search row, shown while Simple acts on the connected server and the player is an admin there (the Configuration link's own fact, `ModsMenu.ProgenitorLinkLanding`). It calls the same jump, `JumpToProgenitorPage(serverSettings: true)`. The caption is **Server settings »**: `»` is the Latin-1 glyph the store link already draws, and the game font is not known to have `›`. Readback `serverSettingsLink` on the toolbar row.
9. **Live acceptance** on the isolated test server; then the roadmap status moves.

## Review fixes (2026-10-05)

A review and a live pass on 2026-10-05 (Configuration tab, Progenitor's page sections, `server_rules` read and get, a Clan size write, the clamps, the password live, declining a wipe, an admin-channel lock: passed live) were followed by these fixes. **Built and game-free tested; not live-verified.**

- **Install rights:** a restriction of `remote-install` and `allowed-steam-ids` to admins the install list allows was built here and reverted on 2026-10-06 (decision 8 below: every admin edits the Operator rows; RBAC is future work).
- **Scheduled wipes:** the server-enforced confirmation ([warning](#rules)).
- **Server rules:** a text compares with its case; the raw file value counts for max players; the password is always written and set with `SetServerPassword`; Server name waits for the restart; a preset applies only when `GameSettingPresets\` exists; a repeated key counts its last copy; the client's max-players bound is the state's; the answer's operation is matched lower-cased before a push.
- **Lock list:** bound by the server on its own and fail closed until it is; refusals name the entry (`(ServerSettings LockedInGame: <entry>)`) and log the value before.
- **Page:** a server-wide switch (the Progenitor signature) is a switch, not a text field reading `true`; long names are shorter (**Message without Progenitor**, **Seconds before disconnect**, **Allow photo mode**, **Repeat notice every (seconds)**, **Creation time limit (seconds)**, **Mods break rules while playing**, **If mods can't be checked**; the popup keeps its own headings); **Apply** sits at the right of a one-line field (a text area keeps it on its own row); subheadings are gold small caps, a step larger, in both scopes; a rebuild for the server's answer keeps the page's scroll position; a locked row says which entry locks it and carries a **Locked** marker; the password field is masked.
- **Live re-test fixes (2026-10-06; built and game-free tested, not yet re-checked live):** an inline-**Apply** row keeps its name in the name column (every settings-row readback now carries `caption` and `captionShown`; a field drawn with an empty name is the regression to look for); the inline **Apply** caption uses the grid buttons' size; the buttons reach into the gutter short of the reset arrow, so the field keeps nearly the whole bar; a non-admin reads Server rules and Server name as `Admin only · <value>`; a write compares with the files' value, not an override's; an unchanged write logs nothing; a refused wipe change's audit line is covered by a test (the live log showed none; not reproduced game-free). Live re-test of `00cb06bb` passed all of these.
- **Second live re-test fix (2026-10-06; built and game-free tested, not yet re-checked live):** on a non-admin's Server rules and Server name rows the **Server** chip covered the start of `Admin only · <value>`: the server's value arrived after the page was drawn and the chip stayed where the shorter `Admin only · …` ended. A read-only row's value written after the layout now places the chip again, left of the text with one gap; those rows are drawn at normal brightness like the other `Admin only` rows.

## Decisions (user, 2026-10-05)

The eight open questions were answered; the build follows these, and the sections above are read with them:

1. **Uninstall Progenitor from this client** moves to Progenitor's page, under Your settings.
2. **The vanilla subheading** is **Server rules · applied at the next restart**. Server name stays under **Server list** (a listing field; planned live, it applies at the next restart since the review fixes).
3. **Install filters and sort:** the Configuration tab gets a **Remember Install filters and sort** switch (off by default; when on, they survive a restart).
4. **Gameplay** is a new subheading under Server settings holding **both** `[PhotoMode] AllowOnThisServer` ("Allow photo mode") and `[Ai] AllySafeHits` ("Ally-safe hits": hits with no team check from units spawned through Progenitor skip allies; vanilla units unchanged). Both live.
5. **Admin-channel rows** (the mid-session choice, the seconds, can't-be-checked) stay hidden from non-admins, as Players joining is.
6. **`GameSettingsPreset`:** while a preset is set, the Game rows are read-only with a note naming the preset; no **Use custom settings** button.
7. **The Install tab mirror** (stage 8) is wanted: a **Server settings ›** link on the Simple view for admins acting on a server.
8. **Operator settings are drawn and editable by admins:** `[RemoteInstall] Enabled`, `[RemoteInstall] AllowedSteamIds` and the three notice-timing values get rows under Server settings (a new **Operator** subheading), editable by admins with the usual authorization, rate limit and audit. Design notes for the build: `AllowedSteamIds` grants the right to install mods on the server, so an admin can extend it to others; the row says so, every change is audited with the before and after lists, an admin can never remove the last entry that is themselves by accident without a confirmation, and `[RemoteInstall] Enabled` turned off from the UI asks for confirmation because it also cuts off the admin making the change. Moves these four (five values) from cfg-only to Server settings in the inventory.
   - **Every admin edits the Operator rows (decided 2026-10-06).** Any authorized admin of the server (admin list or `adminauth`) may change every Operator row, `remote-install` and `allowed-steam-ids` included, whether or not the install list names them: "we want admins to be able to admin." A restriction to admins the install list allows was built on 2026-10-05 and reverted on 2026-10-06. What stays: the confirmations (**Turn off remote installs?**, **Remove yourself from the list?**), the audit lines with the before and after values, and the lock list. An operator who wants these rows frozen names them in `[ServerSettings] LockedInGame` (`operator`, or the setting ids `progenitor/JoinAdmin/remote-install` and `…/allowed-steam-ids`).
   - **Future: role-based access (RBAC).** Today every admin is equal. Per-role permissions, such as who may change the Operator rows or who may install mods, are planned, not built. Until then the config file's lock list is the operator's control.
   - **Added the same day (on request):** first `[RemoteInstall] AllowedSteamIdsInGame` for the install list alone, then, generalized, **any server setting can be made read-only in the game by the server's config file** ([locking](#locking-settings-in-the-servers-config-file)). The switch stays as a deprecated alias.
9. **Locking (requested 2026-10-05):** one cfg-only operator key, `[ServerSettings] LockedInGame`, lists the settings an operator keeps out of the game. It is never drawn or editable in the UI, is read live, and is enforced by the server on every edit path. A locked row is read-only with its value (the password stays hidden) and `Locked in the server's config file.`.

## Open questions

All answered; see [Decisions](#decisions-user-2026-10-05).

1. **Uninstall Progenitor from this client.** Move it to Progenitor's page under Your settings (recommended: it is about Progenitor itself), or keep it on the Configuration tab?
2. **Name of the vanilla subheading:** "Server rules · applied at the next restart" (recommended), "Game server settings", "Join Game listing" or "Vanilla server settings"? Is Server name under **Server list** right?
3. **Install filters and sort** are session memory today, not settings. Add a **Remember Install filters and sort** switch on the Configuration tab, or leave them per session?
4. **A Gameplay subheading under Server settings** for `[PhotoMode] AllowOnThisServer`. Should it exist? Should `[Ai] AllySafeHits` join it, or stay cfg-only?
5. **Non-admins and the admin-channel rows.** The mid-session choice, the seconds and can't-be-checked travel on the admin-only `'PrJa'` channel, so non-admins do not see them. Hide them from non-admins (recommended, as Players joining is), or publish them read-only?
6. **`GameSettingsPreset`:** keep the Game rows read-only while a preset is set (recommended), or offer **Use custom settings**, which clears the preset at the next start?
7. **The Install tab mirror** of Server settings (stage 8): wanted?
8. **Operator settings in the UI:** should `[RemoteInstall] Enabled` / `AllowedSteamIds` or the three notice-timing values ever be drawn? (Answered: yes, decision 8.)

## Three homes build

Stages of [Three homes](#three-homes-2026-10-06). **Built (Debug and Release) and game-free tested; not live-verified.** What is built is described in [mods-menu.md](mods-menu.md#where-progenitors-settings-are-drawn).

1. **The mockup** (this page).
2. **Tabs and homes.** `ModsTab.Preferences` (was `Configuration`, same value) and `ModsTab.Server`; the strip order Mods · Install Mods · Keybinds · Preferences · Server (`ModsTabStrip.Order`); `select_tab` accepts `server`, `preferences` and the alias `configuration`; the history's place words `preferences[@group]` and `server[@group]`. Preferences: the title with the red rule, the purpose line and its **Mods »** / **Server »** links (`link-purpose`), the four groups. Progenitor's page: Your settings (Keys, Photo mode, Build menu, Private games; UI until the [trim](#trim-2026-10-06)) and **On this server** (the two toggles and `link-server`). `SettingPlaces` homes `preferences`, `server-tab/<group>`, `progenitor-page/<group>`. The tab line's ornament is one whole copy centered on the line (`WorkspaceHostLayout.Trim`), with `trimNotch.flairCenterX` / `lineCenterX` on `mods_ui tabs` and `lineRect` beside `notchRect` in `status.hosts.windowRects`.
3. **The Server tab.** `ServerTabPane` over the Server tab's groups of Progenitor's registration (`ProgenitorPage.SectionsFor`); the header, the not-connected, no-Progenitor and waiting lines, the non-admin `Admin only` rows and the admin-only group note; redrawn on the server's answers; the Install tab's link opens it.
4. **Drawers and the left list.** `SettingsDrawers` (more than four settings; `[Ui] ClosedDrawers`), the drawer header (`ModSettingsScopes.DrawerRow`) and the in-place open/close in `ModSettingsView` (`DrawerScope` set on the three homes only), `SectionNav` (slugs, the group at the top, the jump's held highlight), the left lists of Preferences, Server and Progenitor's page (sub-rows under its Mods-list entry), the per-frame highlight (`ModsMenu.TickSectionNav`, one scroll-offset read a frame), the history steps, and the bridge ops `section` and `drawer`.
5. **Live acceptance:** not run ([bridge-ops-ui.md](bridge-ops-ui.md#recipe-three-homes)).

Tests: `ThreeHomesModelTests` (one home per setting, the two homes of Progenitor's registration, the drawer threshold and stored state, the group words, the highlight rules) and `ThreeHomesMenuTests` (Preferences subheadings and left list, the drawer header and its in-place re-layout, the Server tab's drawers, back and forward, the Server tab as admin, as a player who is not an admin, not connected, without Progenitor and while waiting, the lock list on both homes, Progenitor's left list, the bridge's parsing), with the earlier suites updated (`ConfigurationOrganizationTests`, `ProgenitorPageTests`, `ServerSettingsPageTests`, `SettingPlacesTests`, `ModsTabsTests`, `WorkspaceHostsParityTests` and the navigation suites).

Choices made while building (the approved wording leaves them open):

- **Admin-channel rows stay hidden from non-admins** (decision 5 of 2026-10-05): the server sends them only to admins, so `Admin only · <value>` applies to the ConfigSync rows and the server rules; a group made only of admin-channel rows shows one `Only an admin of this server sees these settings.` line instead. **On this server** on Progenitor's page shows only its link to a non-admin.
- **If mods can't be checked** moved from Players without Progenitor to **Mods players need** (the requested list puts it there). The lock list still counts it in `no-progenitor` as well, so an operator's list from before keeps locking it.
- **The ornament** was first centered on the line itself (under the Preferences tab at 1664 units wide); it was then requested centered in the dead space right of the last tab, which is round 13's split layout (`OrnamentCentered = false`; the centered variant stays available as `Trim(frame, n, centered: true)`).
- **`»`, not `›`**, on the link buttons, as the existing links do (the game font is not known to have `›`).
- **The drawer count** is the settings a player can see in the group now (an admin's Operator reads 5; a hidden row is not counted); the more-than-four rule counts the settings the group declares, so a group does not turn into a drawer and back as rows show and hide.
- **A left-list jump to Progenitor's groups** opens the Required group of the Mods list, since its groups are listed under its entry there.
